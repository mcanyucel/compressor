namespace Core.Models;

public sealed class JobItemError
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}

public sealed record JobItemStats
{
    public TimeSpan Duration { get; init; }
    public long BytesBefore { get; init; }
    public long BytesAfter { get; init; }
    public int FileCount { get; init;  }

    /// <summary>Originals deleted after verification (0 unless <c>DeleteAfterVerify</c> is on).</summary>
    public int FilesDeleted { get; init; }
    
    public double CompressionRatio => BytesBefore > 0
    ? 1.0 - (double)BytesAfter / BytesBefore
    : 0.0;
}

/// <summary>
/// Represents a single compression unit - either a file or a directory.
/// This is a pure data class, it has no knowledge of the UI.
/// All mutable fields that are written from backgrounds use <c>volatile</c>
/// for visibility or <c>Interlocked</c> for atomicity.
/// </summary>
public sealed class JobItem
{
    // ── Identity ──────────────────────────────────────────────────────────────────
    // Set once at job-build time. Never mutated after Seal()
    
    /// <summary>
    /// Absolute path to the source file or directory.
    /// </summary>
    public required string SourcePath { get; init; }
    
    /// <summary>
    /// Absolute path where the 7z archive will be written.
    /// Accounts for collision resolution at initiation, but things may change during runtime so another check is necessary.
    /// </summary>
    public required string OutputPath { get; init; }
    
    public required ItemKind ItemKind { get; init; }
    
    /// <summary>
    /// Total uncompressed byte count, pre-scanned at job-build time.
    /// </summary>
    public long TotalBytes { get; init; }
    
    /// <summary>
    /// Total file count inside this item, pre-scanned at job-build time.
    /// </summary>
    public int TotalFileCount { get; init; }

    /// <summary>
    /// Normalized full paths under <see cref="SourcePath"/> to leave out of the archive: subfolders with a Skip rule,
    /// nested items, and their archives. Compared with <c>Paths.Comparer</c>.
    /// </summary>
    public IReadOnlySet<string> ExcludedPaths { get; init; } = new HashSet<string>();
    
    // ── Runtime State ─────────────────────────────────────────────────────────────
    // Written from background worker threads. <c>Volatile</c> ensures visibility across threads without a full lock. Enum is int-backed
    // so reads/writes are atomic on all supported platforms.

    public volatile JobItemState State = JobItemState.Pending;

    /// <summary>
    /// Bytes written to the archive so far. Mutate via <c>Interlocked.Add</c>
    /// </summary>
    public long BytesProcessed;
    
    /// <summary>
    /// Files written to the archive so far. Mutate via <c>Interlocked.Increment</c>
    /// </summary>
    public int FilesProcessed;
    
    // ── Control ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancel this specific item. Called by the FSW handler when a source path change is detected, or by the user via per-item cancedl button.
    /// </summary>
    public CancellationTokenSource Cts { get; } = new();
    
    // ── Post-Run Metadata ─────────────────────────────────────────────────────────
    // Written once by the worker after the item reaches a terminal state.
    // Safe to read from the UI thread after State is terminal.
    
    public JobItemError? Error { get; set; }
    public JobItemStats? Stats { get; set; }

    /// <summary>
    /// True if the item is in a terminal state that will never change again.
    /// </summary>
    public bool IsTerminal => State is
        JobItemState.Done or
        JobItemState.Skipped or
        JobItemState.Cancelled or
        JobItemState.NeedsAttention or
        JobItemState.Failed;
}