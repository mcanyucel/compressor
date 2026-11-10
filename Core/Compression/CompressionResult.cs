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

public sealed record CompressionResult(CompressionOutcome Outcome, string? Message = null);
