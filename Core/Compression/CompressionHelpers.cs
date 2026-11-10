using Core.FileSystem;
using Core.Models;
using Core.SevenZip;

namespace Core.Compression;

/// <summary>
/// Shared compression logic. All strategies delegate here.
/// </summary>
public static class CompressionHelpers
{
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
            files = await Task.Run(() => CollectFiles(item, ct)
                .Select(info => new ArchivedFile(Path.GetRelativePath(sourceRoot, info.FullName), info.Length, info.LastWriteTimeUtc))
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

    /// <summary>
    /// The files to archive for <paramref name="item"/>, with the same rules the job builder used to pre-scan it.
    /// Throws if a folder can't be listed or the source is gone.
    /// </summary>
    public static IReadOnlyList<FileInfo> CollectFiles(JobItem item, CancellationToken ct = default)
    {
        if (item.ItemKind == ItemKind.Directory)
            return SourceEnumerator.EnumerateFiles(item.SourcePath, item.ExcludedPaths, ct: ct).ToList();

        // An explicitly marked file is archived even if its name looks like a lock file.
        var file = new FileInfo(item.SourcePath);
        if (!file.Exists)
            throw new FileNotFoundException($"Source file not found: {item.SourcePath}", item.SourcePath);

        return SourceEnumerator.IsLink(file) ? [] : [file];
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

    public static void CleanupTmp(JobItem item)
    {
        var tmp = $"{item.OutputPath}.tmp";
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch { /* we did our best */ }
    }
}