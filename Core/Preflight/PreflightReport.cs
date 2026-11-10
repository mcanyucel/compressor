namespace Core.Preflight;

/// <summary>
/// Issues found before execution, shown to the user while they can still act on them.
/// Filled in by <c>PreflightRunner</c> (not written yet).
/// </summary>
public sealed class PreflightReport
{
    /// <summary>Files that could not be opened for reading.</summary>
    public List<string> LockedFiles { get; } = [];

    /// <summary>Output paths where a <c>.7z</c> already exists.</summary>
    public List<string> Collisions { get; } = [];

    /// <summary>Source paths of items whose content is mostly incompressible.</summary>
    public List<string> PoorlyCompressible { get; } = [];

    /// <summary>Symlinks skipped at job-build time.</summary>
    public List<string> SkippedSymlinks { get; } = [];

    /// <summary>Items that contain no files; no archive will be produced.</summary>
    public List<string> EmptyItems { get; } = [];

    /// <summary><c>.7z.tmp</c> files left behind by an interrupted run.</summary>
    public List<string> StaleTmpFiles { get; } = [];

    public bool HasWarnings =>
        LockedFiles.Count > 0 ||
        Collisions.Count > 0 ||
        PoorlyCompressible.Count > 0 ||
        SkippedSymlinks.Count > 0 ||
        EmptyItems.Count > 0 ||
        StaleTmpFiles.Count > 0;
}
