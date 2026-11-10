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
    [NotifyPropertyChangedFor(nameof(MarkSuffix))]
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
    [NotifyPropertyChangedFor(nameof(IsMarked), nameof(IsUnit), nameof(IsCovered), nameof(IsExcluded), nameof(MarkSuffix))]
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

    /// <summary>
    /// Shown after the name, in the same line: the archive this folder becomes, that it's left out, or that it
    /// can't be read.
    /// </summary>
    public string MarkSuffix => (Mark switch
    {
        MarkState.Unit => $"   → {Name}.7z",
        MarkState.Excluded => "   left out",
        _ => ""
    }) + (NoAccess ? "   no access" : "");

    public void RefreshMark()
    {
        if (_owner is null) return;
        Mark = _owner.Rules.StateOf(RelativePath);
        OnPropertyChanged(nameof(IsMarked)); // the CheckBox may hold a stale local value after a click
    }

    // ── Scan results ─────────────────────────────────────────────────────────────────

    // One property per column, so the view can align them.

    [ObservableProperty]
    public partial CompressibilityHint Hint { get; set; }

    [ObservableProperty]
    public partial string? HintText { get; set; }

    [ObservableProperty]
    public partial string? SizeText { get; set; }

    [ObservableProperty]
    public partial string? EstimateText { get; set; }

    [ObservableProperty]
    public partial string? FilesText { get; set; }

    public void RefreshScan()
    {
        if (_owner is null) return;

        FolderScan? scan = _owner.Scan;
        CompressibilityTally? tally = scan?.For(RelativePath);

        if (scan is null)
        {
            // still scanning
            HintText = SizeText = EstimateText = FilesText = null;
        }
        else if (tally is null)
        {
            Hint = CompressibilityHint.Empty;
            HintText = null;
            SizeText = EstimateText = "—";
            FilesText = "0";
        }
        else
        {
            Hint = tally.Hint;
            HintText = Display.Hint(tally.Hint);
            SizeText = Display.Bytes(tally.TotalBytes);
            EstimateText = "~" + Display.Bytes(tally.EstimatedArchiveBytes);
            FilesText = tally.FileCount.ToString("N0");
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
