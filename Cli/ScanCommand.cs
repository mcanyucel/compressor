using Core.Analysis;
using Core.FileSystem;
using Core.Models;
using Core.Planning;

namespace Cli;

/// <summary>
/// <c>scan &lt;folder&gt;</c>: one pass over the tree, totals per subfolder down to <c>--depth</c>, so the user can
/// decide what to mark. <c>--write</c> turns the top-level result into a starter job file.
/// </summary>
public static class ScanCommand
{
    public static int Run(Arguments args)
    {
        string folder = args.RequirePositional("folder");
        if (!Directory.Exists(folder))
            throw new UsageException($"Folder not found: {folder}");

        int depth = 1;
        if (args.Get("--depth") is { } depthText && (!int.TryParse(depthText, out depth) || depth < 1))
            throw new UsageException("--depth must be a positive number.");

        string? writePath = args.Get("--write");
        if (writePath is not null && File.Exists(writePath))
            throw new UsageException($"Job file already exists, not overwriting: {writePath}");

        string root = Paths.Normalize(folder);
        var tallies = new Dictionary<string, CompressibilityTally>(Paths.Comparer); // relative folder → totals
        var looseFiles = new CompressibilityTally();
        int links = 0, scanned = 0;
        var inaccessible = new List<string>();
        bool showProgress = !Console.IsOutputRedirected;

        foreach (FileInfo file in SourceEnumerator.EnumerateFiles(root, new HashSet<string>(),
                     onLinkSkipped: _ => links++,
                     onInaccessible: (path, _) => inaccessible.Add(path)))
        {
            string[] parts = Path.GetRelativePath(root, file.FullName).Split(Path.DirectorySeparatorChar);
            if (parts.Length == 1)
                looseFiles.Add(file.Name, file.Length);

            for (int level = 1; level <= Math.Min(depth, parts.Length - 1); level++)
            {
                string key = string.Join(Path.DirectorySeparatorChar, parts[..level]);
                if (!tallies.TryGetValue(key, out var tally))
                    tallies[key] = tally = new CompressibilityTally();
                tally.Add(file.Name, file.Length);
            }

            if (showProgress && ++scanned % 5000 == 0)
                Console.Write($"\rScanning... {scanned:N0} files");
        }

        if (showProgress) Console.Write("\r" + new string(' ', 40) + "\r");

        PrintTable(root, tallies, looseFiles);

        if (links > 0) Console.WriteLine($"\n{links} link(s)/junction(s) skipped (never followed).");
        if (inaccessible.Count > 0)
        {
            Console.WriteLine($"\n{inaccessible.Count} folder(s) could not be read:");
            foreach (string path in inaccessible.Take(10)) Console.WriteLine($"  {path}");
            if (inaccessible.Count > 10) Console.WriteLine($"  ... and {inaccessible.Count - 10} more");
        }

        if (writePath is not null)
            WriteStarterJob(root, tallies, writePath);

        return ExitCodes.Ok;
    }

    private static void PrintTable(string root, Dictionary<string, CompressibilityTally> tallies, CompressibilityTally looseFiles)
    {
        Console.WriteLine(root);
        Console.WriteLine();
        Console.WriteLine($"  {"Folder",-50} {"Files",10} {"Size",10} {"Est. 7z",10}  Hint");
        Console.WriteLine($"  {new string('-', 50)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)}  -------");

        foreach ((string path, CompressibilityTally tally) in tallies.OrderBy(t => t.Key, Paths.Comparer))
        {
            int level = path.Count(c => c == Path.DirectorySeparatorChar);
            string name = new string(' ', level * 2) + Path.GetFileName(path);
            if (name.Length > 50) name = name[..47] + "...";
            PrintRow(name, tally);
        }

        if (looseFiles.FileCount > 0)
            PrintRow("(files directly in this folder)", looseFiles);

        long total = tallies.Where(t => !t.Key.Contains(Path.DirectorySeparatorChar)).Sum(t => t.Value.TotalBytes) + looseFiles.TotalBytes;
        Console.WriteLine();
        Console.WriteLine($"  Total: {Format.Bytes(total)}. Folders without files are not shown. Estimates are rough.");
    }

    private static void PrintRow(string name, CompressibilityTally tally) =>
        Console.WriteLine($"  {name,-50} {tally.FileCount,10:N0} {Format.Bytes(tally.TotalBytes),10} {Format.Bytes(tally.EstimatedArchiveBytes),10}  {Format.Hint(tally.Hint)}");

    private static void WriteStarterJob(string root, Dictionary<string, CompressibilityTally> tallies, string path)
    {
        var rules = tallies
            .Where(t => !t.Key.Contains(Path.DirectorySeparatorChar) &&
                        t.Value.Hint is CompressibilityHint.Good or CompressibilityHint.Average)
            .OrderBy(t => t.Key, Paths.Comparer)
            .Select(t => new JobRule { RelativePath = t.Key, Action = JobAction.Compress })
            .ToList();

        JobFile.Save(new JobConfig { RootPath = root, Rules = rules }, path);
        Console.WriteLine($"\nWrote {path} with {rules.Count} Compress rule(s). Review it, then: compressor plan \"{path}\"");
    }
}
