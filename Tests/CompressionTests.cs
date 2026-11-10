using Core.Compression;
using Core.FileSystem;
using Core.Models;
using Core.SevenZip;
using Core.Strategies;
using Tests.Support;

namespace Tests;

public class VerificationUnitTests
{
    private static ArchivedFile F(string path, long size) => new(path, size, DateTime.UtcNow);
    private static SevenZipEntry E(string path, long size, bool dir = false) => new(path, dir, size, null);

    [Fact]
    public void ExactMatch_Passes_DirectoriesIgnored()
    {
        var result = VerificationHelpers.CompareContents(
            [F("a.txt", 1), F(Path.Combine("sub", "b.txt"), 2)],
            [E("a.txt", 1), E("sub", 0, dir: true), E(Path.Combine("sub", "b.txt"), 2)]);

        Assert.True(result.Passed);
    }

    [Fact]
    public void ReportsMissingUnexpectedAndSizeMismatch()
    {
        var result = VerificationHelpers.CompareContents(
            [F("missing.txt", 1), F("changed.txt", 10), F("ok.txt", 3)],
            [E("changed.txt", 12), E("ok.txt", 3), E("stray.txt", 5)]);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Problems.Count);
        Assert.Contains("Missing from archive: missing.txt", result.Problems);
        Assert.Contains("Unexpected file in archive: stray.txt", result.Problems);
        Assert.Contains(result.Problems, p => p.StartsWith("Size mismatch for changed.txt"));
    }

    [Fact]
    public void DuplicateEntries_AreReported()
    {
        var result = VerificationHelpers.CompareContents([F("a.txt", 1)], [E("a.txt", 1), E("a.txt", 1)]);
        Assert.Contains("Duplicate entry in archive: a.txt", result.Problems);
    }

    [WindowsFact]
    public void SeparatorAndCase_DoNotMatterOnWindows()
    {
        var result = VerificationHelpers.CompareContents([F("SUB/A.TXT", 1)], [E(@"sub\a.txt", 1)]);
        Assert.True(result.Passed);
    }
}

