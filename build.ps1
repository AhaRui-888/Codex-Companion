param(
    [switch]$Test,
    [switch]$Package,
    [string]$OutputDirectory = '',
    [string]$IntermediateDirectory = '',
    [ValidateSet('all', 'ui', 'advertisements', 'gallery', 'restart', 'update', 'relay', 'account')]
    [string]$TestSuite = 'all'
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x is required.' }
# Pin the official SDK and verify the complete NuGet package before extracting
# the four redistributable files. Build artifacts stay out of source control.
$webViewVersion = '1.0.4258.31'
$webViewPackageHash = 'HlGVwvyP/IWiXAU8K57Vmg59khY3DWMOxMnH4AHusFRy0/vDAjKItXUIVNXiXXaq4CLfSZyNUic1wOIlnPLsPQ=='
$webViewDependency = Join-Path $taskRoot ('.dependencies\webview2\' + $webViewVersion)
$webViewPackage = Join-Path $webViewDependency 'sdk.nupkg'
New-Item -ItemType Directory -Force -Path $webViewDependency | Out-Null
function Test-WebViewPackage([string]$Path) {
    $taskHasher = [Security.Cryptography.SHA512]::Create()
    $taskStream = [IO.File]::OpenRead($Path)
    try { return [Convert]::ToBase64String($taskHasher.ComputeHash($taskStream)) -ceq $webViewPackageHash }
    finally { $taskStream.Dispose(); $taskHasher.Dispose() }
}
if (!(Test-Path -LiteralPath $webViewPackage)) {
    $webViewDownload = Join-Path $webViewDependency ('sdk.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $taskProtocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = $taskProtocol -bor [Net.SecurityProtocolType]::Tls12
        $webViewPackageUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/' + $webViewVersion + '/microsoft.web.webview2.' + $webViewVersion + '.nupkg'
        Invoke-WebRequest -UseBasicParsing -Uri $webViewPackageUrl -OutFile $webViewDownload
        if (!(Test-WebViewPackage $webViewDownload)) { throw 'Downloaded WebView2 SDK package failed its pinned SHA-512 check.' }
        Move-Item -LiteralPath $webViewDownload -Destination $webViewPackage
    }
    finally {
        [Net.ServicePointManager]::SecurityProtocol = $taskProtocol
        if (Test-Path -LiteralPath $webViewDownload) { Remove-Item -LiteralPath $webViewDownload -Force }
    }
}
if (!(Test-WebViewPackage $webViewPackage)) {
    throw ('Cached WebView2 SDK package failed its pinned SHA-512 check. Remove only this file and rebuild: ' + $webViewPackage)
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$webViewFiles = @{
    'Microsoft.Web.WebView2.Core.dll' = 'lib/net462/Microsoft.Web.WebView2.Core.dll'
    'Microsoft.Web.WebView2.WinForms.dll' = 'lib/net462/Microsoft.Web.WebView2.WinForms.dll'
    'WebView2Loader.x86.dll' = 'runtimes/win-x86/native/WebView2Loader.dll'
    'WebView2Loader.x64.dll' = 'runtimes/win-x64/native/WebView2Loader.dll'
}
$webViewArchive = [IO.Compression.ZipFile]::OpenRead($webViewPackage)
try {
    foreach ($webViewFile in $webViewFiles.GetEnumerator()) {
        $webViewEntry = $webViewArchive.GetEntry($webViewFile.Value)
        if ($null -eq $webViewEntry) { throw ('Pinned SDK is missing: ' + $webViewFile.Value) }
        $webViewSource = $webViewEntry.Open()
        $webViewTarget = [IO.File]::Open((Join-Path $webViewDependency $webViewFile.Key), [IO.FileMode]::Create, [IO.FileAccess]::Write)
        try { $webViewSource.CopyTo($webViewTarget) }
        finally { $webViewTarget.Dispose(); $webViewSource.Dispose() }
    }
}
finally { $webViewArchive.Dispose() }
$intermediate = if ([String]::IsNullOrWhiteSpace($IntermediateDirectory)) {
    Join-Path $taskRoot '.build'
} elseif ([IO.Path]::IsPathRooted($IntermediateDirectory)) {
    [IO.Path]::GetFullPath($IntermediateDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $taskRoot $IntermediateDirectory))
}
$version = (Get-Content -LiteralPath (Join-Path $taskRoot 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'version.txt must contain major.minor.patch.' }
$assemblyVersion = $version + '.0'
$versionSource = Join-Path $intermediate 'VersionInfo.cs'
$output = if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $taskRoot 'bin'
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $intermediate,$output | Out-Null
Set-Content -LiteralPath $versionSource -Encoding ASCII -Value @(
    '[assembly: System.Reflection.AssemblyTitle("Codex Companion")]'
    '[assembly: System.Reflection.AssemblyProduct("Codex Companion")]'
    ('[assembly: System.Reflection.AssemblyVersion("' + $assemblyVersion + '")]')
    ('[assembly: System.Reflection.AssemblyFileVersion("' + $assemblyVersion + '")]')
)
$updater = Join-Path $intermediate 'CodexCompanion.Updater.exe'
& $compiler /nologo /target:exe /optimize+ /platform:anycpu /langversion:5 "/out:$updater" (Join-Path $taskRoot 'src\UpdaterProgram.cs')
if ($LASTEXITCODE -ne 0) { throw 'Updater compilation failed.' }
$sources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | Where-Object { $_.Name -ne 'UpdaterProgram.cs' } | Select-Object -ExpandProperty FullName
$sources += $versionSource
$references = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll')
$references += '/r:' + (Join-Path $webViewDependency 'Microsoft.Web.WebView2.Core.dll')
$references += '/r:' + (Join-Path $webViewDependency 'Microsoft.Web.WebView2.WinForms.dll')
$framework = Split-Path $compiler
foreach ($assembly in @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll')) {
    $references += '/r:' + (Join-Path $framework ('WPF\' + $assembly))
}
$resources = @("/resource:$updater,CodexCompanionUpdater.exe", "/resource:$(Join-Path $taskRoot 'assets\qq-skin.css'),qq-skin.css")
$resources += '/resource:' + (Join-Path $webViewDependency 'Microsoft.Web.WebView2.Core.dll') + ',WebView2.Microsoft.Web.WebView2.Core.dll'
$resources += '/resource:' + (Join-Path $webViewDependency 'Microsoft.Web.WebView2.WinForms.dll') + ',WebView2.Microsoft.Web.WebView2.WinForms.dll'
$resources += '/resource:' + (Join-Path $webViewDependency 'WebView2Loader.x86.dll') + ',WebView2.Loader.x86'
$resources += '/resource:' + (Join-Path $webViewDependency 'WebView2Loader.x64.dll') + ',WebView2.Loader.x64'
$executable = Join-Path $output 'CodexCompanion.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /langversion:5 "/win32manifest:$(Join-Path $taskRoot 'app.manifest')" "/win32icon:$(Join-Path $taskRoot 'assets\codex-companion.ico')" "/out:$executable" @references @resources @sources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
$offlineSource = Join-Path $taskRoot 'assets\theme-gallery'
if (Test-Path -LiteralPath $offlineSource) {
    $offlineTarget = Join-Path $output 'theme-gallery'
    New-Item -ItemType Directory -Force -Path $offlineTarget | Out-Null
    Copy-Item -Path (Join-Path $offlineSource '*') -Destination $offlineTarget -Recurse -Force
}
Write-Output "Built: $executable"
if ($Test) {
    $testSources = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'tests') -Filter '*.cs' |
        Where-Object { $_.Name -notlike '*.temp.cs' } |
        Select-Object -ExpandProperty FullName
    $testBackend = $sources | Where-Object { (Split-Path $_ -Leaf) -ne 'Program.cs' }
    $runner = Join-Path $intermediate 'Regression.exe'
    & $compiler /nologo /target:exe /optimize+ /platform:anycpu /langversion:5 "/win32manifest:$(Join-Path $taskRoot 'app.manifest')" "/out:$runner" @references @resources @testBackend @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    $testDirectory = Join-Path $taskRoot ('test-output\' + (Get-Date -Format 'yyyyMMdd-HHmmss-ffff'))
    if ($TestSuite -eq 'all') { & $runner $testDirectory }
    else { & $runner $testDirectory ('--' + $TestSuite) }
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
}
if ($Package) {
    $distribution = [IO.Path]::GetFullPath((Join-Path $taskRoot 'dist'))
    if (![String]::Equals((Split-Path $distribution -Parent), [IO.Path]::GetFullPath($taskRoot), [StringComparison]::OrdinalIgnoreCase) -or
        ![String]::Equals((Split-Path $distribution -Leaf), 'dist', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to package outside the project dist directory.'
    }
    if (Test-Path -LiteralPath $distribution) { Remove-Item -LiteralPath $distribution -Recurse -Force }
    $portable = Join-Path $distribution 'portable'
    $portableGallery = Join-Path $portable 'theme-gallery'
    New-Item -ItemType Directory -Force -Path $portableGallery | Out-Null
    Copy-Item -LiteralPath $executable -Destination (Join-Path $distribution 'CodexCompanion.exe')
    Copy-Item -LiteralPath $executable -Destination (Join-Path $portable 'CodexCompanion.exe')
    foreach ($galleryJson in Get-ChildItem -LiteralPath $offlineSource -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        Copy-Item -LiteralPath $galleryJson.FullName -Destination $portableGallery
    }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'assets\THIRD-PARTY-NOTICES.txt') -Destination $portable
    $hash = (Get-FileHash -LiteralPath (Join-Path $distribution 'CodexCompanion.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $distribution 'CodexCompanion.exe.sha256') -Value "$hash  CodexCompanion.exe" -Encoding ASCII
    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $distribution 'CodexCompanion-Windows.zip')
    Write-Output "Packaged: $distribution"
}
