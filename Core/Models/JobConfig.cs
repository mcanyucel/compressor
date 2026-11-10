using System.Text.Json.Serialization;

namespace Core.Models;

public sealed class JobConfig
{
    public required string RootPath { get; init; }

    public ParallelismLevel Parallelism { get; init; } = ParallelismLevel.Normal;

    /// <summary>
    /// 7-Zip <c>-mx</c>: 0 (store) to 9 (ultra). Memory use grows with level and threads; keep old hardware at 5 or below.
    /// </summary>
    public int CompressionLevel { get; init; } = 5;

    public CollisionBehavior CollisionBehavior { get; init; } = CollisionBehavior.RenameNew;
    public LockedFileBehavior LockedFileBehavior { get; init; } = LockedFileBehavior.Skip;

    /// <summary>
    /// What to do when a source file changes during the job. Has no effect until file watching exists (v2).
    /// </summary>
    public SourceChangeBehavior SourceChangeBehavior { get; init; } = SourceChangeBehavior.CancelSubJob;

    public bool DeleteAfterVerify { get; init; } = false;

    /// <summary>
    /// The marking decisions made by the user in the UI tree view.
    /// Paths are relative to <see cref="RootPath"/>
    /// </summary>
    public List<JobRule> Rules { get; init; } = [];

    /// <summary>
    /// Items compressed at the same time. Splits the <see cref="DegreeOfParallelism"/> thread budget with
    /// <see cref="ThreadsPerItem"/>; see "Parallelism" in the design doc. Defaults to be tuned with benchmarks.
    /// </summary>
    [JsonIgnore]
    public int ConcurrentItems => Math.Max(1, DegreeOfParallelism / 2);

    /// <summary>
    /// Threads given to each 7-Zip process (<c>-mmt</c>).
    /// </summary>
    [JsonIgnore]
    public int ThreadsPerItem => Math.Max(1, DegreeOfParallelism / ConcurrentItems);

    /// <summary>
    /// Total thread budget, resolved from <see cref="Parallelism"/> at access time.
    /// Always at least 1
    /// </summary>
    [JsonIgnore]
    public int DegreeOfParallelism => Parallelism switch
    {
        ParallelismLevel.Minimum => Math.Max(1, (int)(Environment.ProcessorCount * 0.10)),
        ParallelismLevel.Low => Math.Max(1, (int)(Environment.ProcessorCount * 0.25)),
        ParallelismLevel.Normal => Math.Max(1, (int)(Environment.ProcessorCount * 0.50)),
        ParallelismLevel.High => Math.Max(1, (int)(Environment.ProcessorCount * 0.75)),
        ParallelismLevel.Maximum => Environment.ProcessorCount,
        _ => Math.Max(1, (int)(Environment.ProcessorCount * 0.50))
    };
}