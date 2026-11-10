using Core.Models;
using Core.SevenZip;

namespace Core.Compression;

/// <summary>
/// Shared compression logic. All strategies delegate here.
/// </summary>
public static class CompressionHelpers
{
    private static readonly string[] LockFilePatterns = ["~$*", "*.tmp", "*.lck", "*.lock"];

    /// <summary>
    /// Compresses <paramref name="item"/> with 7-Zip into <c>&lt;OutputPath&gt;.tmp</c>, then renames it to <see cref="JobItem.OutputPath"/>.
    /// Does not change <see cref="JobItem.State"/>; the executor does that based on the returned outcome.
    /// Throws <see cref="OperationCanceledException"/> on cancellation, after deleting the partial archive.
    /// </summary>
    public static async Task<CompressionResult> CompressAsync(
        JobItem item, JobConfig config, SevenZipRunner runner, CancellationToken ct)
    {
        string tmpPath = $"{item.OutputPath}.tmp";

        // A leftover from an interrupted run. 7-Zip would append to it, and deleting it is the user's call.
        if (File.Exists(tmpPath))
            return new CompressionResult(CompressionOutcome.Failed, $"A temporary archive from an earlier run already exists: {tmpPath}");

        string sourceRoot = item.ItemKind == ItemKind.Directory
            ? item.SourcePath
            : Path.GetDirectoryName(item.SourcePath)
              ?? throw new InvalidOperationException($"File item has no parent directory: {item.SourcePath}");

        List<ArchivedFile> files;
        try
        {
            files = await Task.Run(() => CollectFiles(item)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new ArchivedFile(Path.GetRelativePath(sourceRoot, path), info.Length, info.LastWriteTimeUtc);
                })
                .ToList(), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder we can't read, or a file that disappeared between listing and reading its size.
            return new CompressionResult(CompressionOutcome.Failed, $"Could not read the source files: {ex.Message}");
        }

        if (files.Count == 0)
            return new CompressionResult(CompressionOutcome.NothingToCompress);

        long totalBytes = item.TotalBytes > 0 ? item.TotalBytes : files.Sum(f => f.Size);

        var request = new SevenZipAddRequest
        {
            ArchivePath = tmpPath,
            WorkingDirectory = sourceRoot,
            Files = files.Select(f => f.RelativePath).ToList(),
            CompressionLevel = config.CompressionLevel,
            Threads = config.ThreadsPerItem,
            StopOnUnreadableFile = config.LockedFileBehavior == LockedFileBehavior.AbortSubJob
        };

        // 7-Zip reports a percentage only; translate it to bytes for the UI.
        var progress = new InlineProgress(percent =>
            Interlocked.Exchange(ref item.BytesProcessed, totalBytes * percent / 100));

        SevenZipResult result;
        try
        {
            result = await runner.AddAsync(request, progress, ct);
        }
        catch
        {
            CleanupTmp(item); // cancellation or failure to start 7-Zip
            throw;
        }

        switch (result.Status)
        {
            case SevenZipExitCode.Success:
                break;

            case SevenZipExitCode.Warning:
                return new CompressionResult(CompressionOutcome.CompletedWithWarnings,
                    $"{result.ErrorOutput}\nThe archive was kept as {tmpPath} for review.");

            default:
                CleanupTmp(item);
                return new CompressionResult(CompressionOutcome.Failed,
                    $"7-Zip failed ({result.Status}, exit code {result.ExitCode}): {result.ErrorOutput}");
        }

        try
        {
            // Output path is collision-resolved at job-build time, but something may have appeared there since.
            File.Move(tmpPath, item.OutputPath, overwrite: false);
        }
        catch (IOException ex)
        {
            return new CompressionResult(CompressionOutcome.Failed,
                $"Could not rename {tmpPath} to {item.OutputPath}: {ex.Message}. The archive was kept as {tmpPath}.");
        }

        Interlocked.Exchange(ref item.BytesProcessed, totalBytes);
        Interlocked.Exchange(ref item.FilesProcessed, files.Count);
        return new CompressionResult(CompressionOutcome.Completed) { Files = files };
    }

    /// <summary>
    /// Calls the handler on the reporting thread. Unlike <see cref="Progress{T}"/>, it doesn't post to a
    /// synchronization context, which is right for updating the thread-safe <see cref="JobItem"/> counters.
    /// </summary>
    private sealed class InlineProgress(Action<int> handler) : IProgress<int>
    {
        public void Report(int value) => handler(value);
    }

    public static IReadOnlyList<string> CollectFiles(JobItem item)
    {
        IEnumerable<string> candidates = item.ItemKind == ItemKind.Directory
            ? Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories)
            : [item.SourcePath];

        return candidates
            .Where(f => !IsSymlink(f))
            .Where(f => !IsLockFile(f))
            .ToList();
    }
    /// <summary>
    /// Attempts to open each file with a read handle to detect locks.
    /// Returns paths of files that cannot be opened (i.e. read).
    /// </summary>
    /// <param name="files"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public static async Task<IReadOnlyList<string>> FindLockedFilesAsync(
        IReadOnlyList<string> files,
        CancellationToken ct)
    {
        return await Task.Run(IReadOnlyList<string> () =>
        {
            var locked = new List<string>();

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var fs = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch (IOException)
                {
                    locked.Add(file);
                }
                catch (UnauthorizedAccessException)
                {
                    locked.Add(file);
                }
            }

            return locked;
        }, ct);
    }

    /// <summary>
    /// Resolves the output path for a job item, applying the collision strategy if a .7z file already exists in the natural path.
    /// </summary>
    /// <param name="sourcePath"></param>
    /// <param name="behavior"></param>
    /// <returns></returns>
    public static string ResolveOutputPath(string sourcePath, CollisionBehavior behavior)
    {
        var natural = $"{sourcePath.TrimEnd(Path.DirectorySeparatorChar)}.7z";
        if (!File.Exists(natural))
            return natural;

        return behavior switch
        {
            CollisionBehavior.Skip => natural, // caller checks and skips
            CollisionBehavior.RenameNew => FindAvailablePath(sourcePath),
            _ => natural
        };
    }

    public static void CleanupTmp(JobItem item)
    {
        var tmp = $"{item.OutputPath}.tmp";
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch { /* we did our best */ }
    }

    private static string FindAvailablePath(string sourcePath)
    {
        var i = 1;
        string candidate;
        do
        {
            candidate = sourcePath.TrimEnd(Path.DirectorySeparatorChar) + $"_{i:D3}.7z";
            i++;
        } while (File.Exists(candidate));
        return candidate;
    }
    
    private static bool IsSymlink(string path) =>
        new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IsLockFile(string path)
    {
        string name = Path.GetFileName(path);
        return LockFilePatterns.Any(pattern => FileNameMatchesPattern(name, pattern));
    }

    private static bool FileNameMatchesPattern(string name, string pattern)
    {
        if (pattern.StartsWith('*'))
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        
        return pattern.EndsWith('*') 
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) 
            : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }
}