/// <summary>
/// Compression and verification against real 7-Zip.
/// </summary>
public class CompressionIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly JobConfig Config = new() { RootPath = Path.GetTempPath() };

    private static SevenZipStrategy Strategy() => new(TestSevenZip.Runner());

    [SevenZipFact]
    public async Task Directory_CompressesAndVerifies()
    {
        string docs = _dir.Dir("docs");
        _dir.WriteText(Path.Combine("docs", "a.txt"), 10_000);
        _dir.WriteFile(Path.Combine("docs", "deep", "ünï.txt"), "b");
        _dir.WriteFile(Path.Combine("docs", "empty.txt"), "");
        _dir.WriteFile(Path.Combine("docs", "~$lock.docx"), "filtered");
        JobItem item = TestJobs.Item(docs);
        var strategy = Strategy();

        CompressionResult result = await strategy.CompressItemAsync(item, Config, default);
        VerificationResult verification = await strategy.VerifyItemAsync(item, result, default);

        Assert.Equal(CompressionOutcome.Completed, result.Outcome);
        Assert.Equal(["a.txt", Path.Combine("deep", "ünï.txt"), "empty.txt"], result.Files.Select(f => f.RelativePath).Order());
        Assert.True(File.Exists(item.OutputPath));
        Assert.False(File.Exists(item.OutputPath + ".tmp"));
        Assert.True(verification.Passed, string.Join("\n", verification.Problems));
        Assert.Equal(3, item.FilesProcessed);
    }

    [SevenZipFact]
    public async Task FileItem_StoresJustTheFileName()
    {
        string notes = _dir.WriteText("notes.txt", 100);
        JobItem item = TestJobs.Item(notes, ItemKind.File);

        CompressionResult result = await Strategy().CompressItemAsync(item, Config, default);
        SevenZipListResult list = await TestSevenZip.Runner().ListAsync(item.OutputPath, default);

        Assert.Equal(CompressionOutcome.Completed, result.Outcome);
        Assert.Equal("notes.txt", Assert.Single(list.Entries).Path);
    }

    [SevenZipFact]
    public async Task ExcludedPaths_AreLeftOut()
    {
        string projects = _dir.Dir("projects");
        _dir.WriteFile(Path.Combine("projects", "src", "a.cs"));
        _dir.WriteFile(Path.Combine("projects", "bin", "app.dll"));
        JobItem item = new()
        {
            SourcePath = projects, OutputPath = projects + ".7z", ItemKind = ItemKind.Directory,
            ExcludedPaths = new HashSet<string>(Paths.Comparer) { _dir.P(Path.Combine("projects", "bin")) }
        };

        CompressionResult result = await Strategy().CompressItemAsync(item, Config, default);

        Assert.Equal([Path.Combine("src", "a.cs")], result.Files.Select(f => f.RelativePath));
    }

    [SevenZipFact]
    public async Task EmptyFolder_NothingToCompress()
    {
        JobItem item = TestJobs.Item(_dir.Dir("empty"));

        CompressionResult result = await Strategy().CompressItemAsync(item, Config, default);

        Assert.Equal(CompressionOutcome.NothingToCompress, result.Outcome);
        Assert.False(File.Exists(item.OutputPath));
    }

    [SevenZipFact]
    public async Task LeftoverTmp_FailsAndIsLeftUntouched()
    {
        string folder = _dir.Dir("old");
        _dir.WriteFile(Path.Combine("old", "o.txt"));
        string tmp = _dir.WriteFile("old.7z.tmp", "stale");

        CompressionResult result = await Strategy().CompressItemAsync(TestJobs.Item(folder), Config, default);

        Assert.Equal(CompressionOutcome.Failed, result.Outcome);
        Assert.Equal("stale", await File.ReadAllTextAsync(tmp));
    }

    [SevenZipFact]
    public async Task VanishedSource_Fails()
    {
        CompressionResult result = await Strategy().CompressItemAsync(TestJobs.Item(_dir.P("gone")), Config, default);
        Assert.Equal(CompressionOutcome.Failed, result.Outcome);
    }

    [SevenZipFact(WindowsOnly = true)]
    public async Task LockedFile_Skip_KeepsTmpForReview()
    {
        string folder = _dir.Dir("docs");
        string locked = _dir.WriteFile(Path.Combine("docs", "locked.txt"));
        _dir.WriteFile(Path.Combine("docs", "free.txt"));
        JobItem item = TestJobs.Item(folder);

        using (TestJobs.Lock(locked))
        {
            CompressionResult result = await Strategy().CompressItemAsync(item, Config, default);

            Assert.Equal(CompressionOutcome.CompletedWithWarnings, result.Outcome);
            Assert.True(File.Exists(item.OutputPath + ".tmp"));
            Assert.False(File.Exists(item.OutputPath));
        }
    }

    [SevenZipFact(WindowsOnly = true)]
    public async Task LockedFile_Abort_FailsWithoutArchive()
    {
        string folder = _dir.Dir("docs");
        string locked = _dir.WriteFile(Path.Combine("docs", "locked.txt"));
        JobItem item = TestJobs.Item(folder);
        var config = new JobConfig { RootPath = _dir.Root, LockedFileBehavior = LockedFileBehavior.AbortSubJob };

        using (TestJobs.Lock(locked))
        {
            CompressionResult result = await Strategy().CompressItemAsync(item, config, default);

            Assert.Equal(CompressionOutcome.Failed, result.Outcome);
            Assert.False(File.Exists(item.OutputPath + ".tmp"));
        }
    }

    [SevenZipFact]
    public async Task Cancel_RemovesPartialArchive()
    {
        string big = _dir.WriteRandom("big.bin", 60_000_000);
        JobItem item = TestJobs.Item(big, ItemKind.File);
        var config = new JobConfig { RootPath = _dir.Root, CompressionLevel = 9, Parallelism = ParallelismLevel.Minimum };

        Task<CompressionResult> task = Strategy().CompressItemAsync(item, config, item.Cts.Token);
        while (Interlocked.Read(ref item.BytesProcessed) == 0 && !task.IsCompleted) await Task.Delay(10);
        await item.Cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(item.OutputPath + ".tmp"));
        Assert.False(File.Exists(item.OutputPath));
    }

    [SevenZipFact]
    public async Task Verify_DetectsCorruption()
    {
        string folder = _dir.Dir("data");
        _dir.WriteRandom(Path.Combine("data", "r.bin"), 300_000);
        JobItem item = TestJobs.Item(folder);
        var strategy = Strategy();
        CompressionResult result = await strategy.CompressItemAsync(item, Config, default);

        byte[] bytes = await File.ReadAllBytesAsync(item.OutputPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(item.OutputPath, bytes);

        VerificationResult verification = await strategy.VerifyItemAsync(item, result, default);
        Assert.False(verification.Passed);
        Assert.StartsWith("Integrity test failed", verification.Problems[0]);
    }

    [SevenZipFact]
    public async Task Verify_MissingArchive()
    {
        var result = await VerificationHelpers.VerifyAsync(_dir.P("nope.7z"), [], TestSevenZip.Runner(), default);
        Assert.StartsWith("Archive not found", Assert.Single(result.Problems));
    }
}
