using System.Diagnostics;
using Core.Models;
using Core.SevenZip;

namespace Tests.Support;

/// <summary>
/// A unique temporary folder, deleted on dispose. Links and junctions are removed without following them.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "compressor-tests", Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Root);

    public string P(string relative) => Path.Combine(Root, relative);

    public string Dir(string relative)
    {
        string path = P(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relative, string content = "data")
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A text file that compresses very well.</summary>
    public string WriteText(string relative, int lines) =>
        WriteFile(relative, string.Join('\n', Enumerable.Range(0, lines).Select(i => $"line {i}")));

    /// <summary>Random bytes: incompressible.</summary>
    public string WriteRandom(string relative, int size, int seed = 1)
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Windows only. Junctions don't need admin rights, unlike symbolic links.</summary>
    public string Junction(string relative, string target)
    {
        string path = P(relative);
        using var process = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{path}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(path), $"Could not create junction {path}");
        return path;
    }

    public void Dispose()
    {
        try { DeleteTree(new DirectoryInfo(Root)); }
        catch { /* best effort; it's a temp folder */ }
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry is DirectoryInfo sub)
            {
                if (sub.LinkTarget is not null) sub.Delete(); // removes the link only
                else DeleteTree(sub);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
        directory.Delete();
    }
}

/// <summary>
/// The 7-Zip used by integration tests: <c>COMPRESSOR_7ZIP</c> if set, else the bundled or installed copy.
/// </summary>
public static class TestSevenZip
{
    private static readonly Lazy<SevenZipExecutable?> Located = new(() =>
        SevenZipLocator.TryLocate(
            new SevenZipLocatorOptions { ConfiguredPath = Environment.GetEnvironmentVariable("COMPRESSOR_7ZIP"), AllowInstalled = true },
            out SevenZipExecutable? executable, out _)
            ? executable
            : null);

    public static SevenZipExecutable? Executable => Located.Value;

    public static SevenZipRunner Runner() =>
        new(Executable ?? throw new InvalidOperationException("7-Zip not found."));
}

/// <summary>
/// A test that runs 7-Zip. Skipped when 7-Zip isn't available (and, with <see cref="WindowsOnly"/>, on other systems).
/// </summary>
public sealed class SevenZipFactAttribute : FactAttribute
{
    public bool WindowsOnly { get; set; }

    public override string? Skip
    {
        get => base.Skip
               ?? (WindowsOnly && !OperatingSystem.IsWindows() ? "Windows only." : null)
               ?? (TestSevenZip.Executable is null ? "7-Zip not found. Install it or set COMPRESSOR_7ZIP." : null);
        set => base.Skip = value;
    }
}

/// <summary>
/// For behavior that only exists on Windows: junctions, mandatory file locks.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public override string? Skip
    {
        get => base.Skip ?? (OperatingSystem.IsWindows() ? null : "Windows only.");
        set => base.Skip = value;
    }
}

public static class TestJobs
{
    public static JobItem Item(string source, ItemKind kind = ItemKind.Directory, long totalBytes = 0) => new()
    {
        SourcePath = source,
        OutputPath = source + ".7z",
        ItemKind = kind,
        TotalBytes = totalBytes
    };

    /// <summary>A sealed job marked as pre-flighted, ready for the executor.</summary>
    public static BackupJob Ready(JobConfig config, IEnumerable<JobItem> items)
    {
        var job = new BackupJob { Config = config };
        foreach (JobItem item in items) job.AddItem(item);
        job.Seal();
        job.MarkPreflightStarted();
        job.MarkPreflightCompleted();
        return job;
    }

    /// <summary>
    /// Holds a file open with no sharing, so other processes (7-Zip) can't read it. Mandatory only on Windows.
    /// </summary>
    public static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
}

/// <summary>Reports synchronously on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
