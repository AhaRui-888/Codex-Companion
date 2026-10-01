using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class ConfigurationCompatibilityTests
{
    internal static void Run(string root, bool useInstalledCli)
    {
        Directory.CreateDirectory(root);
        CheckPaths(root);
        CheckPackageIdentity(root);
        CheckWriteVerification(root);
        CheckCredentials(root);
        CheckAtomicRetries(root);
        CheckConcurrentLock(root);
        CheckFailedCommitRollback(root);
        CheckRestorePointerRetry(root);
        CheckServiceLifetime();
        if (useInstalledCli) CheckInstalledCli(root);
        Console.WriteLine("Configuration compatibility: paths, moved package identity, write verification, credentials, file locks and rollback passed.");
    }

    static void CheckPaths(string root)
    {
        string profile = Path.Combine(root, "用户 with spaces");
        string fallback = Path.Combine(profile, ".codex");
        foreach (string empty in new string[] { null, "", "  " })
            Assert(ConfigurationPaths.ResolveHome(empty, profile) == fallback, "Empty CODEX_HOME must use the user profile.");
        string custom = Path.Combine(root, "另一个目录", "configuration home");
        Assert(ConfigurationPaths.ResolveHome(custom, profile) == custom, "An absolute custom home must be retained.");
        Assert(ConfigurationPaths.ResolveHome(@"\\server\profiles\用户\.codex", profile) == @"\\server\profiles\用户\.codex", "UNC home must be absolute.");
        foreach (string relative in new[] { "relative", @"C:relative", @"\drive-relative", "/drive-relative" })
            Reject(delegate { ConfigurationPaths.ResolveHome(relative, profile); });
    }

    static void CheckPackageIdentity(string root)
    {
        string package = Path.Combine(root, "另一安装盘", "WindowsApps", "OpenAI.Codex_26.928.2636.0_x64__fixture");
        Directory.CreateDirectory(Path.Combine(package, "app"));
        File.WriteAllText(Path.Combine(package, "AppxManifest.xml"), "<Package><Applications><Application Id='App' Executable='app/ChatGPT.exe'/></Applications></Package>");
        Assert(CodexInstall.AppUserModelIdForPath(Path.Combine(package, "app", "ChatGPT.exe")) == "OpenAI.Codex_fixture!App",
            "Package identity must not depend on C:\\Program Files.");
        Assert(CodexInstall.AppUserModelIdForPath(Path.Combine(root, "unpackaged", "Codex.exe")) == null,
            "Unpackaged desktop must retain direct startup.");
        string image = Path.Combine(package, "app", "ChatGPT.exe");
        Assert(CodexInstall.MatchesExpectedDesktop(image, image), "The detected desktop may be closed.");
        Reject(delegate { CodexInstall.MatchesExpectedDesktop(image, Path.Combine(root, "different-version", "ChatGPT.exe")); });
        Assert(!CodexInstall.MatchesExpectedDesktop(Path.Combine(root, "unrelated", "ChatGPT.exe"), image),
            "An unrelated app sharing the process name must not be targeted.");
        Assert(!CodexInstall.MatchesExpectedDesktop(Path.Combine(package, "app", "resources", "codex.exe"), image),
            "A bundled CLI or app-server must not be mistaken for another desktop version.");
    }

    static Dictionary<string, object> Table(string key, object value)
    { return new Dictionary<string, object> { { key, value } }; }

    static Dictionary<string, object> Response(string path, Dictionary<string, object> written, Dictionary<string, object> effective)
    {
        return new Dictionary<string, object> {
            { "config", effective }, { "layers", new object[] { new Dictionary<string, object> {
                { "name", new Dictionary<string, object> { { "type", "user" }, { "file", path } } }, { "config", written }
            } } }
        };
    }

    static void CheckWriteVerification(string root)
    {
        string path = Path.Combine(root, "config.toml");
        var edits = new List<object> { CodexConfig.Edit("model", "model-fixture") };
        CodexConfig.VerifyEdits(Response(path, Table("model", "model-fixture"), Table("model", "model-fixture")), path, edits);
        Reject(delegate { CodexConfig.VerifyEdits(Response(path, Table("model", "ignored"), Table("model", "model-fixture")), path, edits); });
        Reject(delegate { CodexConfig.VerifyEdits(Response(path, Table("model", "model-fixture"), Table("model", "overridden")), path, edits); });
        Reject(delegate { CodexConfig.VerifyEdits(Response(path + ".other", Table("model", "model-fixture"), Table("model", "model-fixture")), path, edits); });
        Reject(delegate { CodexConfig.VerifyEdits(Table("config", Table("model", "model-fixture")), path, edits); });
        var numericEdit = new List<object> { CodexConfig.Edit("desktop.contrast", 60) };
        CodexConfig.VerifyEdits(Response(path, Table("desktop", Table("contrast", Table("$serde_json::private::Number", "60"))),
            Table("desktop", Table("contrast", 60))), path, numericEdit);
    }

    static void CheckCredentials(string root)
    {
        string home = Path.Combine(root, "credential-home");
        Directory.CreateDirectory(home);
        var controller = new AppController(Path.Combine(root, "credential-data"), home);
        string auth = Path.Combine(home, "auth.json");
        File.WriteAllText(auth, "{\"tokens\":{\"access_token\":\"fictional\"}}");
        Assert(!controller.HasCredential, "ChatGPT login is not an API Key.");
        File.WriteAllText(auth, "{broken");
        Assert(!controller.HasCredential, "Invalid auth.json is not an API Key.");
        byte[] original = PrivateFiles.Utf8.GetBytes("\uFEFF{\"OPENAI_API_KEY\":\"sk-test-new\",\"OPEN_API_KEY\":\"sk-test-old\"}");
        Assert(AuthFile.ReadOpenApiKey(original) == "sk-test-new", "Canonical key must win over obsolete key; UTF-8 BOM must be accepted.");
        File.WriteAllBytes(auth, original);
        Assert(!controller.HasCredential, "An existing provider's API Key must not be reused for the official relay.");
        string data = Path.Combine(root, "credential-data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "credential.bin"), PrivateFiles.Protect(PrivateFiles.Utf8.GetBytes("sk-test-saved-compatibility")));
        File.WriteAllText(Path.Combine(data, "settings.json"), Json.Write(new UserSettings { BaseUrl = "https://other.example.test/v1" }));
        Assert(!controller.HasCredential, "A saved key for another endpoint must not be reused.");
        File.WriteAllText(Path.Combine(data, "settings.json"), Json.Write(new UserSettings { BaseUrl = AppController.OfficialRelayUrl }));
        Assert(controller.HasCredential, "An explicitly saved relay key must remain reusable.");
    }

    static void CheckAtomicRetries(string root)
    {
        string target = Path.Combine(root, "retry.txt");
        File.WriteAllText(target, "before");
        Exception failure = null;
        Thread worker;
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            worker = new Thread(delegate() {
                try { PrivateFiles.WriteAtomic(target, PrivateFiles.Utf8.GetBytes("after")); }
                catch (Exception error) { failure = error; }
            });
            worker.Start();
            Thread.Sleep(250);
        }
        Assert(worker.Join(5000) && failure == null && File.ReadAllText(target) == "after", "Short-lived lock must recover.");
        failure = null;
        byte[] snapshot = File.ReadAllBytes(target);
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            worker = new Thread(delegate() {
                try { PrivateFiles.WriteAtomicChecked(target, PrivateFiles.Utf8.GetBytes("must-not-overwrite"), PrivateFiles.Digest(snapshot)); }
                catch (Exception error) { failure = error; }
            });
            worker.Start();
            Thread.Sleep(250);
            byte[] external = PrivateFiles.Utf8.GetBytes("external-update");
            locked.SetLength(0);
            locked.Write(external, 0, external.Length);
            locked.Flush(true);
        }
        Assert(worker.Join(5000) && failure is IOException && File.ReadAllText(target) == "external-update", "Retries must never overwrite a concurrent edit.");
    }

    static void CheckConcurrentLock(string root)
    {
        string data = Path.Combine(root, "concurrent-data");
        Directory.CreateDirectory(data);
        string target = Path.Combine(root, "concurrent.txt");
        File.WriteAllText(target, "before");
        var changes = new List<FileChange> { ChangeSet.Prepare(target, PrivateFiles.Utf8.GetBytes("after")) };
        ChangeSet.Commit(changes, data);
        var allowed = new HashSet<string>(new[] { target }, StringComparer.OrdinalIgnoreCase);
        RestorePlan plan = ChangeSet.PrepareRestore(data, allowed);
        byte[] pointer = File.ReadAllBytes(Path.Combine(data, "latest-restore.bin"));
        using (var locked = new FileStream(Path.Combine(data, "configuration.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Reject(delegate { ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(target, PrivateFiles.Utf8.GetBytes("unexpected")) }, data); });
            Reject(delegate { ChangeSet.CompleteRestore(plan, false); });
        }
        Assert(File.ReadAllText(target) == "after" && PrivateFiles.Digest(pointer) == PrivateFiles.Digest(File.ReadAllBytes(Path.Combine(data, "latest-restore.bin"))),
            "A second instance must preserve files and the restore pointer.");
        ChangeSet.CompleteRestore(plan, false);
        Assert(File.ReadAllText(target) == "before", "Restore must succeed after the other instance releases its lock.");
    }

    static void CheckFailedCommitRollback(string root)
    {
        string data = Path.Combine(root, "transaction-data");
        string first = Path.Combine(root, "first.txt");
        string second = Path.Combine(root, "second.txt");
        File.WriteAllText(first, "first-before");
        File.WriteAllText(second, "second-before");
        var changes = new List<FileChange> {
            ChangeSet.Prepare(first, PrivateFiles.Utf8.GetBytes("first-after")),
            ChangeSet.Prepare(second, PrivateFiles.Utf8.GetBytes("second-after"))
        };
        using (var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(delegate { ChangeSet.Commit(changes, data); });
        Assert(File.ReadAllText(first) == "first-before" && File.ReadAllText(second) == "second-before", "Second-file failure must roll back the first file.");
        Assert(!File.Exists(Path.Combine(data, "latest-restore.bin")), "Failure must not report a completed operation.");
        byte[] before = File.ReadAllBytes(first);
        File.WriteAllText(first, "external");
        Reject(delegate { ChangeSet.PrepareSnapshot(first, PrivateFiles.Utf8.GetBytes("generated"), before); });
        Assert(File.ReadAllText(first) == "external", "Staging race must not overwrite external changes.");
    }

    static void CheckInstalledCli(string root)
    {
        string cli = CodexInstall.Cli();
        using (var process = Process.Start(new ProcessStartInfo(cli, "--version") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }))
        {
            var version = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("Configuration service version query timed out."); }
            Console.WriteLine("Configuration service: " + version.Result.Trim());
        }
        string home = Path.Combine(root, "全新 用户", "自定义 configuration");
        byte[] fresh = CodexConfig.Stage(cli, home, null, AppController.BuildRelayConfigEdits("sk-test-fresh-compatibility"));
        string config = PrivateFiles.Utf8.GetString(fresh);
        Assert(config.Contains(AppController.OfficialRelayUrl) && config.Contains(AppController.RelayModel), "Fresh-machine config must contain verified relay values.");
        string original = "# 用户现有设置\nmodel = \"old\"\n" +
            "[mcp_servers.fixture]\ncommand = \"fictional-tool\"\nargs = [\"中文\", \"with spaces\"]\n" +
            "[model_providers.other]\nname = \"保留服务\"\nwire_api = \"responses\"\n" +
            "[model_providers.custom]\nname = \"previous\"\nwire_api = \"responses\"\nenv_key = \"FICTIONAL_OLD_ENV_KEY\"\n";
        var edits = AppController.BuildRelayConfigEdits("sk-test-existing-compatibility");
        ThemeCatalog.AddEdits("forest", edits);
        config = PrivateFiles.Utf8.GetString(CodexConfig.Stage(cli, home, PrivateFiles.Utf8.GetBytes(original), edits));
        Assert(config.Contains("# 用户现有设置") && config.Contains("fictional-tool") && config.Contains("保留服务") && config.Contains("with spaces"), "Existing unrelated config must be preserved.");
        Assert(!config.Contains("FICTIONAL_OLD_ENV_KEY"), "Old provider credential sources must be removed.");
        Reject(delegate { CodexConfig.Stage(cli, home, PrivateFiles.Utf8.GetBytes("this is invalid TOML = ["), edits); });
        string staging = Path.Combine(home, "staging");
        Assert(!Directory.Exists(staging) || Directory.GetDirectories(staging).Length == 0, "Success and failure must clean private staging directories.");
    }

    static void CheckRestorePointerRetry(string root)
    {
        string data = Path.Combine(root, "restore-retry-data");
        string target = Path.Combine(root, "restore-retry.txt");
        File.WriteAllText(target, "original");
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(target, PrivateFiles.Utf8.GetBytes("configured")) }, data);
        var allowed = new HashSet<string>(new[] { target }, StringComparer.OrdinalIgnoreCase);
        RestorePlan plan = ChangeSet.PrepareRestore(data, allowed);
        string pointer = Path.Combine(data, "latest-restore.bin");
        byte[] originalPointer = File.ReadAllBytes(pointer);
        using (var locked = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(delegate { ChangeSet.CompleteRestore(plan, false); });
        Assert(File.ReadAllText(target) == "original" && PrivateFiles.Digest(File.ReadAllBytes(pointer)) == PrivateFiles.Digest(originalPointer),
            "Failure deleting the restore pointer must retain the original recovery intent.");
        ChangeSet.CompleteRestore(ChangeSet.PrepareRestore(data, allowed), false);
        Assert(File.ReadAllText(target) == "original" && !File.Exists(pointer), "Retry must finish restoration without undoing it.");
    }

    static void CheckServiceLifetime()
    {
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        // Wait for an explicit signal after job attachment before creating a
        // child. This helper never connects to the user's Codex desktop.
        string script = "$null = [Console]::ReadLine(); $p = Start-Process -FilePath $PSHOME\\powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60' -PassThru; [Console]::WriteLine($p.Id); Start-Sleep -Seconds 60";
        using (var parent = Process.Start(new ProcessStartInfo(shell, "-NoProfile -NonInteractive -Command \"" + script + "\"") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }))
        {
            Process child = null;
            try
            {
                using (var lifetime = new ConfigurationServiceLifetime())
                {
                    lifetime.Attach(parent);
                    parent.StandardInput.WriteLine("start");
                    parent.StandardInput.Flush();
                    var line = parent.StandardOutput.ReadLineAsync();
                    Assert(line.Wait(10000), "Owned helper child must start.");
                    int id;
                    Assert(Int32.TryParse(line.Result, out id), "Owned helper must report its child PID.");
                    child = Process.GetProcessById(id);
                }
                Assert(parent.WaitForExit(5000) && child.WaitForExit(5000), "Closing the staging job must release parent and descendant processes.");
            }
            finally
            {
                if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(3000); }
                if (child != null)
                {
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                    child.Dispose();
                }
            }
        }
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unsafe or incompatible operation was accepted.");
    }

    static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
