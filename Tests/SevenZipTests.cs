using System.Security.Cryptography;
using Core.SevenZip;
using Tests.Support;

namespace Tests;

public class SevenZipLocatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string BinaryRelative => SevenZipLocator.GetBundledBinaryRelativePath()!;

    /// <summary>A fake bundled binary; the hash check doesn't care what's in it.</summary>
    private string WriteBundle(string? checksumLine)
    {
        string bundle = _dir.Dir("bundle");
        _dir.WriteFile(Path.Combine("bundle", BinaryRelative.Replace('/', Path.DirectorySeparatorChar)), "fake 7-zip binary");
        if (checksumLine is not null)
            File.WriteAllText(Path.Combine(bundle, SevenZipLocator.ChecksumFileName), checksumLine + "\n");
        return bundle;
    }

    private string HashOfFakeBinary() =>
        Convert.ToHexString(SHA256.HashData("fake 7-zip binary"u8.ToArray())).ToLowerInvariant();

    [Fact]
    public void Configured_ExistingFile()
    {
        string exe = _dir.WriteFile("7z.exe");
        Assert.True(SevenZipLocator.TryLocate(new() { ConfiguredPath = exe }, out var found, out _));
        Assert.Equal(SevenZipSource.Configured, found.Source);
    }

    [Fact]
    public void Configured_MissingFile_IsAnError_NoFallback()
    {
        Assert.False(SevenZipLocator.TryLocate(new() { ConfiguredPath = _dir.P("nope.exe"), AllowInstalled = true }, out _, out string? error));
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void Bundled_HashMatches()
    {
        string bundle = WriteBundle($"{HashOfFakeBinary()} *{BinaryRelative}"); // '*' binary marker, lower-case hex

        Assert.True(SevenZipLocator.TryLocate(new() { BundleDirectory = bundle }, out var found, out string? error), error);
        Assert.Equal(SevenZipSource.Bundled, found.Source);
    }

    [Fact]
    public void Bundled_HashMismatch_IsAnError_NoFallback()
    {
        string bundle = WriteBundle($"{new string('0', 64)}  {BinaryRelative}");

        Assert.False(SevenZipLocator.TryLocate(new() { BundleDirectory = bundle, AllowInstalled = true }, out _, out string? error));
        Assert.Contains("Checksum mismatch", error);
    }

    [Fact]
    public void Bundled_ChecksumFileMissing_IsAnError()
    {
        string bundle = WriteBundle(checksumLine: null);

        Assert.False(SevenZipLocator.TryLocate(new() { BundleDirectory = bundle }, out _, out string? error));
        Assert.Contains("Checksum file missing", error);
    }

    [Fact]
    public void NoBundle_InstalledNotAllowed_IsAnError()
    {
        Assert.False(SevenZipLocator.TryLocate(new() { BundleDirectory = _dir.Dir("empty") }, out _, out string? error));
        Assert.Contains("7-Zip not found", error);
    }
}

public class SevenZipRunnerUnitTests
{
    // Validation happens before 7-Zip starts, so a fake executable is fine.
    private static readonly SevenZipRunner Runner = new(new SevenZipExecutable("not-run.exe", SevenZipSource.Configured));
    private static readonly string Temp = Path.GetTempPath();

    private static SevenZipAddRequest Request(
        string? archive = null, IReadOnlyList<string>? files = null, int level = 5, int threads = 1) => new()
    {
        ArchivePath = archive ?? Path.Combine(Temp, $"never-{Guid.NewGuid():N}.7z.tmp"),
        WorkingDirectory = Temp,
        Files = files ?? ["a.txt"],
        CompressionLevel = level,
        Threads = threads
    };

    [Fact]
    public async Task RejectsInvalidRequests()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Runner.AddAsync(Request(archive: "relative.7z"), null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Runner.AddAsync(Request(files: []), null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Runner.AddAsync(Request(files: [Path.Combine(Temp, "abs.txt")]), null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Runner.AddAsync(Request(files: ["line\nbreak.txt"]), null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Runner.AddAsync(Request(level: 10), null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Runner.AddAsync(Request(threads: 0), null, default));
    }

    [Fact]
    public async Task RefusesToUpdateAnExistingArchive()
    {
        string existing = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Runner.AddAsync(Request(archive: existing), null, default));
        }
        finally { File.Delete(existing); }
    }

    [Theory]
    [InlineData(" 43% + big.bin\r", 43)]
    [InlineData("  0M Scan \r          \r  0%\r", 0)]
    [InlineData(" 19% + a\r 43% + a\r 66% + a\r", 66)]
    [InlineData("100%", 100)]
    [InlineData(" 28M 1 Scan  sub\\\r", null)]
    [InlineData("250%", null)]
    public void ProgressParser_LastPercentage(string chunk, int? expected) =>
        Assert.Equal(expected, new SevenZipRunner.ProgressParser().Feed(chunk.AsMemory()));

    [Fact]
    public void ProgressParser_NumberSplitAcrossReads()
    {
        var parser = new SevenZipRunner.ProgressParser();
        Assert.Null(parser.Feed(" 4".AsMemory()));
        Assert.Equal(43, parser.Feed("3% + file\r".AsMemory()));
    }

    [Fact]
    public void ParseTechnicalListing()
    {
        const string output = """

            7-Zip 26.06 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-11-06

            Listing archive: out.7z

            --
            Path = out.7z
            Type = 7z
            Physical Size = 32

            ----------
            Path = big.bin
            Size = 30000000
            Packed Size = 30061773
            Attributes = A
            CRC = 79E82CAA

            Path = sub
            Size = 0
            Attributes = D

            Path = sub\empty.txt
            Size =
            Attributes = A
            CRC =

            """;

        var entries = SevenZipRunner.ParseTechnicalListing(output.Replace("\n", "\r\n"));

        Assert.Equal(3, entries.Count); // the archive's own "Path = out.7z" block is not an entry
        Assert.Equal(new SevenZipEntry("big.bin", false, 30_000_000, "79E82CAA"), entries[0]);
        Assert.True(entries[1].IsDirectory);
        Assert.Equal(new SevenZipEntry(@"sub\empty.txt", false, 0, null), entries[2]);
    }

    [Theory]
    [InlineData(0, SevenZipExitCode.Success)]
    [InlineData(1, SevenZipExitCode.Warning)]
    [InlineData(255, SevenZipExitCode.UserStopped)]
    [InlineData(42, SevenZipExitCode.FatalError)] // unknown codes count as fatal
    public void ExitCodeMapping(int code, SevenZipExitCode expected) =>
        Assert.Equal(expected, new SevenZipResult(code, "").Status);
}

public class SevenZipRunnerIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private SevenZipAddRequest Request(string archiveName, params string[] files) => new()
    {
        ArchivePath = _dir.P(archiveName),
        WorkingDirectory = _dir.P("src"),
        Files = files
    };

