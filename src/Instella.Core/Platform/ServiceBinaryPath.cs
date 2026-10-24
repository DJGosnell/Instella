using Instella.Core.Platform.Windows;

namespace Instella.Core.Platform;

/// <summary>Builds the image path a service is registered with.</summary>
internal static class ServiceBinaryPath
{
    /// <summary>
    /// The quoted executable, then the arguments quoted like a command line
    /// (<see cref="WindowsCommandLine.Join"/>). The executable is always quoted, even without
    /// spaces, so the SCM never searches <c>C:\Program.exe</c>-style prefixes.
    /// </summary>
    /// <exception cref="ArgumentException">The executable path is empty or contains a quote.</exception>
    public static string Build(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (executablePath.Length == 0)
            throw new ArgumentException("The service executable path is empty.", nameof(executablePath));
        if (executablePath.Contains('"', StringComparison.Ordinal))
            throw new ArgumentException($"The service executable path contains a quote: {executablePath}", nameof(executablePath));

        var exe = "\"" + executablePath + "\"";
        return arguments.Count == 0 ? exe : exe + " " + WindowsCommandLine.Join(arguments);
    }

    /// <summary>
    /// Splits an image path read back from the service manager into the executable and its
    /// arguments: the inverse of <see cref="Build"/>. An unquoted executable ends at the first space.
    /// </summary>
    public static (string ExecutablePath, IReadOnlyList<string> Arguments) Parse(string binaryPath)
    {
        ArgumentNullException.ThrowIfNull(binaryPath);
        var text = binaryPath.TrimStart();
        string exe, rest;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            exe = close < 0 ? text[1..] : text[1..close];
            rest = close < 0 ? "" : text[(close + 1)..];
        }
        else
        {
            var space = text.IndexOf(' ');
            exe = space < 0 ? text : text[..space];
            rest = space < 0 ? "" : text[space..];
        }
        return (exe, WindowsCommandLine.Split(rest));
    }
}
