namespace Instella.Core.Manifest;

/// <summary>
/// Thrown when a required manifest field is missing or empty.
/// </summary>
public sealed class InvalidManifestException : Exception
{
    /// <summary>The manifest field that is missing or invalid.</summary>
    public string FieldName { get; }

    /// <summary>A required field is missing or empty.</summary>
    public InvalidManifestException(string fieldName)
        : base($"Required manifest field '{fieldName}' is missing or empty.")
    {
        FieldName = fieldName;
    }

    /// <summary>A field is present but invalid.</summary>
    public InvalidManifestException(string fieldName, string reason)
        : base($"Manifest field '{fieldName}' is invalid: {reason}")
    {
        FieldName = fieldName;
    }
}
