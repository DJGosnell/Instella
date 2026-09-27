using System.Text;

namespace Instella.Core.Platform.Linux;

/// <summary>
/// Escaping for freedesktop.org Desktop Entry files (<c>.desktop</c>), per the Desktop Entry
/// Specification's "Exec key" and value-type rules.
/// </summary>
internal static class DesktopEntry
{
    /// <summary>
    /// An <c>Exec=</c> value: <paramref name="executable"/> as one quoted argument, then
    /// <paramref name="arguments"/> as given (the author's own quoting), then an optional
    /// field code such as <c>%f</c>.
    /// </summary>
    public static string Exec(string executable, string? arguments = null, string? fieldCode = null)
    {
        var sb = new StringBuilder(QuoteArgument(executable));
        if (!string.IsNullOrWhiteSpace(arguments))
            sb.Append(' ').Append(EscapeString(arguments.Replace("%", "%%", StringComparison.Ordinal)));
        if (!string.IsNullOrEmpty(fieldCode))
            sb.Append(' ').Append(fieldCode);
        return sb.ToString();
    }

    /// <summary>
    /// One quoted <c>Exec</c> argument. Inside double quotes the spec reserves <c>"</c>,
    /// <c>`</c>, <c>$</c> and <c>\</c>, which are backslash-escaped; <c>%</c> is doubled (it
    /// starts field codes). The result is then string-escaped, as every value is, so a literal
    /// backslash ends up as four.
    /// </summary>
    public static string QuoteArgument(string argument)
    {
        var sb = new StringBuilder(argument.Length + 2).Append('"');
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\')
                sb.Append('\\');
            if (c == '%')
                sb.Append('%');
            sb.Append(c);
        }
        return EscapeString(sb.Append('"').ToString());
    }

    /// <summary>A value of type string: backslash, newline, tab and carriage return escaped.</summary>
    public static string EscapeString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal)
             .Replace("\t", "\\t", StringComparison.Ordinal)
             .Replace("\r", "\\r", StringComparison.Ordinal);
}
