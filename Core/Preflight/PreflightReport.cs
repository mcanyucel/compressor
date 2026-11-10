namespace Core.Preflight;

/// <summary>
/// Issues found before execution, shown to the user while they can still act on them.
/// The job builder fills the build-time findings; <c>PreflightRunner</c> (not written yet) adds the rest.
/// </summary>
public sealed class PreflightReport
{
    /// <summary>Rules that were ignored: missing paths, paths outside the root, links, conflicts.</summary>
    public List<string> InvalidRules { get; } = [];

    /// <summary>Files that could not be opened for reading.</summary>
    public List<string> LockedFiles { get; } = [];

    /// <summary>Natural output paths (<c>&lt;source&gt;.7z</c>) that were already taken.</summary>
    public List<string> Collisions { get; } = [];

    /// <summary>Source paths of items whose content is mostly incompressible.</summary>
    public List<string> PoorlyCompressible { get; } = [];

    /// <summary>Symbolic links and junctions that were skipped (never followed).</summary>
    public List<string> SkippedLinks { get; } = [];

    /// <summary>Folders that could not be listed, with the reason.</summary>
    public List<string> InaccessiblePaths { get; } = [];

    /// <summary>Marked items that contain no files; no archive will be produced.</summary>
    public List<string> EmptyItems { get; } = [];

    /// <summary><c>.7z.tmp</c> files left behind by an interrupted run.</summary>
    public List<string> StaleTmpFiles { get; } = [];

    public bool HasWarnings =>
        InvalidRules.Count > 0 ||
        LockedFiles.Count > 0 ||
        Collisions.Count > 0 ||
        PoorlyCompressible.Count > 0 ||
        SkippedLinks.Count > 0 ||
        InaccessiblePaths.Count > 0 ||
        EmptyItems.Count > 0 ||
        StaleTmpFiles.Count > 0;
}
