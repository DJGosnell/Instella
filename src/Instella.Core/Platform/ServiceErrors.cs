namespace Instella.Core.Platform;

/// <summary>Maps Win32 service errors to <see cref="ServiceFailure"/>.</summary>
internal static class ServiceErrors
{
    /// <summary><c>ERROR_ACCESS_DENIED</c>.</summary>
    public const int AccessDenied = 5;
    /// <summary><c>ERROR_SERVICE_REQUEST_TIMEOUT</c>.</summary>
    public const int RequestTimeout = 1053;
    /// <summary><c>ERROR_SERVICE_ALREADY_RUNNING</c>.</summary>
    public const int AlreadyRunning = 1056;
    /// <summary><c>ERROR_SERVICE_DOES_NOT_EXIST</c>.</summary>
    public const int DoesNotExist = 1060;
    /// <summary><c>ERROR_SERVICE_NOT_ACTIVE</c>.</summary>
    public const int NotActive = 1062;
    /// <summary><c>ERROR_SERVICE_MARKED_FOR_DELETE</c>.</summary>
    public const int MarkedForDelete = 1072;
    /// <summary><c>ERROR_SERVICE_EXISTS</c>.</summary>
    public const int Exists = 1073;

    /// <summary>The kind of failure a Win32 error code stands for.</summary>
    public static ServiceFailure Classify(int win32Error) => win32Error switch
    {
        DoesNotExist => ServiceFailure.NotFound,
        AccessDenied => ServiceFailure.AccessDenied,
        MarkedForDelete => ServiceFailure.MarkedForDeletion,
        Exists => ServiceFailure.AlreadyExists,
        RequestTimeout => ServiceFailure.Timeout,
        _ => ServiceFailure.Failed,
    };
}
