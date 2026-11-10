using Core.FileSystem;

namespace Core.Analysis;

/// <summary>
/// Totals for every folder in a tree, from one pass over its files.
/// </summary>
public sealed class FolderScan
{
    public required string Root { get; init; }

    /// <summary>
    /// Keyed by folder path relative to <see cref="Root"/> (<c>""</c> is the root). Each tally includes everything below
    /// the folder. Folders without any files have no entry.
    /// </summary>
    public required IReadOnlyDictionary<string, CompressibilityTally> Folders { get; init; }

    public int FileCount { get; init; }
    public IReadOnlyList<string> SkippedLinks { get; init; } = [];
    public IReadOnlyList<string> InaccessiblePaths { get; init; } = [];

    public CompressibilityTally? For(string relativePath) => Folders.GetValueOrDefault(relativePath);
}

/// <summary>
/// One pass over the tree, using the same file listing as compression (<see cref="SourceEnumerator"/>), adding each
/// file to its folder and every ancestor. Only lists files; never reads them.
/// </summary>
public static class FolderScanner
{
    /// <param name="filesScanned">Receives the running file count every 1,000 files.</param>
    public static FolderScan Scan(string root, IProgress<int>? filesScanned = null, CancellationToken ct = default)
    {
        root = Paths.Normalize(root);
        var folders = new Dictionary<string, CompressibilityTally>(Paths.Comparer);
        var links = new List<string>();
        var inaccessible = new List<string>();
        int count = 0;

        foreach (FileInfo file in SourceEnumerator.EnumerateFiles(root, new HashSet<string>(),
                     onLinkSkipped: links.Add,
                     onInaccessible: (path, _) => inaccessible.Add(path),
                     ct: ct))
        {
            string folder = Path.GetRelativePath(root, file.DirectoryName!);
            if (folder == ".") folder = "";

            for (string? current = folder; current is not null; current = current.Length == 0 ? null : Path.GetDirectoryName(current) ?? "")
            {
                if (!folders.TryGetValue(current, out CompressibilityTally? tally))
                    folders[current] = tally = new CompressibilityTally();
                tally.Add(file.Name, file.Length);
            }

            if (++count % 1000 == 0)
                filesScanned?.Report(count);
        }

        filesScanned?.Report(count);
        return new FolderScan
        {
            Root = root,
            Folders = folders,
            FileCount = count,
            SkippedLinks = links,
            InaccessiblePaths = inaccessible
        };
    }
}
