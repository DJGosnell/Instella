using System.Text;

namespace Instella.Core.Platform.Windows;

/// <summary>
/// Builds a Windows command line that <c>CommandLineToArgvW</c> (and the .NET runtime's
/// argument parser) splits back into exactly the given arguments. Used wherever an argument
/// list must travel as one string: the UAC <c>runas</c> relaunch (<c>ProcessStartInfo.Arguments</c>
/// with <c>UseShellExecute</c>) and the SDK's updater launch.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>Quotes and joins <paramref name="args"/> with single spaces.</summary>
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>
    /// Quotes one argument when it is empty or contains whitespace or a quote. Inside quotes,
    /// backslashes are literal unless they precede a quote: before a quote, <c>2n+1</c>
    /// backslashes are emitted followed by the quote; at the end, trailing backslashes are
    /// doubled so they do not escape the closing quote.
    /// </summary>
    public static string Quote(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return arg;

        var sb = new StringBuilder(arg.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            else
                sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
