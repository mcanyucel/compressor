using Core.Models;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace Core.Compression;

/// <summary>
/// Shared compression logic. All strategies delegate here.
/// </summary>
public static class CompressionHelpers
{
    private static readonly string[] LockFilePatterns = ["~$*", "*.tmp", "*.lck", "*.lock"];

    public static async Task CompressAsync(JobItem item, bool checkCancellationBetweenFiles, CancellationToken ct)
    {
        var tmpPath = $"{item.OutputPath}.tmp";

        try
        {
            IReadOnlyList<string> files = await Task.Run(() => CollectFiles(item), ct);

            await Task.Run(() =>
            {
                using var outStream = new FileStream(
                    tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 65536, useAsync: false);

                using IWriter writer = WriterFactory.OpenWriter(
                    outStream,
                    ArchiveType.SevenZip,
                    new WriterOptions(CompressionType.LZMA) { LeaveStreamOpen = false });

                string? sourceRoot = item.ItemKind == ItemKind.Directory
                    ? item.SourcePath
                    : Path.GetDirectoryName(item.SourcePath);

                if (sourceRoot is null)
                    throw new InvalidOperationException("Source root cannot be null!");

                foreach (string filePath in files)
                {
                    if (checkCancellationBetweenFiles)
                        ct.ThrowIfCancellationRequested();

                    string relativePath = Path.GetRelativePath(sourceRoot, filePath);
                    using (var fileStream = File.OpenRead(filePath))
                        writer.Write(relativePath, fileStream, File.GetLastWriteTime(filePath));

                    Interlocked.Add(ref item.BytesProcessed, new FileInfo(filePath).Length);
                    Interlocked.Increment(ref item.FilesProcessed);
                }
            }, ct);
        }
        catch
        {
            CleanupTmp(item);
            throw;
        }
        
        // Atomic rename .7z.tmp to .7z
        // output path should already collision-resolved at job-build time
        File.Move(tmpPath, item.OutputPath, overwrite: false);
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