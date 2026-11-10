using System.Collections.Concurrent;
using Core.Compression;
using Core.Execution;
using Core.Models;
using Core.Planning;
using Core.Preflight;
using Core.Strategies;
using Tests.Support;

namespace Tests;

public class JobExecutorTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private JobConfig Config => new() { RootPath = _dir.Root };

    private JobItem Item(string name, long bytes = 0) => TestJobs.Item(_dir.P(name), totalBytes: bytes);

    [Fact]
    public async Task MapsEachOutcomeToAState()
    {
        var fake = new FakeStrategy();
        fake.Behaviors["done"] = new(CompressionOutcome.Completed);
        fake.Behaviors["warn"] = new(CompressionOutcome.CompletedWithWarnings, Message: "WARNING: locked");
        fake.Behaviors["fail"] = new(CompressionOutcome.Failed, Message: "7-Zip failed");
        fake.Behaviors["empty"] = new(CompressionOutcome.NothingToCompress);
        fake.Behaviors["badverify"] = new(CompressionOutcome.Completed, VerifyProblem: "Missing from archive: x.txt");
        fake.Behaviors["throws"] = new(CompressionOutcome.Completed, Throw: true);
        var items = new[] { "done", "warn", "fail", "empty", "badverify", "throws", "precancel" }.ToDictionary(n => n, n => Item(n));
        items["precancel"].Cts.Cancel();
        var reports = new ConcurrentQueue<JobStateChange>();
        BackupJob job = TestJobs.Ready(Config, items.Values);

        await new JobExecutor(fake).RunAsync(job, new SyncProgress<JobStateChange>(reports.Enqueue), default);

        Assert.Equal(JobItemState.Done, items["done"].State);
        Assert.Equal(1, items["done"].Stats?.FileCount);
        Assert.Equal([JobItemState.InProgress, JobItemState.Verified, JobItemState.Done],
            reports.Where(r => r.SourcePath == items["done"].SourcePath).Select(r => r.NewState));

        Assert.Equal(JobItemState.NeedsAttention, items["warn"].State);
        Assert.Equal("WARNING: locked", items["warn"].Error?.Message);

        Assert.Equal(JobItemState.Failed, items["fail"].State);
        Assert.Equal(JobItemState.Skipped, items["empty"].State);

        Assert.Equal(JobItemState.NeedsAttention, items["badverify"].State);
        Assert.Contains("Missing from archive", items["badverify"].Error?.Message);

        Assert.Equal(JobItemState.Failed, items["throws"].State);
        Assert.IsType<InvalidOperationException>(items["throws"].Error?.Exception);

        Assert.Equal(JobItemState.Cancelled, items["precancel"].State);
        Assert.DoesNotContain("precancel", fake.Started);

        Assert.Equal(JobState.CompletedWithErrors, job.OverallState);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public async Task RespectsConcurrencyLimit_LargestFirst()
    {
        var fake = new FakeStrategy { Delay = TimeSpan.FromMilliseconds(100) };
        var config = new JobConfig { RootPath = _dir.Root, Parallelism = ParallelismLevel.Maximum };
        int limit = config.ConcurrentItems;
        int count = limit * 3;
        BackupJob job = TestJobs.Ready(config, Enumerable.Range(0, count).Select(i => Item($"c{i:D3}", bytes: i)));

        await new JobExecutor(fake).RunAsync(job, null, default);

        Assert.InRange(fake.MaxConcurrent, 1, limit);
        Assert.All(fake.Started.Take(limit), name => Assert.True(int.Parse(name[1..]) >= count - limit, $"{name} started early"));
        Assert.All(job.Items.Values, i => Assert.Equal(JobItemState.Done, i.State));
        Assert.Equal(JobState.Completed, job.OverallState);
    }

    [Fact]
    public async Task GlobalStop_CancelsRunningAndQueuedItems()
    {
        var fake = new FakeStrategy { Delay = TimeSpan.FromSeconds(5) };
        var config = Config;
        BackupJob job = TestJobs.Ready(config, Enumerable.Range(0, config.ConcurrentItems * 3).Select(i => Item($"g{i:D3}")));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await new JobExecutor(fake).RunAsync(job, null, cts.Token);

        Assert.All(job.Items.Values, i => Assert.Equal(JobItemState.Cancelled, i.State));
        Assert.Equal(config.ConcurrentItems, fake.Started.Count);
        Assert.Equal(JobState.Cancelled, job.OverallState);
    }

    [Fact]
    public async Task ItemCancel_AffectsOnlyThatItem()
    {
        var fake = new FakeStrategy { Delay = TimeSpan.FromMilliseconds(500) };
        var items = Enumerable.Range(0, 3).Select(i => Item($"p{i}")).ToList();
        BackupJob job = TestJobs.Ready(Config, items);

        Task run = new JobExecutor(fake).RunAsync(job, null, default);
        await Task.Delay(100);
        await items[1].Cts.CancelAsync();
        await run;

        Assert.Equal(JobItemState.Cancelled, items[1].State);
        Assert.Equal(JobItemState.Done, items[0].State);
        Assert.Equal(JobItemState.Done, items[2].State);
        Assert.Equal(JobState.CompletedWithErrors, job.OverallState);
    }

    [Fact]
    public async Task RequiresPreflight()
    {
        var notPreflighted = new BackupJob { Config = Config };
        notPreflighted.Seal();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobExecutor(new FakeStrategy()).RunAsync(notPreflighted, null, default));
    }

    // ── Delete after verify ──────────────────────────────────────────────────────────

    private JobConfig DeletingConfig => new() { RootPath = _dir.Root, DeleteAfterVerify = true };

    [Fact]
    public async Task DeleteAfterVerify_DeletesOriginals_ThenDone()
    {
        _dir.WriteFile(Path.Combine("docs", "a.txt"));
        _dir.WriteFile(Path.Combine("docs", "sub", "b.txt"));
        var fake = new FakeStrategy();
        fake.Behaviors["docs"] = new(CompressionOutcome.Completed, RealFiles: true);
        JobItem item = Item("docs");

        await new JobExecutor(fake).RunAsync(TestJobs.Ready(DeletingConfig, [item]), null, default);

        Assert.Equal(JobItemState.Done, item.State);
        Assert.Equal(2, item.Stats?.FilesDeleted);
        Assert.False(Directory.Exists(_dir.P("docs")));
    }

    [Fact]
    public async Task DeleteAfterVerify_FailedVerification_DeletesNothing()
    {
        string a = _dir.WriteFile(Path.Combine("docs", "a.txt"));
        var fake = new FakeStrategy();
        fake.Behaviors["docs"] = new(CompressionOutcome.Completed, VerifyProblem: "CRC Failed", RealFiles: true);
        JobItem item = Item("docs");

        await new JobExecutor(fake).RunAsync(TestJobs.Ready(DeletingConfig, [item]), null, default);

        Assert.Equal(JobItemState.NeedsAttention, item.State);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task DeleteAfterVerify_FileChangedAfterArchiving_IsKept_NeedsAttention()
    {
        string a = _dir.WriteFile(Path.Combine("docs", "a.txt"));
        string b = _dir.WriteFile(Path.Combine("docs", "b.txt"));
        var fake = new FakeStrategy();
        fake.Behaviors["docs"] = new(CompressionOutcome.Completed, RealFiles: true, ChangeDuringVerify: "b.txt");
        JobItem item = Item("docs");

        await new JobExecutor(fake).RunAsync(TestJobs.Ready(DeletingConfig, [item]), null, default);

        Assert.Equal(JobItemState.NeedsAttention, item.State);
        Assert.Contains("b.txt: changed since it was archived", item.Error?.Message);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal(1, item.Stats?.FilesDeleted);
    }

    [SevenZipFact]
    public async Task EndToEnd_DeleteAfterVerify()
    {
        _dir.WriteText(Path.Combine("Docs", "a.txt"), 5_000);
        _dir.WriteFile(Path.Combine("Docs", "sub", "b.txt"));
        _dir.Dir(Path.Combine("Docs", "empty-before"));
        _dir.WriteFile(Path.Combine("Projects", "src", "main.cs"));
        string skipped = _dir.WriteFile(Path.Combine("Projects", "bin", "app.dll"));
        var config = new JobConfig
        {
            RootPath = _dir.Root,
            DeleteAfterVerify = true,
            Rules =
            [
                new() { RelativePath = "Docs", Action = JobAction.Compress },
                new() { RelativePath = "Projects", Action = JobAction.Compress },
                new() { RelativePath = Path.Combine("Projects", "bin"), Action = JobAction.Skip }
            ]
        };

        BackupJob job = JobBuilder.Build(config);
        await PreflightRunner.RunAsync(job);
        await new JobExecutor(new SevenZipStrategy(TestSevenZip.Runner())).RunAsync(job, null, default);

        Assert.Equal(JobState.Completed, job.OverallState);

        // Docs: files gone, the folder that was already empty stays, the archive has the files.
        Assert.Equal([_dir.P(Path.Combine("Docs", "empty-before"))], Directory.GetFileSystemEntries(_dir.P("Docs")));
        var docs = await TestSevenZip.Runner().ListAsync(_dir.P("Docs.7z"), default);
        Assert.Equal(["a.txt", Path.Combine("sub", "b.txt")], docs.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).Order());

        // Projects: src archived and deleted; the skipped bin folder is untouched.
        Assert.False(Directory.Exists(_dir.P(Path.Combine("Projects", "src"))));
        Assert.True(File.Exists(skipped));
        Assert.True(File.Exists(_dir.P("Projects.7z")));
    }

    /// <summary>
    /// The whole pipeline against real 7-Zip: rules → builder → pre-flight → executor.
    /// </summary>
    [SevenZipFact]
    public async Task EndToEnd()
    {
        _dir.WriteText(Path.Combine("Docs", "a.txt"), 20_000);
        _dir.WriteFile(Path.Combine("Projects", "src", "main.cs"));
        _dir.WriteRandom(Path.Combine("Projects", "bin", "app.dll"), 50_000);
        _dir.Dir("Empty");
        var config = new JobConfig
        {
            RootPath = _dir.Root,
            Rules =
            [
                new() { RelativePath = "Docs", Action = JobAction.Compress },
                new() { RelativePath = "Projects", Action = JobAction.Compress },
                new() { RelativePath = Path.Combine("Projects", "bin"), Action = JobAction.Skip },
                new() { RelativePath = "Empty", Action = JobAction.Compress }
            ]
        };

        BackupJob job = JobBuilder.Build(config);
        await PreflightRunner.RunAsync(job);
        await new JobExecutor(new SevenZipStrategy(TestSevenZip.Runner())).RunAsync(job, null, default);

        Assert.Equal(JobState.Completed, job.OverallState);
        Assert.Equal(2, job.Items.Count);
        Assert.Contains(_dir.P("Empty"), job.Preflight!.EmptyItems);

        var projects = await TestSevenZip.Runner().ListAsync(_dir.P("Projects.7z"), default);
        Assert.Equal(Path.Combine("src", "main.cs"), Assert.Single(projects.Entries).Path);

        JobItem docs = job.Items[_dir.P("Docs")];
        Assert.True(docs.Stats!.BytesAfter < docs.Stats.BytesBefore / 5);
    }

    /// <param name="RealFiles">Report the item's real files as archived (needed for deletion tests).</param>
    /// <param name="ChangeDuringVerify">A file (relative to the item) to modify during verification.</param>
    private sealed record Behavior(
        CompressionOutcome Outcome, string? Message = null, string? VerifyProblem = null, bool Throw = false,
        bool RealFiles = false, string? ChangeDuringVerify = null);

    /// <summary>
    /// Records start order and peak concurrency; outcomes are configured per item folder name.
    /// </summary>
    private sealed class FakeStrategy : ICompressionStrategy
    {
        public Dictionary<string, Behavior> Behaviors { get; } = [];
        public TimeSpan Delay { get; init; }
        public ConcurrentQueue<string> Started { get; } = new();
        public int MaxConcurrent => _maxConcurrent;

        private int _running;
        private int _maxConcurrent;

        public async Task<CompressionResult> CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct)
        {
            string name = Path.GetFileName(item.SourcePath);
            Started.Enqueue(name);
            int now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = _maxConcurrent) && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen) { }

            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                Behavior behavior = Behaviors.GetValueOrDefault(name) ?? new Behavior(CompressionOutcome.Completed);
                if (behavior.Throw) throw new InvalidOperationException("boom");
                if (behavior.Outcome == CompressionOutcome.Completed) await File.WriteAllTextAsync(item.OutputPath, "archive", ct);

                List<ArchivedFile> files = behavior.RealFiles
                    ? CompressionHelpers.CollectFiles(item)
                        .Select(f => new ArchivedFile(Path.GetRelativePath(item.SourcePath, f.FullName), f.Length, f.LastWriteTimeUtc))
                        .ToList()
                    : [new ArchivedFile("x.txt", 10, DateTime.UtcNow)];

                return new CompressionResult(behavior.Outcome, behavior.Message) { Files = files };
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<VerificationResult> VerifyItemAsync(JobItem item, CompressionResult compression, CancellationToken ct)
        {
            Behavior? behavior = Behaviors.GetValueOrDefault(Path.GetFileName(item.SourcePath));
            if (behavior?.ChangeDuringVerify is { } relative)
                File.AppendAllText(Path.Combine(item.SourcePath, relative), " changed");

            return Task.FromResult(behavior?.VerifyProblem is { } problem ? new VerificationResult([problem]) : VerificationResult.Success);
        }
    }
}
