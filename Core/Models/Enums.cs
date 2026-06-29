namespace Core.Models;

public enum JobItemState
{
    Pending,
    InProgress,
    Verified,
    Done,
    Skipped,
    Cancelled,
    NeedsAttention // Stopped due to external change - user should review
}

public enum ItemKind
{
    File,
    Directory
}

public enum ParallelismLevel
{
    Minimum,    // max(1, ProcessorCount * 0.10)
    Low,        // max(1, ProcessorCount * 0.25)
    Normal,     // max(1, ProcessorCount * 0.50) - default
    High,       // max(1, ProcessorCount * 0.75)
    Maximum     // ProcessorCount
}

public enum CompressionStrategyKind
{
    /// <summary>
    /// No FSW, no reactive handling
    /// </summary>
    Fast,
    /// <summary>
    /// FSW + per-item cancellationtokensource
    /// </summary>
    Safe
}

public enum CollisionBehavior
{
    /// <summary>
    /// Append a unique numeric suffix to the newly created archive. Recommended
    /// </summary>
    RenameNew,
    /// <summary>
    /// Leave teh existing archive in place and skip this job item.
    /// </summary>
    Skip
}

public enum LockedFileBehavior
{
    /// <summary>
    /// Skip the locked file; log in NeedsAttention
    /// </summary>
    Skip,
    /// <summary>
    /// Abort the entire sub-job if any file within it is locked
    /// </summary>
    AbortSubJob
}

public enum SourceChangeBehavior
{
    /// <summary>
    /// Cancel the sub-job that owns the changed path.
    /// </summary>
    CancelSubJob,
    /// <summary>
    /// Ignore source changes and continue.
    /// </summary>
    Ignore
}

public enum JobAction
{
    Compress,
    Skip
}

public enum CompressibilityHint
{
    /// <summary>
    /// Not yet scanned - node has not been expanded.
    /// </summary>
    Unscanned,
    /// <summary>
    /// Leaf folder, the majority of files are known-compressible types.
    /// </summary>
    Good,
    /// <summary>
    /// Leaf folder, mixed compressible and incompressible types.
    /// </summary>
    Average,
    /// <summary>
    /// Leaf folder; the majority of files are known-incompressible types. 
    /// </summary>
    Poor,
    /// <summary>
    /// Folder has subfolders; aggregate hint requires a deep scan.
    /// </summary>
    Unknown,
    /// <summary>
    /// Folder contains no files.
    /// </summary>
    Empty
}

public enum JobState
{
    Building,
    ReadyForPreflight,
    RunningPreflight,
    ReadyToExecute,
    Execute,
    Completed,
    CompletedWithErrors,
    Cancelled
}