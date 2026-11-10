using System.Diagnostics;
using Core.Execution;
using Core.FileSystem;
using Core.Models;
using Core.Planning;
using Core.Preflight;
using Core.SevenZip;
using Core.Strategies;

namespace Cli;

/// <summary>
/// <c>plan</c> and <c>run</c>: load the job file, build the job, run pre-flight, print the report.
/// <c>run</c> then asks for confirmation and executes.
/// </summary>
public static class RunCommand
{
    private static readonly Lock ConsoleLock = new();

    public static async Task<int> RunAsync(Arguments args, bool execute)
    {
        string jobPath = args.RequirePositional("job file");
        if (!File.Exists(jobPath))
            throw new UsageException($"Job file not found: {jobPath}");

        // ── Load, locate 7-Zip, build, pre-flight ────────────────────────────────────
        JobConfig config;
        try { config = JobFile.Load(jobPath); }
        catch (InvalidDataException ex) { throw new UsageException(ex.Message); }

        if (!SevenZipLocator.TryLocate(new SevenZipLocatorOptions { ConfiguredPath = args.Get("--7zip"), AllowInstalled = true },
                out SevenZipExecutable? sevenZip, out string? locateError))
            throw new UsageException(locateError);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // second Ctrl+C: let the process end
            e.Cancel = true;
            cts.Cancel();
            lock (ConsoleLock) Console.WriteLine("\nStopping... unfinished archives will be removed. Press Ctrl+C again to force quit.");
        };