    [SevenZipFact]
    public async Task Add_List_Test_RoundTrip()
    {
        _dir.WriteText(Path.Combine("src", "a.txt"), 50_000);
        _dir.WriteFile(Path.Combine("src", "sub", "ünï çödé.txt"), "héllo");
        var runner = TestSevenZip.Runner();
        var progress = new List<int>();

        SevenZipResult add = await runner.AddAsync(Request("out.7z", "a.txt", Path.Combine("sub", "ünï çödé.txt")),
            new SyncProgress<int>(progress.Add), default);
        SevenZipListResult list = await runner.ListAsync(_dir.P("out.7z"), default);
        SevenZipResult test = await runner.TestAsync(_dir.P("out.7z"), default);

        Assert.True(add.Succeeded, add.ErrorOutput);
        Assert.All(progress, p => Assert.InRange(p, 0, 100));
        Assert.Equal(["a.txt", Path.Combine("sub", "ünï çödé.txt")], list.Entries.Select(e => e.Path).Order());
        Assert.True(test.Succeeded, test.ErrorOutput);
    }

    [SevenZipFact]
    public async Task Test_DetectsCorruption()
    {
        _dir.WriteRandom(Path.Combine("src", "r.bin"), 200_000);
        var runner = TestSevenZip.Runner();
        await runner.AddAsync(Request("out.7z", "r.bin"), null, default);

        byte[] bytes = await File.ReadAllBytesAsync(_dir.P("out.7z"));
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(_dir.P("out.7z"), bytes);

        SevenZipResult test = await runner.TestAsync(_dir.P("out.7z"), default);
        Assert.Equal(SevenZipExitCode.FatalError, test.Status);
    }

    [SevenZipFact]
    public async Task MissingFile_IsAWarning_EvenWithStopOnUnreadable()
    {
        _dir.WriteFile(Path.Combine("src", "a.txt"));
        var runner = TestSevenZip.Runner();

        SevenZipResult plain = await runner.AddAsync(Request("a.7z", "a.txt", "nope.txt"), null, default);
        SevenZipResult sse = await runner.AddAsync(new SevenZipAddRequest
        {
            ArchivePath = _dir.P("b.7z"), WorkingDirectory = _dir.P("src"), Files = ["a.txt", "nope.txt"], StopOnUnreadableFile = true
        }, null, default);

        Assert.Equal(SevenZipExitCode.Warning, plain.Status);
        Assert.Contains("nope.txt", plain.ErrorOutput);
        Assert.Equal(SevenZipExitCode.Warning, sse.Status);
    }

    [SevenZipFact(WindowsOnly = true)]
    public async Task LockedFile_WarningByDefault_FatalWithStopOnUnreadable()
    {
        string locked = _dir.WriteFile(Path.Combine("src", "locked.txt"));
        _dir.WriteFile(Path.Combine("src", "free.txt"));
        var runner = TestSevenZip.Runner();

        using (TestJobs.Lock(locked))
        {
            SevenZipResult plain = await runner.AddAsync(Request("a.7z", "locked.txt", "free.txt"), null, default);
            SevenZipAddRequest stop = Request("b.7z", "locked.txt", "free.txt");
            SevenZipResult sse = await runner.AddAsync(new SevenZipAddRequest
            {
                ArchivePath = stop.ArchivePath, WorkingDirectory = stop.WorkingDirectory, Files = stop.Files, StopOnUnreadableFile = true
            }, null, default);

            Assert.Equal(SevenZipExitCode.Warning, plain.Status);
            Assert.Equal(SevenZipExitCode.FatalError, sse.Status);
            Assert.False(File.Exists(_dir.P("b.7z")));
        }
    }

    [SevenZipFact]
    public async Task Cancel_KillsProcess_AndReleasesTheArchive()
    {
        _dir.WriteRandom(Path.Combine("src", "big.bin"), 60_000_000);
        var runner = TestSevenZip.Runner();
        using var cts = new CancellationTokenSource();
        var request = new SevenZipAddRequest
        {
            ArchivePath = _dir.P("big.7z.tmp"), WorkingDirectory = _dir.P("src"), Files = ["big.bin"], CompressionLevel = 9, Threads = 1
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.AddAsync(request, new SyncProgress<int>(p => { if (p >= 5) cts.Cancel(); }), cts.Token));

        // The process is gone and its handle closed, so the partial archive can be deleted.
        if (File.Exists(request.ArchivePath)) File.Delete(request.ArchivePath);
        Assert.False(File.Exists(request.ArchivePath));
    }
}
