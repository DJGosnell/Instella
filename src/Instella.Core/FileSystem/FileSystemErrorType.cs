namespace Instella.Core.FileSystem;

/// <summary>
/// Types of file system errors that can occur during operations.
/// </summary>
public enum FileSystemErrorType : byte
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>File or directory was not found.</summary>
    NotFound = 1,

    /// <summary>Access to the file or directory was denied.</summary>
    AccessDenied = 2,

    /// <summary>The file is in use by another process.</summary>
    InUse = 3,

    /// <summary>The disk is full.</summary>
    DiskFull = 4,

    /// <summary>The path is invalid.</summary>
    InvalidPath = 5,

    /// <summary>An unknown error occurred.</summary>
    Unknown = 255
}
