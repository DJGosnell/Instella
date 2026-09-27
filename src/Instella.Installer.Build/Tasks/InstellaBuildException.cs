namespace Instella.Installer.Build.Tasks;

/// <summary>
/// A build failure with a stable <c>INSTELLA0xxx</c> code; the MSBuild task reports it as an
/// error with that code instead of an exception stack.
/// </summary>
public sealed class InstellaBuildException(string code, string message) : Exception(message)
{
    /// <summary>The diagnostic code, for example <c>INSTELLA0201</c>.</summary>
    public string Code { get; } = code;
}
