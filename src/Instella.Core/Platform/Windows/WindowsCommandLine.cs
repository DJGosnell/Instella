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

    /// <summary>
    /// Splits <paramref name="commandLine"/> into arguments as <c>CommandLineToArgvW</c> does for
    /// everything after the program name: the inverse of <see cref="Join"/>.
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArg = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < commandLine.Length && commandLine[i] == '\\') i++;
                var count = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
                hasArg = true;
                continue;
            }
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasArg = true;
                continue;
            }
            if (!inQuotes && c is ' ' or '\t' or '\n' or '\v')
            {
                if (hasArg) args.Add(current.ToString());
                current.Clear();
                hasArg = false;
                continue;
            }
            current.Append(c);
            hasArg = true;
        }
        if (hasArg) args.Add(current.ToString());
        return args;
    }
}
