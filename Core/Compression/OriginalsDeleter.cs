using Core.FileSystem;
using Core.Models;

namespace Core.Compression;

/// <param name="Deleted">Files deleted.</param>
/// <param name="AlreadyGone">Archived files that no longer existed (removed by someone else); nothing to do.</param>
/// <param name="Kept">Archived files that were not deleted, each with the reason.</param>
public sealed record DeletionResult(int Deleted, int AlreadyGone, IReadOnlyList<string> Kept)
{
    public bool Complete => Kept.Count == 0;
}

/// <summary>
/// Deletes the originals of an item whose archive has been verified. This is the only code that deletes user
/// files, so it is deliberately conservative:
/// <list type="bullet">
/// <item>Only files in the archived list are candidates. Excluded subfolders, lock files, links and files added
/// since compression are never touched.</item>
/// <item>A file is deleted only if its size and last-write time still match what was archived.</item>
/// <item>Only folders that became empty because of these deletions are removed. Folders that were already empty
/// were not archived, so they stay.</item>
/// <item>Read-only files are deleted (their read-only flag is cleared first): they are safely in the archive.</item>
/// </list>
/// Not cancellable on purpose: once it starts it finishes, so the result always describes the whole item.
/// </summary>
public static class OriginalsDeleter
{
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0 };

    /// <param name="item">An item in the <c>Verified</c> state.</param>
    /// <param name="archived">From <see cref="CompressionResult.Files"/>: exactly what was handed to 7-Zip.</param>
    public static DeletionResult Delete(JobItem item, IReadOnlyList<ArchivedFile> archived)
    {
        string sourceRoot = item.ItemKind == ItemKind.Directory
            ? item.SourcePath
            : Path.GetDirectoryName(item.SourcePath)
              ?? throw new InvalidOperationException($"File item has no parent directory: {item.SourcePath}");

        var kept = new List<string>();
        var foldersToCheck = new HashSet<string>(Paths.Comparer);
        int deleted = 0, alreadyGone = 0;

        foreach (ArchivedFile file in archived)
        {
            string path = Path.GetFullPath(Path.Combine(sourceRoot, file.RelativePath));
            if (!Paths.IsStrictlyUnder(path, sourceRoot))
            {
                kept.Add($"{file.RelativePath}: resolves outside the item's folder");
                continue;
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                alreadyGone++;
                continue;
            }

            if (info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteTimeUtc)
            {
                kept.Add($"{file.RelativePath}: changed since it was archived");
                continue;
            }

            try
            {
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kept.Add($"{file.RelativePath}: could not delete ({ex.Message})");
                continue;
            }

            if (item.ItemKind == ItemKind.Directory)
                AddFolderAndAncestors(Path.GetDirectoryName(path)!, sourceRoot, foldersToCheck);
        }

        RemoveEmptiedFolders(foldersToCheck);
        return new DeletionResult(deleted, alreadyGone, kept);
    }

    /// <summary>
    /// The folder and every ancestor up to and including the item's own folder. Only these can have become empty.
    /// </summary>
    private static void AddFolderAndAncestors(string folder, string sourceRoot, HashSet<string> folders)
    {
        string? current = folder;
        while (current is not null && Paths.IsSameOrUnder(current, sourceRoot) && folders.Add(current))
        {
            if (Paths.Comparer.Equals(current, sourceRoot)) break;
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>
    /// Deepest first, so a parent is checked after its children have gone. Never recursive: a folder is removed only
    /// when it has no entries at all, hidden ones included.
    /// </summary>
    private static void RemoveEmptiedFolders(HashSet<string> folders)
    {
        foreach (string folder in folders.OrderByDescending(f => f.Length))
        {
            try
            {
                var directory = new DirectoryInfo(folder);
                if (!directory.Exists || directory.EnumerateFileSystemInfos("*", AllEntries).Any())
                    continue;

                if (directory.Attributes.HasFlag(FileAttributes.ReadOnly))
                    directory.Attributes &= ~FileAttributes.ReadOnly;
                directory.Delete(recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An empty folder left behind loses no data.
            }
        }
    }
}
