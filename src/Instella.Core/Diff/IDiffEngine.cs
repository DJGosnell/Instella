namespace Instella.Core.Diff;

/// <summary>
/// Interface for binary diff operations (patch creation and application).
/// </summary>
internal interface IDiffEngine
{
    /// <summary>
    /// Creates a binary patch from old file to new file.
    /// </summary>
    /// <param name="oldFile">Stream containing the original file (must be seekable).</param>
    /// <param name="newFile">Stream containing the modified file (must be seekable).</param>
    /// <param name="patchOutput">Stream to write the patch to (must be seekable and writable).</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreatePatchAsync(
        Stream oldFile,
        Stream newFile,
        Stream patchOutput,
        IProgress<DiffProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a binary patch to an old file to produce the new file.
    /// </summary>
    /// <param name="oldFile">Stream containing the original file (must be seekable).</param>
    /// <param name="patch">Stream containing the patch data (must be seekable).</param>
    /// <param name="newFileOutput">Stream to write the patched file to.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxOutputSize">Refuse a patch whose header announces a larger output (the signed size).</param>
    Task ApplyPatchAsync(
        Stream oldFile,
        Stream patch,
        Stream newFileOutput,
        IProgress<DiffProgress>? progress = null,
        CancellationToken ct = default,
        long? maxOutputSize = null);
}
