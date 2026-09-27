namespace Instella.Core.FileSystem;

/// <summary>
/// Represents a file system error with type, message, and optional exception.
/// </summary>
/// <param name="Type">The type of error.</param>
/// <param name="Message">Human-readable error message.</param>
/// <param name="Exception">The underlying exception, if any.</param>
public sealed record FileSystemError(FileSystemErrorType Type, string Message, Exception? Exception = null);
