namespace Core.Analysis;

/// <summary>
/// File types with known compression behavior. Anything not listed counts as "unknown" (assumed average).
/// Extensions are lower-case and include the dot.
/// </summary>
public static class ExtensionLists
{
    /// <summary>Already compressed: 7-Zip saves almost nothing on these.</summary>
    public static readonly IReadOnlySet<string> KnownIncompressible = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // images
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl",
        // video
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp",
        // audio
        ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma",
        // archives and packages
        ".zip", ".7z", ".gz", ".tgz", ".bz2", ".xz", ".rar", ".br", ".zst", ".cab", ".jar", ".apk"
    };

    /// <summary>
    /// Compress well. Note that .docx/.xlsx/.pptx are deliberately absent: they are zip files internally and gain
    /// little, so they count as unknown. The older binary Office formats compress very well and are common on old PCs.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownCompressible = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // text and data
        ".txt", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".log",
        ".html", ".htm", ".css", ".md", ".rtf", ".sql",
        // source code
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".c", ".cpp", ".h", ".hpp", ".java", ".php", ".rb", ".sh", ".ps1", ".bat",
        // documents and databases (uncompressed formats)
        ".doc", ".xls", ".ppt", ".pst", ".mdb", ".accdb", ".db", ".sqlite",
        // uncompressed media
        ".svg", ".bmp", ".tif", ".tiff", ".psd", ".wav", ".aiff"
    };
}
