namespace Core.Models;

public sealed class JobConfig
{
    public required string RootPath { get; init; }

    public ParallelismLevel Parallelism { get; init; } = ParallelismLevel.Normal;
    public CompressionStrategyKind Strategy { get; init; } = CompressionStrategyKind.Safe;
    public CollisionBehavior CollisionBehavior { get; init; } = CollisionBehavior.RenameNew;
    public LockedFileBehavior LockedFileBehavior { get; init; } = LockedFileBehavior.Skip;
    public SourceChangeBehavior SourceChangeBehavior { get; init; } = SourceChangeBehavior.CancelSubJob;
    public bool DeleteAfterVerify { get; init; } = false;

    /// <summary>
    /// The marking decisions made by the user in the UI tree view.
    /// Paths are relative to <see cref="RootPath"/>
    /// </summary>
    public List<JobRule> Rules { get; init; } = [];

    /// <summary>
    /// Resolved from <see cref="Parallelism"/> at access time.
    /// Always at least 1
    /// </summary>
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