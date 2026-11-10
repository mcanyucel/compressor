using System.Diagnostics;
using Core.Compression;
using Core.Models;
using Core.Strategies;

namespace Core.Execution;

/// <summary>
/// Runs a sealed, pre-flighted <see cref="BackupJob"/>: compresses and verifies items in parallel and moves each
/// <see cref="JobItem"/> through its states.
///
/// Per item: <c>Pending → InProgress → Verified → Done</c> (with <c>DeleteAfterVerify</c>, originals are deleted
/// between <c>Verified</c> and <c>Done</c>), or one of the other terminal states:
/// <list type="bullet">
/// <item><c>Skipped</c>: nothing to compress.</item>
/// <item><c>NeedsAttention</c>: 7-Zip warnings (archive kept as <c>.7z.tmp</c>), failed verification (archive kept as <c>.7z</c>),
/// or some originals not deleted because they changed or couldn't be deleted (archive verified).</item>
/// <item><c>Failed</c>: no archive produced.</item>
/// <item><c>Cancelled</c>: per-item cancel or the global stop.</item>
/// </list>
///
/// One item's failure never stops the others. Only items in <c>Pending</c> are run, so a job can be rerun
/// after resetting the items the user wants to retry.
/// </summary>
public sealed class JobExecutor(ICompressionStrategy strategy)
{
    /// <param name="stateProgress">
    /// Receives every state transition. Create it as a <see cref="Progress{T}"/> on the UI thread so reports are
    /// marshalled there. Error and Stats are written before the terminal state, so they are safe to read on report.
    /// </param>
    /// <param name="ct">The global stop. Running items are cancelled; items not yet started become <c>Cancelled</c>.</param>
    public async Task RunAsync(BackupJob job, IProgress<JobStateChange>? stateProgress, CancellationToken ct)
    {
        if (job.OverallState != JobState.ReadyToExecute)
            throw new InvalidOperationException($"Job must be sealed and pre-flighted before execution (state: {job.OverallState}).");

        // Largest first: long items start early instead of running alone at the end.
        List<JobItem> items = job.Items.Values
            .Where(i => i.State == JobItemState.Pending)
            .OrderByDescending(i => i.TotalBytes)
            .ToList();

        job.MarkExecutionStarted();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = job.Config.ConcurrentItems,
            CancellationToken = ct
        };

        try
        {
            await Parallel.ForEachAsync(items, options,
                async (item, loopCt) => await RunItemAsync(item, job.Config, stateProgress, loopCt));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Parallel.ForEachAsync stops starting new items and throws once running ones finish.
        }

        if (ct.IsCancellationRequested)
        {
            foreach (JobItem item in items.Where(i => i.State == JobItemState.Pending))
                Transition(item, JobItemState.Cancelled, stateProgress, "Job stopped before this item started.");

            job.MarkExecutionCancelled();
        }
        else
        {
            job.MarkExecutionCompleted();
        }
    }

    private async Task RunItemAsync(JobItem item, JobConfig config, IProgress<JobStateChange>? stateProgress, CancellationToken globalCt)
    {
        // Cancelled while waiting in the queue (per-item cancel, or the v2 file watcher).
        if (item.Cts.IsCancellationRequested)
        {
            Transition(item, JobItemState.Cancelled, stateProgress, "Cancelled before it started.");
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(item.Cts.Token, globalCt);
        var stopwatch = Stopwatch.StartNew();

        Transition(item, JobItemState.InProgress, stateProgress);

        try
        {
            CompressionResult compression = await strategy.CompressItemAsync(item, config, linked.Token);

            switch (compression.Outcome)
            {
                case CompressionOutcome.NothingToCompress:
                    Transition(item, JobItemState.Skipped, stateProgress, "No files to compress.");
                    return;

                case CompressionOutcome.CompletedWithWarnings:
                    Fail(item, JobItemState.NeedsAttention, compression.Message ?? "7-Zip reported warnings.", stateProgress);
                    return;

                case CompressionOutcome.Failed:
                    Fail(item, JobItemState.Failed, compression.Message ?? "Compression failed.", stateProgress);
                    return;
            }

            VerificationResult verification = await strategy.VerifyItemAsync(item, compression, linked.Token);
            if (!verification.Passed)
            {
                Fail(item, JobItemState.NeedsAttention,
                    $"Verification failed; the archive was kept for review:\n{string.Join("\n", verification.Problems)}",
                    stateProgress);
                return;
            }

            item.Stats = new JobItemStats
            {
                Duration = stopwatch.Elapsed,
                BytesBefore = compression.Files.Sum(f => f.Size),
                BytesAfter = new FileInfo(item.OutputPath).Length,
                FileCount = compression.Files.Count
            };

            Transition(item, JobItemState.Verified, stateProgress);

            if (config.DeleteAfterVerify)
            {
                // Deliberately not cancellable; see OriginalsDeleter.
                DeletionResult deletion = OriginalsDeleter.Delete(item, compression.Files);
                item.Stats = item.Stats with { FilesDeleted = deletion.Deleted };

                if (!deletion.Complete)
                {
                    Fail(item, JobItemState.NeedsAttention, DescribeKept(deletion), stateProgress);
                    return;
                }
            }

            Transition(item, JobItemState.Done, stateProgress);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // In v2 the file watcher sets NeedsAttention itself before cancelling; keep its state and reason.
            if (item.State != JobItemState.NeedsAttention)
            {
                string reason = item.Cts.IsCancellationRequested ? "Cancelled." : "Job stopped.";
                Transition(item, JobItemState.Cancelled, stateProgress, reason);
            }
        }
        catch (Exception ex)
        {
            Fail(item, JobItemState.Failed, $"Unexpected error: {ex.Message}", stateProgress, ex);
        }
    }

    private static string DescribeKept(DeletionResult deletion)
    {
        const int maxListed = 20;
        var lines = deletion.Kept.Take(maxListed).ToList();
        if (deletion.Kept.Count > maxListed)
            lines.Add($"... and {deletion.Kept.Count - maxListed} more");

        return $"The archive is verified, but {deletion.Kept.Count} original file(s) were kept " +
               $"({deletion.Deleted} deleted):\n{string.Join("\n", lines)}";
    }

    private static void Fail(JobItem item, JobItemState state, string message, IProgress<JobStateChange>? stateProgress, Exception? ex = null)
    {
        item.Error = new JobItemError { Message = message, Exception = ex };
        Transition(item, state, stateProgress, message);
    }

    private static void Transition(JobItem item, JobItemState state, IProgress<JobStateChange>? stateProgress, string? reason = null)
    {
        item.State = state;
        stateProgress?.Report(new JobStateChange { SourcePath = item.SourcePath, NewState = state, Reason = reason });
    }
}
