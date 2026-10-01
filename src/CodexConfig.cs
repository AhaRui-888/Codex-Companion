using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Use the installed Codex TOML writer instead of editing TOML with regular expressions.
// The child works in a private staging CODEX_HOME. It never writes the live configuration.
public static class CodexConfig
{
    public static object Edit(string key, object value)
    { return new { keyPath = key, value = value, mergeStrategy = "replace" }; }

    public static byte[] Stage(string cli, string dataDirectory, byte[] original, List<object> edits)
    {
        string scratch = Path.Combine(dataDirectory, "staging", Guid.NewGuid().ToString("N"));
        PrivateFiles.EnsureDirectory(scratch);
        string path = Path.Combine(scratch, "config.toml");
        try
        {
            if (original != null) File.WriteAllBytes(path, original);
            var info = new ProcessStartInfo(cli, "app-server --listen stdio:// -c analytics.enabled=false")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                // An isolated cwd avoids unrelated project-scoped .codex
                // overrides from the user's TEMP directory or its parents.
                WorkingDirectory = scratch
            };
            info.EnvironmentVariables["CODEX_HOME"] = scratch;
            using (var lifetime = new ConfigurationServiceLifetime())
            using (var process = Process.Start(info))
            // .NET Framework's StandardInput defaults to the Windows code page
            // (e.g. GB2312). JSON-RPC requires UTF-8, including Chinese file paths.
            // Do not emit a BOM before the first JSON message.
            using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                lifetime.Attach(process);
                // Drain diagnostics without logging: a user's configuration may contain secrets.
                var diagnostics = process.StandardError.ReadToEndAsync();
                try
                {
                    Request(process, input, 1, "initialize", new { clientInfo = new { name = "midweb_codex_companion", version = "1.0.0" } });
                    input.WriteLine(Json.Write(new { method = "initialized" }));
                    input.Flush();
                    var response = Request(process, input, 2, "config/read", new { includeLayers = true });
                    object value;
                    var config = response.TryGetValue("config", out value) ? value as Dictionary<string, object> : null;
                    if (config != null && !String.IsNullOrEmpty(Json.Text(config, "profile")))
                        throw new InvalidOperationException("当前 Codex 启用了独立 profile，暂不自动覆盖，请先使用默认配置。");
                    Request(process, input, 3, "config/batchWrite", new { edits = edits, filePath = path, reloadUserConfig = true });
                    // A read after writing verifies that Codex can load the generated TOML.
                    var verified = Request(process, input, 4, "config/read", new { includeLayers = true });
                    VerifyEdits(verified, path, edits);
                    return File.ReadAllBytes(path);
                }
                finally
                {
                    input.Close();
                    if (!process.WaitForExit(3000)) process.Kill();
                    process.WaitForExit(3000);
                }
            }
        }
        finally
        {
            // Only our known fresh GUID directory is eligible for cleanup.
            string root = Path.GetFullPath(Path.Combine(dataDirectory, "staging")) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(scratch);
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                DeleteScratch(resolved);
        }
    }

    internal static void VerifyEdits(Dictionary<string, object> response, string configPath, List<object> edits)
    {
        object rawConfig;
        var effective = response.TryGetValue("config", out rawConfig) ? rawConfig as Dictionary<string, object> : null;
        object rawLayers;
        var layers = response.TryGetValue("layers", out rawLayers) ? rawLayers as object[] : null;
        Dictionary<string, object> written = null;
        if (layers != null) foreach (object layer in layers)
        {
            var row = layer as Dictionary<string, object>;
            object rawName, rawValues;
            if (row == null || !row.TryGetValue("name", out rawName)) continue;
            var name = rawName as Dictionary<string, object>;
            if (name == null || Json.Text(name, "type") != "user") continue;
            string file = Json.Text(name, "file");
            if (file != null && String.Equals(Path.GetFullPath(file), Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase) &&
                row.TryGetValue("config", out rawValues)) written = rawValues as Dictionary<string, object>;
        }
        if (effective == null || written == null) throw new InvalidDataException("无法确认 Codex 实际读取了目标配置文件；原配置未修改。");
        foreach (object item in edits)
        {
            var edit = Json.Read(Json.Write(item));
            string key = Json.Text(edit, "keyPath");
            object expected = edit["value"];
            object actual;
            if (!TryValue(written, key, out actual) || !EqualValue(expected, actual))
                throw new InvalidDataException("Codex 配置写入校验失败（" + key + "）；原配置未修改。");
            // Some legacy passthrough keys are not exposed by config/read's
            // typed config. The user layer above still verifies their TOML.
            if (TryValue(effective, key, out actual) && !EqualValue(expected, actual))
                throw new InvalidDataException("Codex 的有效配置被其他配置层覆盖（" + key + "）；原配置未修改。");
        }
    }

    static bool TryValue(Dictionary<string, object> values, string key, out object value)
    {
        value = values;
        foreach (string part in key.Split('.'))
        {
            var table = value as Dictionary<string, object>;
            if (table == null || !table.TryGetValue(part, out value)) return false;
        }
        return true;
    }

    static bool EqualValue(object expected, object actual)
    {
        // config/read's raw layers may encode JSON numbers using serde_json's
        // arbitrary-precision wrapper; effective config uses normal numbers.
        var number = actual as Dictionary<string, object>;
        if (number != null && number.Count == 1 && number.ContainsKey("$serde_json::private::Number") &&
            expected != null && !(expected is string) && !(expected is bool))
            return Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture) ==
                Convert.ToString(number["$serde_json::private::Number"], System.Globalization.CultureInfo.InvariantCulture);
        var table = expected as Dictionary<string, object>;
        if (table != null)
        {
            var other = actual as Dictionary<string, object>;
            if (other == null) return false;
            foreach (var pair in table)
            {
                object value;
                if (!other.TryGetValue(pair.Key, out value) || !EqualValue(pair.Value, value)) return false;
            }
            return true;
        }
        var array = expected as object[];
        if (array != null)
        {
            var other = actual as object[];
            if (other == null || array.Length != other.Length) return false;
            for (int index = 0; index < array.Length; index++) if (!EqualValue(array[index], other[index])) return false;
            return true;
        }
        return Object.Equals(expected, actual) ||
            (expected != null && actual != null && !(expected is string) && !(actual is string) &&
                Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture) == Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture));
    }

    static void DeleteScratch(string path)
    {
        // Codex may release inherited handles a moment after its process exits.
        // Retry briefly so a transient Windows lock cannot turn a successful
        // configuration write into a user-visible failure.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, true);
                return;
            }
            catch (IOException error) { if (attempt == 0 || attempt == 49) Debug.WriteLine("Staging cleanup " + error.GetType().Name + " 0x" + error.HResult.ToString("X8")); Thread.Sleep(100); }
            catch (UnauthorizedAccessException error) { if (attempt == 0 || attempt == 49) Debug.WriteLine("Staging cleanup " + error.GetType().Name + " 0x" + error.HResult.ToString("X8")); Thread.Sleep(100); }
        }
        // Cleanup is best-effort after the child has exited. The next run uses
        // a fresh GUID, and no live configuration file has been touched here.
    }
    static Dictionary<string, object> Request(Process process, TextWriter input, int id, string method, object parameters)
    {
        input.WriteLine(Json.Write(new { id = id, method = method, @params = parameters }));
        input.Flush();
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 20000)
        {
            var next = process.StandardOutput.ReadLineAsync();
            if (!next.Wait((int)Math.Max(1, 20000 - timer.ElapsedMilliseconds))) throw new TimeoutException("Codex 配置服务超时，请更新 Codex 后重试。");
            string line = next.Result;
            if (line == null) throw new InvalidOperationException("Codex 配置服务提前退出，原配置没有修改。");
            var item = Json.Read(line);
            object itemId;
            if (!item.TryGetValue("id", out itemId) || Convert.ToString(itemId) != id.ToString()) continue;
            if (item.ContainsKey("error")) throw new InvalidOperationException("Codex 拒绝了配置操作（" + method + "）。请检查 Codex 版本和现有配置；原配置未改动。");
            object result;
            if (!item.TryGetValue("result", out result) || !(result is Dictionary<string, object>)) throw new InvalidDataException("Codex 配置服务返回了不兼容的数据。");
            return (Dictionary<string, object>)result;
        }
        throw new TimeoutException("Codex 配置服务超时。");
    }
}
