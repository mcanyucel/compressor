using Core.FileSystem;
using Core.Models;
using Core.Preflight;

namespace Core.Planning;

/// <summary>
/// Turns a <see cref="JobConfig"/> (root folder + the user's rules) into a sealed <see cref="BackupJob"/>.
///
/// <para><b>Rules: nearest rule wins.</b> Each file and folder follows the closest rule at or above it.</para>
/// <list type="bullet">
/// <item>A <c>Compress</c> rule whose nearest ruled ancestor is not <c>Compress</c> starts a job item (an "effective unit").
/// A <c>Compress</c> rule under another one is redundant: it is already covered.</item>
/// <item>A <c>Skip</c> rule inside a compressed folder excludes that subtree from the archive; it stays as-is on disk.</item>
/// <item>A <c>Compress</c> rule under such a <c>Skip</c> starts its own item again.</item>
/// </list>
/// With <c>Compress</c> rules only, this is exactly the "effective unit" DFS from the design doc.
///
/// <para>Each item is pre-scanned (file count and bytes) and gets an output path next to its source, with
/// collisions resolved per <see cref="JobConfig.CollisionBehavior"/>. Problems go into <see cref="BackupJob.Preflight"/>;
/// the pre-flight runner adds its own checks to the same report.</para>
/// </summary>
public static class JobBuilder
{
    public static BackupJob Build(JobConfig config, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(config.RootPath) || !Directory.Exists(config.RootPath))
            throw new ArgumentException($"Root path must be an existing absolute folder: {config.RootPath}", nameof(config));

        string root = Paths.Normalize(config.RootPath);
        var report = new PreflightReport();
        Dictionary<string, JobAction> rules = ResolveRules(root, config.Rules, report);

        // ── 1. Effective units and their output paths ───────────────────────────────
        // Outputs are planned for all units first, because an item must exclude the archives of items nested inside it.
        var outputs = new Dictionary<string, string>(Paths.Comparer); // unit → output path
        var plannedOutputs = new HashSet<string>(Paths.Comparer);

        foreach (string unit in FindUnits(root, rules).Order(Paths.Comparer))
        {
            if (Path.GetDirectoryName(unit) is null)
            {
                report.InvalidRules.Add($"A drive root can't be compressed into a single archive: {unit}");
                continue;
            }

            if (ResolveOutputPath(unit, config.CollisionBehavior, plannedOutputs, report) is { } output)
            {
                outputs[unit] = output;
                plannedOutputs.Add(output);
            }
        }

        // ── 2. Pre-scan each unit and create its item ──────────────────────────────
        var job = new BackupJob { Config = config, Preflight = report };

        foreach ((string unit, string output) in outputs)
        {
            ct.ThrowIfCancellationRequested();

            bool isDirectory = Directory.Exists(unit);
            var excluded = new HashSet<string>(Paths.Comparer);
            long totalBytes = 0;
            int fileCount = 0;

            if (isDirectory)
            {
                AddExclusions(unit, root, rules, outputs, excluded);

                foreach (FileInfo file in SourceEnumerator.EnumerateFiles(
                             unit, excluded,
                             onLinkSkipped: report.SkippedLinks.Add,
                             onInaccessible: (path, ex) => report.InaccessiblePaths.Add($"{path}: {ex.Message}"),
                             ct))
                {
                    totalBytes += file.Length;
                    fileCount++;
                }
            }
            else
            {
                totalBytes = new FileInfo(unit).Length;
                fileCount = 1;
            }

            if (fileCount == 0)
            {
                report.EmptyItems.Add(unit);
                continue;
            }

            job.AddItem(new JobItem
            {
                SourcePath = unit,
                OutputPath = output,
                ItemKind = isDirectory ? ItemKind.Directory : ItemKind.File,
                TotalBytes = totalBytes,
                TotalFileCount = fileCount,
                ExcludedPaths = excluded
            });
        }

