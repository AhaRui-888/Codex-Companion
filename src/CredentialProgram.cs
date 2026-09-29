using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Dedicated console subsystem: stdout is used only by Codex's credential pipe.
internal static class CredentialProgram
{
    static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--credential" || !Path.IsPathRooted(args[1])) return 2;
        byte[] secret = null;
        try
        {
            if (new FileInfo(args[1]).Length > 32768) return 3;
            secret = ProtectedData.Unprotect(File.ReadAllBytes(args[1]), null, DataProtectionScope.CurrentUser);
            string key = new UTF8Encoding(false, true).GetString(secret);
            if (String.IsNullOrWhiteSpace(key) || key.IndexOfAny(new[] { '\r', '\n' }) >= 0) return 3;
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write(key);
            return 0;
        }
        catch { return 1; }
        finally { if (secret != null) Array.Clear(secret, 0, secret.Length); }
    }
}
