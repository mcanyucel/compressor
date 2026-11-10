using Core.Models;

namespace Core.Preflight;

/// <summary>
/// Issues found before execution, shown to the user while they can still act on them.
/// <see cref="Planning.JobBuilder"/> fills the build-time findings; <see cref="PreflightRunner"/> adds the rest.
/// </summary>
public sealed class PreflightReport
{
    // ── From the job builder ─────────────────────────────────────────────────────────

    /// <summary>Rules that were ignored: missing paths, paths outside the root, links, conflicts.</summary>
    public List<string> InvalidRules { get; } = [];

    /// <summary>Natural output paths (<c>&lt;source&gt;.7z</c>) that were already taken.</summary>
    public List<string> Collisions { get; } = [];

    /// <summary>Symbolic links and junctions that were skipped (never followed).</summary>
    public List<string> SkippedLinks { get; } = [];

    /// <summary>Folders that could not be listed, with the reason.</summary>
    public List<string> InaccessiblePaths { get; } = [];

    /// <summary>Marked items that contain no files; no archive will be produced.</summary>
    public List<string> EmptyItems { get; } = [];

    /// <summary><c>.7z.tmp</c> files left behind by an interrupted run.</summary>
    public List<string> StaleTmpFiles { get; } = [];

    // ── From the pre-flight runner ───────────────────────────────────────────────────

    /// <summary>Files that could not be opened for reading. 7-Zip would skip them.</summary>
    public List<string> LockedFiles { get; } = [];

    /// <summary>False when the locked-file check was skipped; <see cref="LockedFiles"/> is then empty.</summary>
    public bool LockedFilesChecked { get; set; }

    /// <summary>Source paths of items whose content is mostly incompressible.</summary>
    public List<string> PoorlyCompressible { get; } = [];

    /// <summary>Drives whose free space is below the estimated size of the archives written there.</summary>
    public List<string> LowDiskSpace { get; } = [];

    /// <summary>Per item (keyed by source path): compressibility hint and rough archive size.</summary>
    public Dictionary<string, ItemEstimate> Estimates { get; } = [];

    public bool HasWarnings =>
        InvalidRules.Count > 0 ||
        Collisions.Count > 0 ||
        SkippedLinks.Count > 0 ||
        InaccessiblePaths.Count > 0 ||
        EmptyItems.Count > 0 ||
        StaleTmpFiles.Count > 0 ||
        LockedFiles.Count > 0 ||
        PoorlyCompressible.Count > 0 ||
        LowDiskSpace.Count > 0;
}

public sealed record ItemEstimate(CompressibilityHint Hint, long EstimatedArchiveBytes);
