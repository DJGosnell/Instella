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
}
