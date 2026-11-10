using Core.Analysis;
using Core.FileSystem;
using Core.Models;

namespace Core.Preflight;

/// <summary>
/// The checks that need to touch every file, run after <see cref="Planning.JobBuilder"/> and before execution:
/// <list type="number">
/// <item>Locked files: each file is opened for reading, the way 7-Zip opens it.</item>
/// <item>Compressibility per item, by file type and size.</item>
/// <item>Free space on each output drive against the estimated archive sizes.</item>
/// </list>
/// Results are added to <see cref="BackupJob.Preflight"/>. Running it again replaces the previous results.
/// </summary>
public static class PreflightRunner
{
    public static async Task<PreflightReport> RunAsync(BackupJob job, CancellationToken ct = default)
    {
        if (job.OverallState is not (JobState.ReadyForPreflight or JobState.RunningPreflight or JobState.ReadyToExecute))
            throw new InvalidOperationException($"Pre-flight needs a sealed job that hasn't run yet (state: {job.OverallState}).");

        PreflightReport report = job.Preflight ??= new PreflightReport();
        job.MarkPreflightStarted();

        report.LockedFiles.Clear();
        report.PoorlyCompressible.Clear();
        report.LowDiskSpace.Clear();
        report.Estimates.Clear();

        await Task.Run(() =>
        {
            foreach (JobItem item in job.Items.Values.OrderBy(i => i.SourcePath, Paths.Comparer))
            {
                var tally = new CompressibilityTally();

                foreach (FileInfo file in ListFiles(item, report, ct))
                {
                    tally.Add(file.Name, file.Length);
                    if (IsLocked(file))
                        report.LockedFiles.Add(file.FullName);
                }

                report.Estimates[item.SourcePath] = new ItemEstimate(tally.Hint, tally.EstimatedArchiveBytes);
                if (tally.Hint == CompressibilityHint.Poor)
                    report.PoorlyCompressible.Add(item.SourcePath);
            }

            CheckDiskSpace(job, report);
        }, ct);

        job.MarkPreflightCompleted();
        return report;
    }

    private static IEnumerable<FileInfo> ListFiles(JobItem item, PreflightReport report, CancellationToken ct)
    {
        if (item.ItemKind == ItemKind.File)
        {
            var file = new FileInfo(item.SourcePath);
            if (file.Exists) return [file];

            report.InaccessiblePaths.Add($"{item.SourcePath}: file no longer exists");
            return [];
        }

        // Unreadable subfolders were already reported by the job builder; only add ones that are new.
        return SourceEnumerator.EnumerateFiles(item.SourcePath, item.ExcludedPaths,
            onInaccessible: (path, ex) =>
            {
                if (!report.InaccessiblePaths.Exists(p => p.StartsWith(path + ":", Paths.Comparison)))
                    report.InaccessiblePaths.Add($"{path}: {ex.Message}");
            },
            ct: ct);
    }

    /// <summary>
    /// Opens with <see cref="FileShare.Read"/>, as 7-Zip does (without <c>-ssw</c>): a file another program has
    /// open for writing fails here exactly as it would during compression.
    /// </summary>
    private static bool IsLocked(FileInfo file)
    {
        try
        {
            using var _ = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void CheckDiskSpace(BackupJob job, PreflightReport report)
    {
        var neededPerDrive = job.Items.Values
            .GroupBy(i => Path.GetPathRoot(i.OutputPath) ?? "", Paths.Comparer)
            .Select(g => (Drive: g.Key, Needed: g.Sum(i => report.Estimates.TryGetValue(i.SourcePath, out var e) ? e.EstimatedArchiveBytes : i.TotalBytes)));

        foreach ((string drive, long needed) in neededPerDrive)
        {
            long free;
            try
            {
                free = new DriveInfo(drive).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue; // network shares and the like: can't tell
            }

            if (needed > free)
                report.LowDiskSpace.Add($"{drive}: archives need about {needed:N0} bytes, {free:N0} bytes free");
        }
    }
}
