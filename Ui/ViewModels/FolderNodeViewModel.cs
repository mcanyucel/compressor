using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Analysis;
using Core.FileSystem;
using Core.Models;
using Core.Planning;

namespace Ui.ViewModels;

/// <summary>
/// One folder in the tree. Children are loaded when the node is first expanded; until then a placeholder child
/// makes the expander show. Links and junctions are not shown (they are never followed).
/// </summary>
public partial class FolderNodeViewModel : ObservableObject
{
    private static readonly EnumerationOptions AllFolders = new() { AttributesToSkip = 0 };

    private readonly MainWindowViewModel? _owner;
    private bool _loaded;

    public FolderNodeViewModel(MainWindowViewModel owner, string fullPath, string relativePath)
    {
        _owner = owner;
        FullPath = fullPath;
        RelativePath = relativePath;
        // The root's full path is in the toolbar; a drive root (C:\) has no name of its own.
        Name = Path.GetFileName(fullPath) is { Length: > 0 } name ? name : fullPath;

        if (HasSubfolders(fullPath))
            Children.Add(Placeholder());

        RefreshMark();
        RefreshScan();
    }

    private FolderNodeViewModel()
    {
        Name = "Loading...";
        FullPath = RelativePath = "";
        IsPlaceholder = true;
    }

    private static FolderNodeViewModel Placeholder() => new();

    public string Name { get; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public bool IsPlaceholder { get; }
    public bool IsRoot => RelativePath.Length == 0 && !IsPlaceholder;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    // ── Expanding ────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool NoAccess { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) LoadChildren();
    }

    private void LoadChildren()
    {
        if (_loaded || IsPlaceholder || _owner is null) return;
        _loaded = true;
        Children.Clear();

        try
        {
            var folders = new DirectoryInfo(FullPath)
                .EnumerateDirectories("*", AllFolders)
                .Where(d => !SourceEnumerator.IsLink(d))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (DirectoryInfo folder in folders)
                Children.Add(new FolderNodeViewModel(_owner, folder.FullName, Path.Combine(RelativePath, folder.Name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NoAccess = true;
        }
    }

    private static bool HasSubfolders(string path)
    {
        try { return new DirectoryInfo(path).EnumerateDirectories("*", AllFolders).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ── Marking ──────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarked), nameof(IsUnit), nameof(IsCovered), nameof(IsExcluded), nameof(MarkText))]
    public partial MarkState Mark { get; set; }

    /// <summary>Checked = ends up in an archive. Setting it toggles the rules (see <see cref="RuleSet.Toggle"/>).</summary>
    public bool IsMarked
    {
        get => Mark is MarkState.Unit or MarkState.Covered;
        set
        {
            if (value != IsMarked && _owner is not null)
                _owner.ToggleMark(this);
        }
    }

    public bool IsUnit => Mark == MarkState.Unit;
    public bool IsCovered => Mark == MarkState.Covered;
    public bool IsExcluded => Mark == MarkState.Excluded;

    public string? MarkText => Mark switch
    {
        MarkState.Unit => $"→ {Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar))}.7z",
        MarkState.Excluded => "left out",
        _ => null
    };

    public void RefreshMark()
    {
        if (_owner is null) return;
        Mark = _owner.Rules.StateOf(RelativePath);
        OnPropertyChanged(nameof(IsMarked)); // the CheckBox may hold a stale local value after a click
    }

    // ── Scan results ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string? SizeText { get; set; }

    [ObservableProperty]
    public partial CompressibilityHint Hint { get; set; }

    [ObservableProperty]
    public partial string? HintText { get; set; }

    public void RefreshScan()
    {
        if (_owner is null) return;

        FolderScan? scan = _owner.Scan;
        CompressibilityTally? tally = scan?.For(RelativePath);

        if (scan is null)
        {
            SizeText = null;
            HintText = null;
        }
        else if (tally is null)
        {
            SizeText = "no files";
            Hint = CompressibilityHint.Empty;
            HintText = null;
        }
        else
        {
            SizeText = $"{Display.Bytes(tally.TotalBytes)} · {tally.FileCount:N0} files · ~{Display.Bytes(tally.EstimatedArchiveBytes)} as 7z";
            Hint = tally.Hint;
            HintText = Display.Hint(tally.Hint);
        }
    }

    /// <summary>This node and all loaded descendants (placeholders excluded).</summary>
    public IEnumerable<FolderNodeViewModel> SelfAndLoadedDescendants()
    {
        if (IsPlaceholder) yield break;
        yield return this;
        foreach (FolderNodeViewModel child in Children)
        foreach (FolderNodeViewModel node in child.SelfAndLoadedDescendants())
            yield return node;
    }
}
