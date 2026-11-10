using Core.FileSystem;
using Core.Models;

namespace Core.Planning;

/// <summary>How a folder or file is affected by the rules, for display.</summary>
public enum MarkState
{
    /// <summary>Not compressed.</summary>
    None,
    /// <summary>Compressed into its own archive (the topmost compressed node).</summary>
    Unit,
    /// <summary>Compressed as part of an ancestor's archive.</summary>
    Covered,
    /// <summary>Inside a compressed folder, but left out of its archive.</summary>
    Excluded
}

/// <summary>
/// The user's marks, as rules keyed by path relative to the root (<c>""</c> is the root itself).
/// Same semantics as <see cref="JobBuilder"/>: the nearest rule at or above a path wins.
///
/// The UI shows one checkbox per node: checked means "ends up in an archive". <see cref="Toggle"/> flips that with
/// the fewest rules: it removes the node's own rule, and only adds the opposite one if the node's state didn't change.
/// </summary>
public sealed class RuleSet
{
    private readonly Dictionary<string, JobAction> _rules = new(Paths.Comparer);

    public static RuleSet From(IEnumerable<JobRule> rules)
    {
        var set = new RuleSet();
        foreach (JobRule rule in rules)
            set._rules[Normalize(rule.RelativePath)] = rule.Action; // later rules win; the job builder reports conflicts
        return set;
    }

    public int Count => _rules.Count;

    public JobAction? RuleFor(string relativePath) =>
        _rules.TryGetValue(Normalize(relativePath), out JobAction action) ? action : null;

    /// <summary>True if the nearest rule at or above the path is Compress.</summary>
    public bool IsCompressed(string relativePath)
    {
        for (string? path = Normalize(relativePath); path is not null; path = Parent(path))
        {
            if (_rules.TryGetValue(path, out JobAction action))
                return action == JobAction.Compress;
        }
        return false;
    }

    public MarkState StateOf(string relativePath)
    {
        string path = Normalize(relativePath);
        bool self = IsCompressed(path);
        bool parent = Parent(path) is { } p && IsCompressed(p);

        return (self, parent) switch
        {
            (true, false) => MarkState.Unit,
            (true, true) => MarkState.Covered,
            (false, true) => MarkState.Excluded,
            _ => MarkState.None
        };
    }

    public void Toggle(string relativePath)
    {
        string path = Normalize(relativePath);
        bool wasCompressed = IsCompressed(path);

        _rules.Remove(path);
        if (IsCompressed(path) == wasCompressed)
            _rules[path] = wasCompressed ? JobAction.Skip : JobAction.Compress;
    }

    public void Clear() => _rules.Clear();

    /// <summary>Paths that will each become an archive.</summary>
    public IEnumerable<string> Units => _rules.Keys.Where(path => StateOf(path) == MarkState.Unit);

    /// <summary>
    /// The rules that have an effect, sorted by path. Rules that don't change anything (a Skip outside any compressed
    /// folder, a Compress inside one) are left out; they can remain after toggling a parent.
    /// </summary>
    public List<JobRule> ToRules() =>
        _rules
            .Where(r => (r.Value == JobAction.Compress) != (Parent(r.Key) is { } p && IsCompressed(p)))
            .OrderBy(r => r.Key, Paths.Comparer)
            .Select(r => new JobRule { RelativePath = r.Key, Action = r.Value })
            .ToList();

    /// <summary><c>"a/b/"</c>, <c>"a\b"</c> → <c>"a\b"</c> (platform separator); <c>"."</c> and <c>""</c> → <c>""</c>.</summary>
    public static string Normalize(string relativePath)
    {
        string path = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                                  .Trim(Path.DirectorySeparatorChar);
        return path == "." ? "" : path;
    }

    private static string? Parent(string path) =>
        path.Length == 0 ? null : Path.GetDirectoryName(path) ?? "";
}
