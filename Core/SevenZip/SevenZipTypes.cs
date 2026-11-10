namespace Core.SevenZip;

/// <summary>
/// Where a 7-Zip executable was found.
/// </summary>
public enum SevenZipSource
{
    /// <summary>Path set explicitly by the user in settings.</summary>
    Configured,
    /// <summary>The binary shipped with the app, verified against <c>SHA256SUMS</c>.</summary>
    Bundled,
    /// <summary>A copy of 7-Zip installed on the machine.</summary>
    Installed
}

public sealed record SevenZipExecutable(string Path, SevenZipSource Source);

/// <summary>
/// 7-Zip's documented exit codes.
/// </summary>
public enum SevenZipExitCode
{
    Success = 0,
    /// <summary>Non-fatal, e.g. some input files could not be opened.</summary>
    Warning = 1,
    FatalError = 2,
    CommandLineError = 7,
    OutOfMemory = 8,
    UserStopped = 255
}

public sealed record SevenZipResult(int ExitCode, string ErrorOutput)
{
    /// <summary>Unknown exit codes are treated as fatal.</summary>
    public SevenZipExitCode Status => Enum.IsDefined(typeof(SevenZipExitCode), ExitCode)
        ? (SevenZipExitCode)ExitCode
        : SevenZipExitCode.FatalError;

    public bool Succeeded => Status == SevenZipExitCode.Success;
}

/// <summary>
/// Input for <see cref="SevenZipRunner.AddAsync"/>.
/// </summary>
public sealed class SevenZipAddRequest
{
    /// <summary>
    /// Absolute path of the archive to create, normally <c>&lt;output&gt;.7z.tmp</c>. Must not exist yet:
    /// 7-Zip would otherwise update the existing archive instead of creating a new one.
    /// </summary>
    public required string ArchivePath { get; init; }

    /// <summary>
    /// Absolute path that <see cref="Files"/> are relative to. Paths are stored in the archive relative to this folder.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Files to add, relative to <see cref="WorkingDirectory"/>. Taken literally (no wildcard matching).
    /// </summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>7-Zip <c>-mx</c>, 0 (store) to 9 (ultra).</summary>
    public int CompressionLevel { get; init; } = 5;

    /// <summary>7-Zip <c>-mmt</c>, at least 1.</summary>
    public int Threads { get; init; } = 1;

    /// <summary>
    /// Pass <c>-sse</c>: fail instead of warning when an input file can't be opened.
    /// </summary>
    public bool StopOnUnreadableFile { get; init; }
}

/// <summary>
/// One entry from <c>7z l -slt</c>.
/// </summary>
public sealed record SevenZipEntry(string Path, bool IsDirectory, long Size, string? Crc);

public sealed record SevenZipListResult(SevenZipResult Result, IReadOnlyList<SevenZipEntry> Entries);
