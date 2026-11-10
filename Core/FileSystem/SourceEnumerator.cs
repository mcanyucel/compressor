namespace Core.FileSystem;

/// <summary>
/// Lists the files that belong in an archive. Used by the job builder (to pre-scan sizes) and by compression
/// (to build the list handed to 7-Zip), so both always see the same files.
///
/// <list type="bullet">
/// <item>Hidden and system files are included.</item>
/// <item>Symbolic links and junctions are skipped, never followed. Old Windows profiles contain junctions such as
/// <c>Application Data</c> that would otherwise be archived twice or loop forever. Other reparse points
/// (e.g. OneDrive files) are treated as normal files and folders.</item>
/// <item>Lock and temporary files (<c>~$*</c>, <c>*.tmp</c>, <c>*.lck</c>, <c>*.lock</c>) are skipped.</item>
/// <item>Excluded paths are pruned without being entered.</item>
/// </list>
/// </summary>
public static class SourceEnumerator
{
    private static readonly string[] LockFilePatterns = ["~$*", "*.tmp", "*.lck", "*.lock"];

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, // recursion is manual, so links can be skipped and exclusions pruned
        AttributesToSkip = 0,          // include hidden and system files
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false
    };

    /// <param name="directory">Normalized full path of the folder to list.</param>
    /// <param name="excludedPaths">Normalized full paths to leave out, compared with <see cref="Paths.Comparer"/>.</param>
    /// <param name="onLinkSkipped">Called for each link or junction that was skipped.</param>
    /// <param name="onInaccessible">
    /// Called for a folder that can't be listed, and enumeration continues. When null, the exception is thrown instead.
    /// </param>
    public static IEnumerable<FileInfo> EnumerateFiles(
        string directory,
        IReadOnlySet<string> excludedPaths,
        Action<string>? onLinkSkipped = null,
        Action<string, Exception>? onInaccessible = null,
        CancellationToken ct = default)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            DirectoryInfo current = pending.Pop();

            List<FileSystemInfo> entries;
            try
            {
                entries = current.EnumerateFileSystemInfos("*", Options).ToList();
            }
            catch (Exception ex) when (onInaccessible is not null && ex is IOException or UnauthorizedAccessException)
            {
                onInaccessible(current.FullName, ex);
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (excludedPaths.Contains(entry.FullName))
                    continue;

                if (IsLink(entry))
                {
                    onLinkSkipped?.Invoke(entry.FullName);
                    continue;
                }

                if (entry is DirectoryInfo subdirectory)
                    pending.Push(subdirectory);
                else if (entry is FileInfo file && !IsLockFile(file.Name))
                    yield return file;
            }
        }
    }

    /// <summary>
    /// True for symbolic links and junctions. Other reparse points (cloud placeholders, deduplicated files) return false.
    /// </summary>
    public static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
            return false;

        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // can't tell; don't follow it
        }
    }

    public static bool IsLockFile(string fileName) =>
        LockFilePatterns.Any(pattern => MatchesPattern(fileName, pattern));

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern.StartsWith('*'))
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);

        return pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
