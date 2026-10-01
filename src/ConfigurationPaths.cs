using System;
using System.IO;

internal static class ConfigurationPaths
{
    internal static string ResolveHome(string configured, string userProfile)
    {
        if (String.IsNullOrWhiteSpace(userProfile)) throw new InvalidOperationException("无法确定当前 Windows 用户目录。");
        if (String.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(Path.Combine(userProfile, ".codex"));
        string home = configured.Trim();
        if (!Path.IsPathRooted(home) || ((home.StartsWith("\\") || home.StartsWith("/")) && !home.StartsWith("\\\\")) || home.Length < 3 ||
            (home.Length > 1 && home[1] == ':' && home[2] != '\\' && home[2] != '/'))
            throw new InvalidOperationException("CODEX_HOME 必须是绝对目录，请修正该环境变量后重新启动助手。");
        return Path.GetFullPath(home);
    }
}
