using System;
using System.IO;

internal static class AutoUpdaterTests
{
    internal static void Run()
    {
        Assert(AutoUpdater.ParseReleaseVersion("v1.2.3") == new Version(1, 2, 3), "stable tag");
        Reject(delegate { AutoUpdater.ParseReleaseVersion("1.2.3"); });
        Reject(delegate { AutoUpdater.ParseReleaseVersion("v1.2.3.4"); });
        string hash = new String('a', 64);
        Assert(AutoUpdater.ParseHash(hash + "  CodexCompanion.exe") == hash, "hash parse");
        Reject(delegate { AutoUpdater.ParseHash(hash + "  another.exe"); });
        Reject(delegate { AutoUpdater.ParseHash("abc  CodexCompanion.exe"); });
        string url = "https://github.com/AhaRui-888/Codex-Companion/releases/download/v1.2.3/CodexCompanion.exe";
        AutoUpdater.ValidateAssetUrl(url, "v1.2.3", AutoUpdater.ExecutableName);
        Reject(delegate { AutoUpdater.ValidateAssetUrl(url.Replace("github.com", "example.com"), "v1.2.3", AutoUpdater.ExecutableName); });
        Reject(delegate { AutoUpdater.ValidateAssetUrl(url + "?other=1", "v1.2.3", AutoUpdater.ExecutableName); });
        Reject(delegate { AutoUpdater.ValidateAssetUrl(url.Replace("v1.2.3", "v1.2.4"), "v1.2.3", AutoUpdater.ExecutableName); });
        UpdateRelease release = new UpdateRelease { tag_name = "v1.2.3", assets = new[] {
            new UpdateAsset { name = AutoUpdater.ExecutableName, browser_download_url = url, size = 1024 }
        } };
        Assert(AutoUpdater.FindAsset(release, AutoUpdater.ExecutableName).size == 1024, "release asset");
        release.assets = new[] { release.assets[0], release.assets[0] };
        Reject(delegate { AutoUpdater.FindAsset(release, AutoUpdater.ExecutableName); });
        release.prerelease = true;
        Reject(delegate { AutoUpdater.FindAsset(release, AutoUpdater.ExecutableName); });
        Console.WriteLine("Updater: version, digest, release asset, and repository URL validation passed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid update input was accepted.");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Update test failed: " + name);
    }
}
