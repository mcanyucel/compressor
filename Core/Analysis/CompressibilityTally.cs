using Core.Models;

namespace Core.Analysis;

/// <summary>
/// Accumulates files by compressibility and turns them into a <see cref="CompressibilityHint"/> and a rough archive-size estimate.
///
/// Weighted by <b>bytes</b>, not file count: one 4 GB video next to 200 small text files is a poor candidate,
/// even though most of its files compress well.
/// </summary>
public sealed class CompressibilityTally
{
    // Rough output/input ratios for the size estimate. Deliberately conservative.
    private const double CompressibleRatio = 0.3;
    private const double UnknownRatio = 0.7;
    private const double IncompressibleRatio = 1.0;

    // Share of bytes above which a folder counts as Good or Poor (from the design doc).
    private const double Threshold = 0.65;

    public int FileCount { get; private set; }
    public long TotalBytes { get; private set; }
    public long CompressibleBytes { get; private set; }
    public long IncompressibleBytes { get; private set; }

    public void Add(string fileName, long size)
    {
        FileCount++;
        TotalBytes += size;

        string extension = Path.GetExtension(fileName);
        if (ExtensionLists.KnownCompressible.Contains(extension))
            CompressibleBytes += size;
        else if (ExtensionLists.KnownIncompressible.Contains(extension))
            IncompressibleBytes += size;
    }

    public CompressibilityHint Hint
    {
        get
        {
            if (FileCount == 0) return CompressibilityHint.Empty;
            if (TotalBytes == 0) return CompressibilityHint.Average;

            double good = (double)CompressibleBytes / TotalBytes;
            double poor = (double)IncompressibleBytes / TotalBytes;

            return good > Threshold ? CompressibilityHint.Good
                : poor > Threshold ? CompressibilityHint.Poor
                : CompressibilityHint.Average;
        }
    }

    /// <summary>
    /// Rough archive size, from fixed per-category ratios. Good enough for a disk-space check and a savings preview,
    /// not a promise.
    /// </summary>
    public long EstimatedArchiveBytes
    {
        get
        {
            long unknown = TotalBytes - CompressibleBytes - IncompressibleBytes;
            return (long)(CompressibleBytes * CompressibleRatio + unknown * UnknownRatio + IncompressibleBytes * IncompressibleRatio);
        }
    }
}
