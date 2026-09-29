using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// All files are fictional and private to the test run. Desktop operations are
// substituted so testing never closes the user's running Codex session.
internal static class ConfigurationWorkflowTests
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        TestConfirmedRestore(Path.Combine(root, "consent"));
        TestPreviewRace(Path.Combine(root, "race"));
        TestUnchangedFiles(Path.Combine(root, "unchanged"));
        TestControllerLifecycle(Path.Combine(root, "controller"));
        TestConfigurationRelaunch();
        Console.WriteLine("Restore consent, encrypted recovery, race protection and configuration shutdown checks passed.");
    }

    static void TestConfigurationRelaunch()
    {
        bool configured = false;
        int starts = 0;
        ConfigurationLaunchResult result = ConfigurationLaunchWorkflow.RunAsync(
            () => { configured = true; return Task.FromResult("configured"); },
            () => { Assert(configured, "Start must follow configuration commit."); starts++; }).GetAwaiter().GetResult();
        Assert(result.Started && starts == 1, "Successful configuration must start Codex exactly once.");
        ExpectFailure(delegate { ConfigurationLaunchWorkflow.RunAsync(
            () => { throw new TimeoutException("quit timeout"); }, () => starts++).GetAwaiter().GetResult(); });
        Assert(starts == 1, "Quit or configuration failure must never launch Codex.");
        result = ConfigurationLaunchWorkflow.RunAsync(() => Task.FromResult("configured"),
            () => { throw new InvalidOperationException("activation failed"); }).GetAwaiter().GetResult();
        Assert(!result.Started && result.Message.Contains("配置已保存") && result.Message.Contains("activation failed"),
            "Launch failure must distinguish saved configuration from a successful relaunch.");
    }

    static void TestConfirmedRestore(string root)
    {
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "auth.json");
        string data = Path.Combine(root, "data");
        File.WriteAllText(target, "original-login");
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(target, Bytes("relay-login")) }, data);
        File.WriteAllText(target, "subsequent-login");
        string manifest = Path.Combine(data, "latest-restore.bin");
        string digest = PrivateFiles.Digest(File.ReadAllBytes(manifest));
        RestorePlan plan = ChangeSet.PrepareRestore(data, Allowed(target));
        Assert(plan.ConflictingFiles.Length == 1 && plan.ConflictingFiles[0] == "auth.json", "Preview must name the conflicting file only.");
        Assert(File.ReadAllText(target) == "subsequent-login", "Preview must not change files.");
        ExpectFailure(delegate { ChangeSet.CompleteRestore(plan, false); });
        Assert(PrivateFiles.Digest(File.ReadAllBytes(manifest)) == digest && File.ReadAllText(target) == "subsequent-login", "Declining must preserve files and the backup pointer.");
        ChangeSet.CompleteRestore(plan, true);
        Assert(File.ReadAllText(target) == "original-login", "Confirmed restore must recover the original login.");
        Assert(!File.Exists(manifest), "Successful restoration must consume the restore pointer.");
        bool rescueFound = false;
        foreach (string backup in Directory.GetFiles(Path.Combine(data, "backups"), "*.bin"))
        {
            byte[] encrypted = File.ReadAllBytes(backup);
            Assert(PrivateFiles.Utf8.GetString(encrypted.Select(b => b < 128 ? b : (byte)32).ToArray()).IndexOf("subsequent-login", StringComparison.Ordinal) < 0, "Rescue data must not be stored in plaintext.");
            byte[] plain = PrivateFiles.Unprotect(encrypted);
            try
            {
                FileChange[] entries = new JavaScriptSerializer().Deserialize<FileChange[]>(PrivateFiles.Utf8.GetString(plain));
                rescueFound |= entries.Any(e => e.Before != null && PrivateFiles.Utf8.GetString(Convert.FromBase64String(e.Before)) == "subsequent-login");
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        Assert(rescueFound, "The displaced login must have an encrypted rescue backup.");
    }

    static void TestPreviewRace(string root)
    {
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "config.toml");
        string second = Path.Combine(root, "auth.json");
        string data = Path.Combine(root, "data");
        File.WriteAllText(first, "before-config");
        File.WriteAllText(second, "before-auth");
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(first, Bytes("after-config")), ChangeSet.Prepare(second, Bytes("after-auth")) }, data);
        File.WriteAllText(first, "external-config");
        RestorePlan plan = ChangeSet.PrepareRestore(data, Allowed(first, second));
        File.WriteAllText(second, "changed-during-confirmation");
        ExpectFailure(delegate { ChangeSet.CompleteRestore(plan, true); });
        Assert(File.ReadAllText(first) == "external-config" && File.ReadAllText(second) == "changed-during-confirmation", "A confirmation race must leave every file untouched.");
        plan = ChangeSet.PrepareRestore(data, Allowed(first, second));
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(first, Bytes("new-operation")) }, data);
        ExpectFailure(delegate { ChangeSet.CompleteRestore(plan, true); });
        Assert(File.ReadAllText(first) == "new-operation", "A stale restore pointer must never supersede a newer operation.");
        ExpectFailure(delegate { ChangeSet.PrepareRestore(data, Allowed(second)); });
    }

    static void TestUnchangedFiles(string root)
    {
        Directory.CreateDirectory(root);
        string unchanged = Path.Combine(root, "state.json");
        string created = Path.Combine(root, "auth.json");
        string restored = Path.Combine(root, "config.toml");
        string data = Path.Combine(root, "data");
        File.WriteAllText(unchanged, "same");
        File.WriteAllText(restored, "before");
        ChangeSet.Commit(new List<FileChange> { ChangeSet.Prepare(unchanged, Bytes("same")), ChangeSet.Prepare(created, Bytes("new")), ChangeSet.Prepare(restored, Bytes("after")) }, data);
        File.WriteAllText(unchanged, "codex-updated-state");
        File.WriteAllText(restored, "before");
        RestorePlan plan = ChangeSet.PrepareRestore(data, Allowed(unchanged, created, restored));
        Assert(plan.ConflictingFiles.Length == 0, "No-op and already-restored files must not report false conflicts.");
        ChangeSet.CompleteRestore(plan, false);
        Assert(File.ReadAllText(unchanged) == "codex-updated-state" && File.ReadAllText(restored) == "before" && !File.Exists(created), "Restore must skip unchanged files and remove files created by configuration.");
    }

    static void TestControllerLifecycle(string root)
    {
        string home = Path.Combine(root, "home");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(home);
        string config = Path.Combine(home, "config.toml");
        string auth = Path.Combine(home, "auth.json");
        string original = "model = \"old-model\"\n";
        string savedOnExit = "# saved by Codex when closing\nmodel = \"old-model\"\n";
        string login = "{\"OPENAI_API_KEY\":\"sk-test-old-login\",\"account_id\":\"fictional\"}";
        File.WriteAllText(config, original);
        File.WriteAllText(auth, login);
        var controller = new AppController(data, home);
        var oldClose = CodexDesktopRestart.CloseAndWaitOverride;
        var oldFind = CodexDesktopRestart.FindOverride;
        var oldExecutable = CodexDesktopRestart.ExecutableOverride;
        try
        {
            int closes = 0;
            int closedChecks = 0;
            CodexDesktopRestart.ExecutableOverride = delegate { return "fixture.exe"; };
            CodexDesktopRestart.FindOverride = delegate(string path) { closedChecks++; return new List<CodexProcessAdapter>(); };
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; };
            ExpectFailure(delegate { controller.ApplyAsync(new UserSettings(), "invalid key").GetAwaiter().GetResult(); });
            ExpectFailure(delegate { controller.PrepareRestore(); });
            Assert(closes == 0, "Invalid input and missing backups must not close Codex.");
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; throw new TimeoutException("test close timeout"); };
            ExpectFailure(delegate { controller.ApplyAsync(new UserSettings(), "sk-test-new").GetAwaiter().GetResult(); });
            Assert(File.ReadAllText(config) == original && File.ReadAllText(auth) == login && !Directory.Exists(data), "Close failure must precede all configuration writes.");
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; File.WriteAllText(config, savedOnExit); };
            controller.ApplyAsync(new UserSettings(), "sk-test-new").GetAwaiter().GetResult();
            Assert(File.ReadAllText(config).Contains(AppController.OfficialRelayUrl) && closedChecks > 0, "Apply must write the relay after verifying desktop exit.");
            Assert(Json.Read(File.ReadAllText(auth)).Count == 1, "Apply must retain only OPENAI_API_KEY.");
            CodexDesktopRestart.CloseAndWaitOverride = delegate(CancellationToken token) { closes++; };
            RestorePlan plan = controller.PrepareRestore();
            Assert(plan.ConflictingFiles.Length == 0, "A normal apply/restore cycle must not conflict.");
            CodexDesktopRestart.FindOverride = delegate(string path) { return new List<CodexProcessAdapter> { new CodexProcessAdapter(delegate { return false; }, delegate { return IntPtr.Zero; }, delegate { throw new Exception("Must not close in final verification."); }, null, null) }; };
            ExpectFailure(delegate { controller.CompleteRestore(plan, false); });
            Assert(File.ReadAllText(config).Contains(AppController.OfficialRelayUrl), "Restart during confirmation must block restoration.");
            CodexDesktopRestart.FindOverride = delegate(string path) { return new List<CodexProcessAdapter>(); };
            controller.CompleteRestore(plan, false);
            Assert(File.ReadAllText(config) == savedOnExit && File.ReadAllText(auth) == login, "Restore must use the snapshot taken after Codex saved and exited.");
            Assert(!File.Exists(Path.Combine(data, "settings.json")) && !File.Exists(Path.Combine(data, "credential.bin")), "Files newly created by apply must be removed on restore.");
            Assert(closes == 3, "Apply and restore must each request closure once after validation.");
        }
        finally
        {
            CodexDesktopRestart.CloseAndWaitOverride = oldClose;
            CodexDesktopRestart.FindOverride = oldFind;
            CodexDesktopRestart.ExecutableOverride = oldExecutable;
        }
    }

    static byte[] Bytes(string text) { return PrivateFiles.Utf8.GetBytes(text); }
    static HashSet<string> Allowed(params string[] paths) { return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase); }
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void ExpectFailure(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (IOException) { return; } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } catch (TimeoutException) { return; }
        throw new InvalidOperationException("The operation should have been rejected.");
    }
}
