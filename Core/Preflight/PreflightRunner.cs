using System.Diagnostics;
using Core.Analysis;
using Core.FileSystem;
using Core.Models;

namespace Core.Preflight;

public sealed class PreflightOptions
{
    /// <summary>
    /// Open every file to find ones 7-Zip won't be able to read. This is by far the slowest part of pre-flight:
    /// the first open of each file triggers the antivirus scan. Without it, locked files are still handled safely
    /// during compression (the item needs attention, nothing is deleted).
    /// </summary>
    public bool CheckLockedFiles { get; init; } = true;
}

/// <param name="FilesDone">Files checked so far, across all items.</param>
/// <param name="FilesTotal">All files in the job, from the job builder's pre-scan.</param>
public sealed record PreflightProgress(int FilesDone, int FilesTotal);

/// <summary>
/// The checks that need to touch every file, run after <see cref="Planning.JobBuilder"/> and before execution:
/// <list type="number">
/// <item>Locked files (optional, see <see cref="PreflightOptions.CheckLockedFiles"/>): each file is opened for
/// reading, the way 7-Zip opens it.</item>
/// <item>Compressibility per item, by file type and size.</item>
/// <item>Free space on each output drive against the estimated archive sizes.</item>
/// </list>
/// Results are added to <see cref="BackupJob.Preflight"/>. Running it again replaces the previous results.
/// </summary>
public static class PreflightRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public static Task<PreflightReport> RunAsync(BackupJob job, CancellationToken ct = default) =>
        RunAsync(job, new PreflightOptions(), progress: null, ct);

    /// <param name="progress">Reported about every 100 ms, and once at the end.</param>
    public static async Task<PreflightReport> RunAsync(
        BackupJob job, PreflightOptions options, IProgress<PreflightProgress>? progress = null, CancellationToken ct = default)
    {
        if (job.OverallState is not (JobState.ReadyForPreflight or JobState.RunningPreflight or JobState.ReadyToExecute))
            throw new InvalidOperationException($"Pre-flight needs a sealed job that hasn't run yet (state: {job.OverallState}).");

        PreflightReport report = job.Preflight ??= new PreflightReport();
        job.MarkPreflightStarted();

        report.LockedFilesChecked = options.CheckLockedFiles;
        report.LockedFiles.Clear();
        report.PoorlyCompressible.Clear();
        report.LowDiskSpace.Clear();
        report.Estimates.Clear();

        await Task.Run(() =>
        {
            int total = job.Items.Values.Sum(i => i.TotalFileCount);
            int done = 0;
            var sinceReport = Stopwatch.StartNew();
            progress?.Report(new PreflightProgress(0, total));

            foreach (JobItem item in job.Items.Values.OrderBy(i => i.SourcePath, Paths.Comparer))
            {
                var tally = new CompressibilityTally();

                foreach (FileInfo file in ListFiles(item, report, ct))
                {
                    tally.Add(file.Name, file.Length);
                    if (options.CheckLockedFiles && IsLocked(file))
                        report.LockedFiles.Add(file.FullName);

                    done++;
                    if (progress is not null && sinceReport.Elapsed >= ProgressInterval)
                    {
                        // Files added since the pre-scan can push done past total; don't show more than 100%.
                        progress.Report(new PreflightProgress(Math.Min(done, total), total));
                        sinceReport.Restart();
                    }
                }

                report.Estimates[item.SourcePath] = new ItemEstimate(tally.Hint, tally.EstimatedArchiveBytes);
                if (tally.Hint == CompressibilityHint.Poor)
                    report.PoorlyCompressible.Add(item.SourcePath);
            }

            CheckDiskSpace(job, report);
            progress?.Report(new PreflightProgress(total, total));
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
