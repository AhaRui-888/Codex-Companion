using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

// The managed SDK is loaded from the EXE so an EXE-only update remains complete.
// Only the architecture-specific native loader is extracted to our private data.
internal static class WebViewRuntime
{
    internal const string SdkVersion = "1.0.4258.31";
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
    private static bool installed;

    internal static void InstallAssemblyResolver()
    {
        lock (Gate)
        {
            if (installed) return;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            installed = true;
        }
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms") return null;
        lock (Gate)
        {
            Assembly assembly;
            if (Loaded.TryGetValue(name, out assembly)) return assembly;
            assembly = Assembly.Load(PrivateFiles.Resource("WebView2." + name + ".dll"));
            Loaded.Add(name, assembly);
            return assembly;
        }
    }

    internal static string PrepareLoaderDirectory(string dataDirectory)
    {
        string architecture = Environment.Is64BitProcess ? "x64" : "x86";
        string directory = Path.Combine(Path.GetFullPath(dataDirectory), "webview2-sdk", SdkVersion, architecture);
        PrivateFiles.EnsureDirectory(directory);
        string path = Path.Combine(directory, "WebView2Loader.dll");
        byte[] bytes = PrivateFiles.Resource("WebView2.Loader." + architecture);
        if (!File.Exists(path) || PrivateFiles.Digest(File.ReadAllBytes(path)) != PrivateFiles.Digest(bytes))
            PrivateFiles.WriteAtomic(path, bytes);
        return directory;
    }

    internal static string CreateProfileDirectory(string dataDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(dataDirectory), "login-profiles");
        PrivateFiles.EnsureDirectory(root);
        // Old, abandoned profiles are never reused. Clear leftovers from a crash.
        foreach (string path in Directory.GetDirectories(root))
        {
            Guid identifier;
            if (Guid.TryParseExact(Path.GetFileName(path), "N", out identifier) &&
                Directory.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                DeleteProfileLater(root, path);
        }
        string profile = Path.Combine(root, Guid.NewGuid().ToString("N"));
        PrivateFiles.EnsureDirectory(profile);
        return profile;
    }

    internal static void DeleteProfileLater(string root, string profile)
    {
        string resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string resolvedProfile = Path.GetFullPath(profile);
        Guid identifier;
        if (!String.Equals(Path.GetDirectoryName(resolvedProfile), resolvedRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(resolvedProfile), "N", out identifier))
            throw new InvalidOperationException("拒绝清理不属于登录窗口的浏览器数据目录。");
        Task.Run(async delegate
        {
            // Closing the control releases Chromium's locks asynchronously.
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    if (Directory.Exists(resolvedProfile)) Directory.Delete(resolvedProfile, true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(500).ConfigureAwait(false);
            }
        });
    }
}