        job.Seal();
        return job;
    }

    // ── Rules ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates rules and maps them to normalized absolute paths. Invalid and conflicting rules are reported and ignored.
    /// </summary>
    private static Dictionary<string, JobAction> ResolveRules(string root, IEnumerable<JobRule> rules, PreflightReport report)
    {
        var resolved = new Dictionary<string, JobAction>(Paths.Comparer);
        var conflicted = new HashSet<string>(Paths.Comparer);

        foreach (JobRule rule in rules)
        {
            string relative = rule.RelativePath.Trim();
            if (Path.IsPathRooted(relative))
            {
                report.InvalidRules.Add($"Rule path must be relative to the root folder: {rule.RelativePath}");
                continue;
            }

            string full = Paths.Normalize(Path.Combine(root, relative));
            if (!Paths.IsSameOrUnder(full, root))
            {
                report.InvalidRules.Add($"Rule path is outside the root folder: {rule.RelativePath}");
                continue;
            }

            FileSystemInfo? info = Directory.Exists(full) ? new DirectoryInfo(full)
                : File.Exists(full) ? new FileInfo(full)
                : null;

            if (info is null)
            {
                report.InvalidRules.Add($"Rule path does not exist: {rule.RelativePath}");
                continue;
            }

            if (!Paths.Comparer.Equals(full, root) && SourceEnumerator.IsLink(info))
            {
                report.InvalidRules.Add($"Rule path is a link or junction, which is never followed: {rule.RelativePath}");
                continue;
            }

            if (resolved.TryGetValue(full, out JobAction existing))
            {
                if (existing != rule.Action) conflicted.Add(full);
                continue;
            }

            resolved[full] = rule.Action;
        }

        foreach (string path in conflicted)
        {
            resolved.Remove(path);
            report.InvalidRules.Add($"Conflicting Compress and Skip rules for the same path; both ignored: {Path.GetRelativePath(root, path)}");
        }

        return resolved;
    }

    /// <summary>
    /// Compress rules whose nearest ruled ancestor is not Compress.
    /// </summary>
    private static IEnumerable<string> FindUnits(string root, Dictionary<string, JobAction> rules) =>
        rules
            .Where(r => r.Value == JobAction.Compress && NearestAncestorAction(r.Key, root, rules) != JobAction.Compress)
            .Select(r => r.Key);

    /// <summary>
    /// What a unit's archive must leave out:
    /// <list type="bullet">
    /// <item>Skip rules inside it that are in effect (nearest ruled ancestor is Compress).</item>
    /// <item>Other units nested inside it, and their archives (final and <c>.tmp</c>).</item>
    /// </list>
    /// </summary>
    private static void AddExclusions(
        string unit, string root,
        Dictionary<string, JobAction> rules,
        Dictionary<string, string> outputs,
        HashSet<string> excluded)
    {
        foreach ((string path, JobAction action) in rules)
        {
            if (action == JobAction.Skip && Paths.IsStrictlyUnder(path, unit) &&
                NearestAncestorAction(path, root, rules) == JobAction.Compress)
            {
                excluded.Add(path);
            }
        }

        foreach ((string otherUnit, string otherOutput) in outputs)
        {
            if (Paths.Comparer.Equals(otherUnit, unit)) continue;

            if (Paths.IsStrictlyUnder(otherUnit, unit)) excluded.Add(otherUnit);
            if (Paths.IsStrictlyUnder(otherOutput, unit))
            {
                excluded.Add(otherOutput);
                excluded.Add(otherOutput + ".tmp");
            }
        }
    }

    /// <summary>
    /// The action of the closest rule strictly above <paramref name="path"/>, up to and including the root. Null if none.
    /// </summary>
    private static JobAction? NearestAncestorAction(string path, string root, Dictionary<string, JobAction> rules)
    {
        string current = path;
        while (!Paths.Comparer.Equals(current, root))
        {
            current = Path.GetDirectoryName(current)!; // path is under root, so this reaches root before null
            if (rules.TryGetValue(current, out JobAction action))
                return action;
        }

        return null;
    }

    // ── Output Paths ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>&lt;source&gt;.7z</c> next to the source. If that is taken (an existing file or folder, another item's output,
    /// or a leftover <c>.7z.tmp</c>), <see cref="CollisionBehavior.RenameNew"/> picks <c>&lt;source&gt;_001.7z</c>, <c>_002</c>, ...
    /// and <see cref="CollisionBehavior.Skip"/> leaves the item out. Returns null when the item is left out.
    /// </summary>
    private static string? ResolveOutputPath(
        string unit, CollisionBehavior behavior, HashSet<string> plannedOutputs, PreflightReport report)
    {
        string natural = unit + ".7z";

        bool collides = File.Exists(natural) || Directory.Exists(natural) || plannedOutputs.Contains(natural);
        bool staleTmp = File.Exists(natural + ".tmp");

        if (staleTmp) report.StaleTmpFiles.Add(natural + ".tmp");
        if (!collides && !staleTmp) return natural;

        if (collides)
        {
            report.Collisions.Add(natural);
            if (behavior == CollisionBehavior.Skip) return null;
        }

        // A leftover .tmp alone isn't a collision with an existing archive, but compression would refuse to
        // overwrite it, so pick a free name either way.
        for (int i = 1; ; i++)
        {
            string candidate = $"{unit}_{i:D3}.7z";
            if (!File.Exists(candidate) && !Directory.Exists(candidate) &&
                !File.Exists(candidate + ".tmp") && !plannedOutputs.Contains(candidate))
                return candidate;
        }
    }
}
