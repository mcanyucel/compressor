using Core.Models;
using Core.Planning;
using Core.Preflight;
using Tests.Support;

namespace Tests;

public class PreflightRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private BackupJob Build(params string[] compress) => JobBuilder.Build(new JobConfig
    {
        RootPath = _dir.Root,
        Rules = compress.Select(p => new JobRule { RelativePath = p, Action = JobAction.Compress }).ToList()
    });

    [Fact]
    public async Task EstimatesCompressibilityPerItem()
    {
        _dir.WriteText(Path.Combine("Docs", "a.txt"), 1000);
        _dir.WriteRandom(Path.Combine("Photos", "p.jpg"), 10_000);
        BackupJob job = Build("Docs", "Photos");

        PreflightReport report = await PreflightRunner.RunAsync(job);

        Assert.Equal(CompressibilityHint.Good, report.Estimates[_dir.P("Docs")].Hint);
        Assert.Equal(CompressibilityHint.Poor, report.Estimates[_dir.P("Photos")].Hint);
        Assert.Equal([_dir.P("Photos")], report.PoorlyCompressible);
        Assert.Equal(JobState.ReadyToExecute, job.OverallState);
    }

    [WindowsFact]
    public async Task FindsLockedFiles_AndRerunDoesNotDuplicate()
    {
        string locked = _dir.WriteFile(Path.Combine("Docs", "locked.txt"));
        _dir.WriteFile(Path.Combine("Docs", "free.txt"));
        BackupJob job = Build("Docs");

        using (TestJobs.Lock(locked))
        {
            await PreflightRunner.RunAsync(job);
            PreflightReport report = await PreflightRunner.RunAsync(job);
            Assert.Equal([locked], report.LockedFiles);
        }
    }

    [Fact]
    public async Task AddsToTheBuildersReport()
    {
        _dir.WriteFile(Path.Combine("Docs", "a.txt"));
        BackupJob job = JobBuilder.Build(new JobConfig
        {
            RootPath = _dir.Root,
            Rules = [new() { RelativePath = "Docs", Action = JobAction.Compress }, new() { RelativePath = "Missing", Action = JobAction.Compress }]
        });

        PreflightReport report = await PreflightRunner.RunAsync(job);

        Assert.Same(job.Preflight, report);
        Assert.Single(report.InvalidRules);
    }

    [Fact]
    public async Task RequiresASealedJob()
    {
        var job = new BackupJob { Config = new JobConfig { RootPath = _dir.Root } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreflightRunner.RunAsync(job));
    }
}