        Console.WriteLine($"Building job from {config.RootPath} ...");
        BackupJob job;
        PreflightReport report;
        try
        {
            job = JobBuilder.Build(config, cts.Token);
            Console.WriteLine("Running pre-flight checks (opening every file) ...");
            report = await PreflightRunner.RunAsync(job, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }

        Console.WriteLine();
        PrintPlan(job, report, sevenZip);
        PrintWarnings(report);

        if (!execute)
            return ExitCodes.Ok;

        if (job.Items.IsEmpty)
        {
            Console.WriteLine("\nNothing to compress.");
            return ExitCodes.Ok;
        }

        if (!args.Has("--yes") && !Confirm(job.Config.DeleteAfterVerify))
            return ExitCodes.Cancelled;

        // ── Execute ──────────────────────────────────────────────────────────────────
        Console.WriteLine();
        var executor = new JobExecutor(new SevenZipStrategy(new SevenZipRunner(sevenZip)));
        var stopwatch = Stopwatch.StartNew();
        var progress = new ConsoleProgress(job);

        using (var statusTimer = new Timer(_ => progress.DrawStatus(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
            await executor.RunAsync(job, progress, cts.Token);

        progress.ClearStatus();
        PrintSummary(job, stopwatch.Elapsed);

        return job.OverallState switch
        {
            JobState.Completed => ExitCodes.Ok,
            JobState.Cancelled => ExitCodes.Cancelled,
            _ => ExitCodes.CompletedWithErrors
        };
    }

    // ── Plan ─────────────────────────────────────────────────────────────────────────

    private static void PrintPlan(BackupJob job, PreflightReport report, SevenZipExecutable sevenZip)
    {
        string root = job.Config.RootPath;
        long totalBytes = job.TotalBytes;
        long estimated = job.Items.Values.Sum(i => report.Estimates.TryGetValue(i.SourcePath, out var e) ? e.EstimatedArchiveBytes : i.TotalBytes);

        Console.WriteLine($"Job:     {root}");
        Console.WriteLine($"7-Zip:   {sevenZip.Path} ({sevenZip.Source.ToString().ToLowerInvariant()})");
        Console.WriteLine($"Threads: {job.Config.ConcurrentItems} item(s) at a time x {job.Config.ThreadsPerItem} thread(s), level {job.Config.CompressionLevel}");
        Console.WriteLine(job.Config.DeleteAfterVerify
            ? "Originals: DELETED after each archive is verified (only files that are unchanged since archiving)"
            : "Originals: kept (deleteAfterVerify is off)");
        Console.WriteLine();

        if (job.Items.IsEmpty)
        {
            Console.WriteLine("No items to compress.");
            return;
        }

        Console.WriteLine($"  {"Item",-45} {"Files",9} {"Size",10} {"Est. 7z",10}  {"Hint",-8} Output");
        Console.WriteLine($"  {new string('-', 45)} {new string('-', 9)} {new string('-', 10)} {new string('-', 10)}  {new string('-', 8)} ------");
        foreach (JobItem item in job.Items.Values.OrderBy(i => i.SourcePath, Paths.Comparer))
        {
            report.Estimates.TryGetValue(item.SourcePath, out ItemEstimate? estimate);
            string name = Format.Relative(root, item.SourcePath);
            if (name.Length > 45) name = "..." + name[^42..];
            Console.WriteLine($"  {name,-45} {item.TotalFileCount,9:N0} {Format.Bytes(item.TotalBytes),10} " +
                              $"{Format.Bytes(estimate?.EstimatedArchiveBytes ?? item.TotalBytes),10}  " +
                              $"{Format.Hint(estimate?.Hint ?? CompressibilityHint.Unknown),-8} {Path.GetFileName(item.OutputPath)}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {job.Items.Count} item(s), {Format.Bytes(totalBytes)} -> about {Format.Bytes(estimated)} (rough estimate)");
    }

    private static void PrintWarnings(PreflightReport report)
    {
        if (!report.HasWarnings)
        {
            Console.WriteLine("\nNo warnings.");
            return;
        }

        Console.WriteLine("\nWarnings:");
        Section("Disk space may run out", report.LowDiskSpace);
        Section("Locked files (will be skipped; the item will need attention)", report.LockedFiles);
        Section("Rules ignored", report.InvalidRules);
        Section("Name collisions (a numbered name will be used, or the item skipped, per collisionBehavior)", report.Collisions);
        Section("Leftover temporary archives from an earlier run (left untouched)", report.StaleTmpFiles);
        Section("Unreadable folders", report.InaccessiblePaths);
        Section("Mostly incompressible (savings likely small)", report.PoorlyCompressible);
        Section("Marked but empty (no archive)", report.EmptyItems);
        Section("Links/junctions skipped (never followed)", report.SkippedLinks);
    }

    private static void Section(string title, List<string> lines)
    {
        if (lines.Count == 0) return;
        Console.WriteLine($"  {title}: {lines.Count}");
        foreach (string line in lines.Take(10)) Console.WriteLine($"    - {line}");
        if (lines.Count > 10) Console.WriteLine($"    ... and {lines.Count - 10} more");
    }

    private static bool Confirm(bool deletesOriginals)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("\nInput is redirected; pass --yes to start without confirmation.");
            return false;
        }

        Console.Write(deletesOriginals
            ? "\nStart? Original files will be deleted once their archive is verified. [y/N] "
            : "\nStart? [y/N] ");
        string? answer = Console.ReadLine();
        return answer?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    // ── Summary ──────────────────────────────────────────────────────────────────────

    private static void PrintSummary(BackupJob job, TimeSpan elapsed)
    {
        var items = job.Items.Values.ToList();
        var done = items.Where(i => i.State == JobItemState.Done).ToList();
        long before = done.Sum(i => i.Stats?.BytesBefore ?? 0);
        long after = done.Sum(i => i.Stats?.BytesAfter ?? 0);

        Console.WriteLine();
        Console.WriteLine($"Finished in {Format.Duration(elapsed)}: {job.OverallState}");
        foreach (var group in items.GroupBy(i => i.State).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key,-15} {group.Count()}");
        if (done.Count > 0)
            Console.WriteLine($"  Done items: {Format.Bytes(before)} -> {Format.Bytes(after)} ({(before > 0 ? 1 - (double)after / before : 0):P0} saved)");
        if (job.Config.DeleteAfterVerify)
            Console.WriteLine($"  Original files deleted: {items.Sum(i => i.Stats?.FilesDeleted ?? 0):N0}");

        var problems = items.Where(i => i.State is JobItemState.NeedsAttention or JobItemState.Failed).ToList();
        if (problems.Count == 0) return;

        Console.WriteLine("\nNeeds your attention:");
        foreach (JobItem item in problems)
        {
            Console.WriteLine($"  {item.State}: {item.SourcePath}");
            foreach (string line in (item.Error?.Message ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8))
                Console.WriteLine($"      {line}");
        }
    }

    // ── Live Progress ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints a line per finished item, and redraws a one-line status (when the console isn't redirected).
    /// Reports arrive on worker threads, so all writes go through <see cref="ConsoleLock"/>.
    /// </summary>
    private sealed class ConsoleProgress(BackupJob job) : IProgress<JobStateChange>
    {
        private readonly bool _interactive = !Console.IsOutputRedirected;
        private int _statusLength;

        public void Report(JobStateChange change)
        {
            if (change.NewState is JobItemState.InProgress or JobItemState.Verified) return;

            JobItem item = job.Items[change.SourcePath];
            string name = Format.Relative(job.Config.RootPath, item.SourcePath);
            string detail = change.NewState switch
            {
                JobItemState.Done when item.Stats is { } s =>
                    $"{Format.Bytes(s.BytesBefore)} -> {Format.Bytes(s.BytesAfter)} ({s.CompressionRatio:P0} saved, {Format.Duration(s.Duration)})",
                _ => Format.FirstLine(change.Reason)
            };

            lock (ConsoleLock)
            {
                ClearStatusUnlocked();
                Console.WriteLine($"  {Label(change.NewState),-10} {name}  {detail}");
            }
        }

        public void DrawStatus()
        {
            if (!_interactive) return;

            long total = job.TotalBytes;
            long processed = job.TotalBytesProcessed;
            int finished = job.Items.Values.Count(i => i.IsTerminal);
            int running = job.CountByState(JobItemState.InProgress);
            string status = $"  [{(total > 0 ? (double)processed / total : 0),4:P0}] {finished}/{job.Items.Count} finished, {running} running, " +
                            $"{Format.Bytes(processed)} of {Format.Bytes(total)}";

            lock (ConsoleLock)
            {
                Console.Write("\r" + status.PadRight(_statusLength));
                _statusLength = status.Length;
            }
        }

        public void ClearStatus()
        {
            lock (ConsoleLock) ClearStatusUnlocked();
        }

        private void ClearStatusUnlocked()
        {
            if (!_interactive || _statusLength == 0) return;
            Console.Write("\r" + new string(' ', _statusLength) + "\r");
            _statusLength = 0;
        }

        private static string Label(JobItemState state) => state switch
        {
            JobItemState.Done => "done",
            JobItemState.Skipped => "skipped",
            JobItemState.Cancelled => "cancelled",
            JobItemState.NeedsAttention => "ATTENTION",
            JobItemState.Failed => "FAILED",
            _ => state.ToString()
        };
    }
}
