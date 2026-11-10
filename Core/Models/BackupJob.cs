using System.Collections.Concurrent;
using Core.Preflight;

namespace Core.Models;

/// <summary>
/// The root job object. Holds all <see cref="JobItem"/> instances and overall lifecycle state. Build it up with <see cref="AddItem"/>, then call
/// <see cref="Seal"/> before handing it to <c>JobExecutor</c>.
///
/// After <see cref="Seal"/>, the dictionary structure is frozen. Only individual <see cref="JobItem"/> properties change during execution.
/// </summary>
public sealed class BackupJob
{
    public required JobConfig Config { get; init; }

    // ── Item Storage ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keyed by normalized absolute path. OrdinalIgnoreCase because Windows paths are case-insensitive.
    /// </summary>
    public ConcurrentDictionary<string, JobItem> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Job item paths sorted descending by length (deepest-first).
    /// Built at <see cref="Seal"/> time for O(n) FSW prefix-match resolution where n is bounded by <c>MaxDegreeOfParallelism</c> in practice.
    /// </summary>
    public IReadOnlyList<string> SortedPaths { get; private set; } = [];

    // ── Lifecycle ──────────────────────────────────────────────────────────────────

    public JobState OverallState { get; private set; } = JobState.Building;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public PreflightReport? Preflight { get; set; }

    private bool _sealed;

    // ── Build Phase ──────────────────────────────────────────────────────────────────
    public void AddItem(JobItem item)
    {
        if (_sealed)
            throw new InvalidOperationException("Cannot add items after sealed.");

        Items[item.SourcePath] = item;
    }

    /// <summary>
    /// Freezes the items dictionary and bakes <see cref="SortedPaths"/>.
    /// Must be called once before execution begins.
    /// </summary>
    public void Seal()
    {
        if (_sealed) return;

        SortedPaths = Items.Keys
            .OrderByDescending(p => p.Length)
            .ToList();

        OverallState = JobState.ReadyForPreflight;
        _sealed = true;
    }

    // ── FSW Resolution ───────────────────────────────────────────────────────────────

    public JobItem? FindOwningItem(string changedPath)
    {
        foreach (string path in SortedPaths)
        {
            if (IsSameOrUnder(changedPath, path))
                return Items[path];
        }

        return null;
    }

    /// <summary>
    /// True if <paramref name="path"/> equals <paramref name="itemPath"/> or lies inside it.
    /// A plain StartsWith would wrongly match <c>C:\docs\abc</c> against <c>C:\docs\a</c>.
    /// </summary>
    private static bool IsSameOrUnder(string path, string itemPath)
    {
        if (!path.StartsWith(itemPath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Length == itemPath.Length || Path.EndsInDirectorySeparator(itemPath))
            return true;

        char next = path[itemPath.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }

    // ── Lifecycle Transitions ─────────────────────────────────────────────────────────

    public void MarkPreflightStarted() => OverallState = JobState.RunningPreflight;
    public void MarkPreflightCompleted() => OverallState = JobState.ReadyToExecute;

    public void MarkExecutionStarted()
    {
        OverallState = JobState.Executing;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void MarkExecutionCompleted()
    {
        FinishedAt = DateTimeOffset.UtcNow;
        OverallState = Items.Values.Any(i =>
            i.State is JobItemState.NeedsAttention or JobItemState.Cancelled)
            ? JobState.CompletedWithErrors
            : JobState.Completed;
    }
    
    // ── Aggregate Helpers (UI Polling) ─────────────────────────────────────────────────
    
    public int CountByState(JobItemState state) => Items.Values.Count(i => i.State == state);
    public long TotalBytes => Items.Values.Sum(i => i.TotalBytes);
    public long TotalBytesProcessed => Items.Values.Sum(i => Interlocked.Read(ref i.BytesProcessed));
}