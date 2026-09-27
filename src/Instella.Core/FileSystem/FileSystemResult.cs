namespace Instella.Core.FileSystem;

/// <summary>
/// Result of a file system operation.
/// </summary>
public readonly struct FileSystemResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; }

    /// <summary>Error information if the operation failed.</summary>
    public FileSystemError? Error { get; }

    private FileSystemResult(bool success, FileSystemError? error)
    {
        Success = success;
        Error = error;
    }

    /// <summary>Creates a successful result.</summary>
    public static FileSystemResult Ok() => new(true, null);

    /// <summary>Creates a failed result with an error.</summary>
    public static FileSystemResult Fail(FileSystemError error) => new(false, error);

    /// <summary>Creates a failed result from an exception.</summary>
    public static FileSystemResult FromException(Exception ex)
    {
        var (type, message) = CategorizeException(ex);
        return Fail(new FileSystemError(type, message, ex));
    }

    internal static (FileSystemErrorType Type, string Message) CategorizeException(Exception ex) => ex switch
    {
        FileNotFoundException => (FileSystemErrorType.NotFound, ex.Message),
        DirectoryNotFoundException => (FileSystemErrorType.NotFound, ex.Message),
        UnauthorizedAccessException => (FileSystemErrorType.AccessDenied, ex.Message),
        IOException io when io.HResult == unchecked((int)0x80070020) => (FileSystemErrorType.InUse, "File is in use by another process."),
        IOException io when io.HResult == unchecked((int)0x80070070) => (FileSystemErrorType.DiskFull, "Not enough disk space."),
        PathTooLongException => (FileSystemErrorType.InvalidPath, "Path is too long."),
        ArgumentException => (FileSystemErrorType.InvalidPath, ex.Message),
        _ => (FileSystemErrorType.Unknown, ex.Message)
    };

    /// <summary>Implicitly converts a FileSystemError to a failed result.</summary>
    public static implicit operator FileSystemResult(FileSystemError error) => Fail(error);
}

/// <summary>
/// Result of a file system operation that returns a value.
/// </summary>
/// <typeparam name="T">The type of the result value.</typeparam>
public readonly struct FileSystemResult<T>
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; }

    /// <summary>The result value if successful.</summary>
    public T? Value { get; }

    /// <summary>Error information if the operation failed.</summary>
    public FileSystemError? Error { get; }

    private FileSystemResult(bool success, T? value, FileSystemError? error)
    {
        Success = success;
        Value = value;
        Error = error;
    }

    /// <summary>Creates a successful result with a value.</summary>
    public static FileSystemResult<T> Ok(T value) => new(true, value, null);

    /// <summary>Creates a failed result with an error.</summary>
    public static FileSystemResult<T> Fail(FileSystemError error) => new(false, default, error);

    /// <summary>Creates a failed result from an exception.</summary>
    public static FileSystemResult<T> FromException(Exception ex)
    {
        var (type, message) = FileSystemResult.CategorizeException(ex);
        return Fail(new FileSystemError(type, message, ex));
    }

    /// <summary>Implicitly converts a FileSystemError to a failed result.</summary>
    public static implicit operator FileSystemResult<T>(FileSystemError error) => Fail(error);
}
