namespace Core.Compression;

public enum CompressionOutcome
{
    /// <summary>Archive written and renamed from <c>.7z.tmp</c> to <c>.7z</c>.</summary>
    Completed,

    /// <summary>
    /// 7-Zip finished with warnings (e.g. a file could not be opened). The archive is left as <c>.7z.tmp</c>
    /// for the user to review; the item should become <c>NeedsAttention</c>.
    /// </summary>
    CompletedWithWarnings,

    /// <summary>The item contains no files after filtering; no archive was produced.</summary>
    NothingToCompress,

    /// <summary>No usable archive was produced. Any <c>.7z.tmp</c> written by this run has been deleted.</summary>
    Failed
}

public sealed record CompressionResult(CompressionOutcome Outcome, string? Message = null)
{
    /// <summary>
    /// The files handed to 7-Zip, as they were just before compression. Set when <see cref="Outcome"/> is
    /// <see cref="CompressionOutcome.Completed"/>; this is what <see cref="VerificationHelpers"/> checks the archive against.
    /// </summary>
    public IReadOnlyList<ArchivedFile> Files { get; init; } = [];
}

/// <summary>
/// One file as it was when handed to 7-Zip.
/// </summary>
/// <param name="RelativePath">Path inside the archive, relative to the item's source root.</param>
/// <param name="Size">Size in bytes just before compression.</param>
/// <param name="LastWriteTimeUtc">Kept so the delete-originals step can check the source hasn't changed since.</param>
public sealed record ArchivedFile(string RelativePath, long Size, DateTime LastWriteTimeUtc);
