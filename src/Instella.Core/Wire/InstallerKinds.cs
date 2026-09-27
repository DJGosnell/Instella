namespace Instella.Core.Wire;

/// <summary>The kinds of installer a release can publish, and the rules for their file names.</summary>
public static class InstallerKinds
{
    /// <summary>The small installer that downloads the app from the server.</summary>
    public const string Online = "online";

    /// <summary>The installer that carries the app.</summary>
    public const string Offline = "offline";

    /// <summary>Longest accepted installer file name.</summary>
    public const int MaxFileNameLength = 128;

    /// <summary>True for <see cref="Online"/> and <see cref="Offline"/>.</summary>
    public static bool IsValid(string? kind) => kind is Online or Offline;

    /// <summary>
    /// True when <paramref name="fileName"/> is a plain file name that is safe to send in a
    /// download header and to save on every OS: letters, digits, <c>. _ - + ( )</c> and spaces,
    /// not starting with a dot or a space.
    /// </summary>
    public static bool IsValidFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName)
        && fileName.Length <= MaxFileNameLength
        && fileName[0] is not ('.' or ' ')
        && fileName[^1] is not ('.' or ' ')
        && fileName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or '(' or ')' or ' ');
}
