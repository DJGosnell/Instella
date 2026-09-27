using System;
using System.Diagnostics.CodeAnalysis;
using Instella.Installer.Runtime.Runners;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// Reads and writes the command line stored in a Windows Run value
/// (<c>Software\Microsoft\Windows\CurrentVersion\Run</c>): an executable, optionally quoted,
/// followed by its arguments.
/// </summary>
internal static class RunCommand
{
    /// <summary>The Run key, under HKCU for per-user installs and HKLM for machine-wide ones.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Splits <paramref name="command"/> into the executable's full path and the arguments.
    /// Environment variables are expanded first (REG_EXPAND_SZ). A quoted first token is the
    /// executable; otherwise the text up to the first <c>.exe</c> followed by a space or the end
    /// (an unquoted path with spaces); otherwise the first space-delimited token. False when the
    /// executable is not a full path.
    /// </summary>
    public static bool TryGetExecutable(string? command,
        [NotNullWhen(true)] out string? exePath, [NotNullWhen(true)] out string? arguments)
    {
        exePath = null;
        arguments = null;
        if (string.IsNullOrWhiteSpace(command)) return false;

        var text = Environment.ExpandEnvironmentVariables(command).Trim();
        string exe;
        string rest;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) return false;
            exe = text[1..close];
            rest = text[(close + 1)..];
        }
        else
        {
            var end = ExeEnd(text);
            if (end < 0)
            {
                var space = text.IndexOf(' ', StringComparison.Ordinal);
                end = space < 0 ? text.Length : space;
            }
            exe = text[..end];
            rest = text[end..];
        }

        if (!InstallPaths.TryNormalize(exe, requireRooted: true, out var normalized, out _)) return false;
        exePath = normalized;
        arguments = rest.Trim();
        return true;
    }

    /// <summary>The Run value for <paramref name="exePath"/>: the quoted path, then the arguments.</summary>
    public static string Format(string exePath, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments.Trim()}";

    // Index just past the first ".exe" that ends the text or is followed by a space; -1 when none.
    private static int ExeEnd(string text)
    {
        var from = 0;
        while (true)
        {
            var i = text.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            var end = i + 4;
            if (end == text.Length || text[end] == ' ') return end;
            from = end;
        }
    }
}
