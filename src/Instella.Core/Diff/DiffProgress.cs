namespace Instella.Core.Diff;

/// <summary>
/// Progress information for diff operations.
/// </summary>
/// <param name="BytesProcessed">Number of bytes processed so far.</param>
/// <param name="TotalBytes">Total bytes to process (may be approximate for some operations).</param>
/// <param name="Phase">Current phase of the operation.</param>
internal readonly record struct DiffProgress(long BytesProcessed, long TotalBytes, DiffPhase Phase)
{
    /// <summary>
    /// Progress as a percentage (0-100).
    /// </summary>
    public double Percentage => TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100 : 0;
}

/// <summary>
/// Phases of a diff operation.
/// </summary>
internal enum DiffPhase : byte
{
    /// <summary>Reading the original file.</summary>
    ReadingOldFile,

    /// <summary>Reading the new file (patch creation) or patch file (patch application).</summary>
    ReadingNewFile,

    /// <summary>Processing/computing the diff.</summary>
    Processing,

    /// <summary>Writing output.</summary>
    Writing,

    /// <summary>Operation complete.</summary>
    Complete
}
