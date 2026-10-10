using System;
using System.IO;

internal sealed class AccountSessionStore
{
    internal readonly string DirectoryPath;
    internal string SessionPath { get { return Path.Combine(DirectoryPath, "website-session.bin"); } }

    internal AccountSessionStore(string directory) { DirectoryPath = Path.GetFullPath(directory); }

    internal AccountSession Load()
    {
        if (!File.Exists(SessionPath)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(SessionPath);
            if (encrypted.Length > 32768) throw new InvalidDataException();
            byte[] plain = PrivateFiles.Unprotect(encrypted);
            try
            {
                var parsed = AccountClient.ParseObject(PrivateFiles.Utf8.GetString(plain));
                var session = new AccountSession { AccessToken = Json.Text(parsed, "access"), RefreshToken = Json.Text(parsed, "refresh") };
                session.Validate();
                return session;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        catch (Exception error)
        {
            if (!(error is System.Security.Cryptography.CryptographicException) && !(error is InvalidDataException) &&
                !(error is ArgumentException) && !(error is System.Text.DecoderFallbackException)) throw;
            throw new InvalidDataException("本机登录信息不可用，请重新登录。");
        }
    }

    internal void Save(AccountSession session)
    {
        session.Validate();
        PrivateFiles.EnsureDirectory(DirectoryPath);
        byte[] plain = PrivateFiles.Utf8.GetBytes(Json.Write(new { access = session.AccessToken, refresh = session.RefreshToken }));
        try { PrivateFiles.WriteAtomic(SessionPath, PrivateFiles.Protect(plain)); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    internal void Clear() { if (File.Exists(SessionPath)) File.Delete(SessionPath); }
}
