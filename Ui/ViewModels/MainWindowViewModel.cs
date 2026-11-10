using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Analysis;
using Core.Execution;
using Core.FileSystem;
using Core.Models;
using Core.Planning;
using Core.Preflight;
using Core.SevenZip;
using Core.Strategies;

namespace Ui.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record PlanItemRow(string Name, string Files, string Size, string Estimate, CompressibilityHint Hint, string? HintText, string Output);

public sealed record WarningGroup(string Title, IReadOnlyList<string> Lines)
{
    public string Header => $"{Title} ({Lines.Count})";
}

/// <summary>
/// The single window: 1. choose folders and settings, 2. check (job builder + pre-flight), 3. run.
/// Any change to the marks or settings throws away the checked plan, so what runs is always what was checked.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private RuleSet _rules = new();
    private FolderScan? _scan;
    private CancellationTokenSource? _scanCts;
    private readonly SevenZipExecutable? _sevenZip;

    public MainWindowViewModel()
    {
        if (SevenZipLocator.TryLocate(new SevenZipLocatorOptions { AllowInstalled = true }, out SevenZipExecutable? executable, out string? error))
        {
            _sevenZip = executable;
            SevenZipStatus = $"7-Zip: {executable.Source.ToString().ToLowerInvariant()}";
        }
        else
        {
            SevenZipError = $"{error} Compression is unavailable until 7-Zip is found.";
        }

        SelectedLevel = Levels[2];
        SelectedParallelism = ParallelismLevels[2];
        SelectedCollision = CollisionOptions[0];
        SelectedLocked = LockedOptions[0];
    }

    internal RuleSet Rules => _rules;
    internal FolderScan? Scan => _scan;

    /// <summary>
    /// 0 = choose folders, 1 = check, 2 = run. Only the footer buttons move between steps; the step headers are an
    /// indicator, not navigation, so the Check step can't be reached without actually checking.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChooseStep), nameof(IsCheckStep), nameof(IsRunStep), nameof(ShowCheckActions))]
    public partial int Step { get; set; }

    public bool IsChooseStep => Step == 0;
    public bool IsCheckStep => Step == 1;
    public bool IsRunStep => Step == 2;

    [ObservableProperty]
    public partial string Status { get; set; } = "Open a folder, or load a job file, to start.";

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public string? SevenZipStatus { get; }
    public string? SevenZipError { get; }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    // ── 1. Folder tree ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoot))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial string? RootPath { get; set; }

    public bool HasRoot => RootPath is not null;

    public ObservableCollection<FolderNodeViewModel> Tree { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string? SelectionSummary { get; set; }

    /// <summary>A folder to open, or a .json job file to load.</summary>
    public Task OpenAsync(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? LoadJobAsync(path)
            : OpenFolderAsync(path);

    public async Task OpenFolderAsync(string path, IEnumerable<JobRule>? rules = null)
    {
        if (!Directory.Exists(path))
        {
            ErrorMessage = $"Folder not found: {path}";
            return;
        }

        _scanCts?.Cancel();
        _checkCts?.Cancel();
        ClearPlan();
        ErrorMessage = null;
        Step = 0;

        RootPath = Paths.Normalize(path);
        _rules = rules is null ? new RuleSet() : RuleSet.From(rules);
        _scan = null;

        Tree.Clear();
        Tree.Add(new FolderNodeViewModel(this, RootPath, "") { IsExpanded = true });
        UpdateSelectionSummary();

        await ScanAsync(RootPath);
    }

    private async Task ScanAsync(string root)
    {
        var cts = _scanCts = new CancellationTokenSource();
        IsScanning = true;
        var progress = new Progress<int>(count =>
        {
            if (_scanCts == cts) Status = $"Scanning {count:N0} files...";
        });

        try
        {
            FolderScan scan = await Task.Run(() => FolderScanner.Scan(root, progress, cts.Token), cts.Token);
            if (_scanCts != cts) return;

            _scan = scan;
            foreach (FolderNodeViewModel node in Tree.SelectMany(n => n.SelfAndLoadedDescendants()))
                node.RefreshScan();

            Status = $"{scan.FileCount:N0} files, {Display.Bytes(scan.For("")?.TotalBytes ?? 0)}" +
                     (scan.SkippedLinks.Count > 0 ? $" · {scan.SkippedLinks.Count} link(s)/junction(s) skipped" : "") +
                     (scan.InaccessiblePaths.Count > 0 ? $" · {scan.InaccessiblePaths.Count} folder(s) not readable" : "");
        }
        catch (OperationCanceledException)
        {
            // a new folder was opened
        }
        finally
        {
            if (_scanCts == cts) IsScanning = false;
        }
    }

    internal void ToggleMark(FolderNodeViewModel node)
    {
        _rules.Toggle(node.RelativePath);
        foreach (FolderNodeViewModel n in Tree.SelectMany(t => t.SelfAndLoadedDescendants()))
            n.RefreshMark();

        ClearPlan();
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        int units = _rules.Units.Count();
        SelectionSummary = units switch
        {
            0 => "Nothing selected yet.",
            1 => "1 folder will become an archive.",
            _ => $"{units} folders will each become an archive."
        };
        CheckCommand.NotifyCanExecuteChanged();
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────

    public IReadOnlyList<Option<int>> Levels { get; } =
    [
        new(1, "Fastest (1)"),
        new(3, "Fast (3)"),
        new(5, "Normal (5)"),
        new(7, "Maximum (7)"),
        new(9, "Ultra (9): slow, uses a lot of memory")
    ];

    public IReadOnlyList<Option<ParallelismLevel>> ParallelismLevels { get; } =
        Enum.GetValues<ParallelismLevel>()
            .Select(level => new Option<ParallelismLevel>(level,
                $"{level} ({new JobConfig { RootPath = "", Parallelism = level }.DegreeOfParallelism} threads)"))
            .ToList();

    public IReadOnlyList<Option<CollisionBehavior>> CollisionOptions { get; } =
    [
        new(CollisionBehavior.RenameNew, "Use a numbered name (Music_001.7z)"),
        new(CollisionBehavior.Skip, "Skip that folder")
    ];

    public IReadOnlyList<Option<LockedFileBehavior>> LockedOptions { get; } =
    [
        new(LockedFileBehavior.Skip, "Archive the rest, flag it for review"),
        new(LockedFileBehavior.AbortSubJob, "Don't archive that folder")
    ];

    [ObservableProperty]
    public partial Option<int> SelectedLevel { get; set; }

    [ObservableProperty]
    public partial Option<ParallelismLevel> SelectedParallelism { get; set; }

    [ObservableProperty]
    public partial Option<CollisionBehavior> SelectedCollision { get; set; }

    [ObservableProperty]
    public partial Option<LockedFileBehavior> SelectedLocked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    public partial bool DeleteAfterVerify { get; set; }

    /// <summary>Off by default: opening every file is what makes a check slow (see PreflightOptions).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckExplanation))]
    public partial bool CheckLockedFiles { get; set; }

    /// <summary>Start the run as soon as the check is done, so a long job can be left unattended.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckButtonText))]
    public partial bool StartAutomatically { get; set; }

    public string CheckButtonText => StartAutomatically ? "Check and start →" : "Check →";

    partial void OnCheckLockedFilesChanged(bool value) => ClearPlan();
    partial void OnSelectedLevelChanged(Option<int> value) => ClearPlan();
    partial void OnSelectedParallelismChanged(Option<ParallelismLevel> value) => ClearPlan();
    partial void OnSelectedCollisionChanged(Option<CollisionBehavior> value) => ClearPlan();
    partial void OnSelectedLockedChanged(Option<LockedFileBehavior> value) => ClearPlan();
    partial void OnDeleteAfterVerifyChanged(bool value) => ClearPlan();

    private JobConfig BuildConfig() => new()
    {
        RootPath = RootPath!,
        CompressionLevel = SelectedLevel.Value,
        Parallelism = SelectedParallelism.Value,
        CollisionBehavior = SelectedCollision.Value,
        LockedFileBehavior = SelectedLocked.Value,
        DeleteAfterVerify = DeleteAfterVerify,
        Rules = _rules.ToRules()
    };

    // ── Job files ────────────────────────────────────────────────────────────────────

    public async Task LoadJobAsync(string path)
    {
        JobConfig config;
        try
        {
            config = JobFile.Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return;
        }

        SelectedLevel = Levels.FirstOrDefault(l => l.Value == config.CompressionLevel) ?? new Option<int>(config.CompressionLevel, $"Level {config.CompressionLevel}");
        SelectedParallelism = ParallelismLevels.First(p => p.Value == config.Parallelism);
        SelectedCollision = CollisionOptions.First(c => c.Value == config.CollisionBehavior);
        SelectedLocked = LockedOptions.First(l => l.Value == config.LockedFileBehavior);
        DeleteAfterVerify = config.DeleteAfterVerify;

        await OpenFolderAsync(config.RootPath, config.Rules);
        Status = $"Loaded {Path.GetFileName(path)}. {Status}";
    }

    public void SaveJob(string path)
    {
        try
        {
            JobFile.Save(BuildConfig(), path);
            Status = $"Saved {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
    }

    // ── 2. Check ─────────────────────────────────────────────────────────────────────

    private BackupJob? _job;

    public ObservableCollection<PlanItemRow> PlanItems { get; } = [];
    public ObservableCollection<WarningGroup> Warnings { get; } = [];

    [ObservableProperty]
    public partial string? PlanSummary { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCheckActions))]
    public partial bool IsChecking { get; set; }

    /// <summary>Back and Start on step 2; while checking, only Cancel is shown there.</summary>
    public bool ShowCheckActions => IsCheckStep && !IsChecking;

    [ObservableProperty]
    public partial double CheckProgress { get; set; }

    /// <summary>False while the job is being built (file count not known yet): the bar is indeterminate.</summary>
    [ObservableProperty]
    public partial bool IsCheckProgressKnown { get; set; }

    [ObservableProperty]
    public partial string? CheckProgressText { get; set; }

    public string CheckExplanation => CheckLockedFiles
        ? "Opening each file once to find locked ones. Still much faster than compressing, but your antivirus scans every file the first time it's opened."
        : "Listing files and estimating archive sizes. This usually takes seconds.";

    private CancellationTokenSource? _checkCts;

    public bool HasPlan => _job is not null;
    public bool HasWarnings => Warnings.Count > 0;

    public string StartButtonText => DeleteAfterVerify ? "Start and delete originals" : "Start";

    private bool CanCheck() => RootPath is not null && !IsChecking && !IsRunning && _rules.Units.Any();

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        JobConfig config = BuildConfig();
        var options = new PreflightOptions { CheckLockedFiles = CheckLockedFiles };
        bool startAfter = StartAutomatically;

        ClearPlan();
        PlanSummary = null;
        ErrorMessage = null;
        IsChecking = true;
        Step = 1; // the progress is shown on the Check step

        var cts = _checkCts = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        string verb = options.CheckLockedFiles ? "opened" : "checked";
        IsCheckProgressKnown = false;
        CheckProgress = 0;
        CheckProgressText = "Listing files...";
        Status = "Checking...";

        var progress = new Progress<PreflightProgress>(p =>
        {
            if (_checkCts != cts) return;
            IsCheckProgressKnown = true;
            CheckProgress = p.FilesTotal > 0 ? 100.0 * p.FilesDone / p.FilesTotal : 100;
            CheckProgressText = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files {verb} · {Display.Duration(elapsed.Elapsed)}";
        });

        BackupJob job;
        try
        {
            job = await Task.Run(async () =>
            {
                BackupJob built = JobBuilder.Build(config, cts.Token);
                await PreflightRunner.RunAsync(built, options, progress, cts.Token);
                return built;
            }, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (_checkCts == cts) Step = 0; // not if a new folder was opened meanwhile
            Status = "Check cancelled.";
            return;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Check failed: {ex.Message}";
            Status = "Check failed.";
            Step = 0;
            return;
        }
        finally
        {
            if (_checkCts == cts) _checkCts = null;
            IsChecking = false;
        }

        ShowPlan(job);
        Status = $"Check finished in {Display.Duration(elapsed.Elapsed)}.";

        if (!startAfter)
        {
            Status += " Review the plan, then start.";
        }
        else if (job.Preflight!.LowDiskSpace.Count > 0)
        {
            Status += " Not started automatically: disk space may run out. Review the warnings.";
        }
        else if (StartCommand.CanExecute(null))
        {
            await StartCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Cancel a running check and go back to choosing folders.</summary>
    [RelayCommand]
    private void CancelCheck() => _checkCts?.Cancel();

    private void ShowPlan(BackupJob job)
    {
        _job = job;
        PreflightReport report = job.Preflight!;
        string root = job.Config.RootPath;

        foreach (JobItem item in job.Items.Values.OrderBy(i => i.SourcePath, Paths.Comparer))
        {
            report.Estimates.TryGetValue(item.SourcePath, out ItemEstimate? estimate);
            string name = Path.GetRelativePath(root, item.SourcePath);
            PlanItems.Add(new PlanItemRow(
                name == "." ? Path.GetFileName(item.SourcePath) : name,
                $"{item.TotalFileCount:N0}",
                Display.Bytes(item.TotalBytes),
                Display.Bytes(estimate?.EstimatedArchiveBytes ?? item.TotalBytes),
                estimate?.Hint ?? CompressibilityHint.Unknown,
                estimate is null ? null : Display.Hint(estimate.Hint),
                Path.GetFileName(item.OutputPath)));
        }

        AddWarnings("Disk space may run out", report.LowDiskSpace);
        AddWarnings("Locked files (they will be skipped; the folder will need attention)", report.LockedFiles);
        AddWarnings("Rules ignored", report.InvalidRules);
        AddWarnings("Archive names already taken", report.Collisions);
        AddWarnings("Leftover temporary archives from an earlier run (left untouched)", report.StaleTmpFiles);
        AddWarnings("Unreadable folders", report.InaccessiblePaths);
        AddWarnings("Mostly incompressible (savings likely small)", report.PoorlyCompressible);
        AddWarnings("Marked but empty (no archive)", report.EmptyItems);
        AddWarnings("Links and junctions skipped (never followed)", report.SkippedLinks);

        long total = job.TotalBytes;
        long estimated = job.Items.Values.Sum(i => report.Estimates.TryGetValue(i.SourcePath, out var e) ? e.EstimatedArchiveBytes : i.TotalBytes);
        PlanSummary = job.Items.IsEmpty
            ? "Nothing to compress."
            : $"{job.Items.Count} archive(s) from {Display.Bytes(total)} of files, about {Display.Bytes(estimated)} after compression (rough estimate)." +
              (Warnings.Count == 0 ? " No warnings." : "") +
              (report.LockedFilesChecked ? "" : " Locked files weren't checked; any that 7-Zip can't read will be flagged during the run.");

        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasWarnings));
        StartCommand.NotifyCanExecuteChanged();
    }

    private void AddWarnings(string title, List<string> lines)
    {
        if (lines.Count > 0) Warnings.Add(new WarningGroup(title, lines.ToList()));
    }

    private void ClearPlan()
    {
        _job = null;
        PlanItems.Clear();
        Warnings.Clear();
        PlanSummary = HasRoot ? "Not checked yet. Choose folders, then press Check." : null;
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasWarnings));
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanGoBack() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step = 0;

    // ── 3. Run ───────────────────────────────────────────────────────────────────────

    private CancellationTokenSource? _runCts;
    private readonly HashSet<JobItemViewModel> _active = [];
    private readonly Dictionary<string, JobItemViewModel> _runIndex = new(Paths.Comparer);

    public ObservableCollection<JobItemViewModel> RunItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand), nameof(StopCommand), nameof(BackCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double OverallProgress { get; set; }

    [ObservableProperty]
    public partial string? RunStatus { get; set; }

    [ObservableProperty]
    public partial string? RunSummary { get; set; }

    private bool CanStart() => _job is { Items.IsEmpty: false } && !IsRunning && _sevenZip is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        BackupJob job = _job!;
        string root = job.Config.RootPath;

        RunItems.Clear();
        _runIndex.Clear();
        _active.Clear();
        foreach (JobItem item in job.Items.Values.OrderBy(i => i.SourcePath, Paths.Comparer))
        {
            string name = Path.GetRelativePath(root, item.SourcePath);
            var vm = new JobItemViewModel(item, name == "." ? Path.GetFileName(item.SourcePath) : name);
            vm.SyncFrom();
            RunItems.Add(vm);
            _runIndex[item.SourcePath] = vm;
        }

        RunSummary = null;
        OverallProgress = 0;
        IsRunning = true;
        Step = 2;
        Status = "Running...";
        _runCts = new CancellationTokenSource();

        // Low-frequency path: state changes, marshalled to the UI thread by Progress<T>.
        var stateProgress = new Progress<JobStateChange>(change =>
        {
            if (!_runIndex.TryGetValue(change.SourcePath, out JobItemViewModel? vm)) return;
            vm.SyncFrom();
            if (vm.State == JobItemState.InProgress) _active.Add(vm);
            else if (vm.Item.IsTerminal) _active.Remove(vm);
        });

        // High-frequency path: bytes processed, polled for running items only.
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => SyncRunning(job));
        timer.Start();

        var started = DateTime.UtcNow;
        try
        {
            var executor = new JobExecutor(new SevenZipStrategy(new SevenZipRunner(_sevenZip!)));
            await executor.RunAsync(job, stateProgress, _runCts.Token);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Run failed: {ex.Message}";
        }
        finally
        {
            timer.Stop();
            foreach (JobItemViewModel vm in RunItems) vm.SyncFrom(); // final authoritative pass
            _active.Clear();
            SyncRunning(job);
            RunSummary = Summarize(job, DateTime.UtcNow - started);
            Status = $"Finished: {job.OverallState}.";
            IsRunning = false;
            ClearPlan(); // a job runs once; check again to run again
        }
    }

    private void SyncRunning(BackupJob job)
    {
        foreach (JobItemViewModel vm in _active) vm.SyncFrom();

        long total = job.TotalBytes;
        long processed = job.TotalBytesProcessed;
        OverallProgress = total > 0 ? 100.0 * processed / total : 0;
        int finished = job.Items.Values.Count(i => i.IsTerminal);
        RunStatus = $"{finished} of {job.Items.Count} finished · {Display.Bytes(processed)} of {Display.Bytes(total)}";
    }

    private static string Summarize(BackupJob job, TimeSpan elapsed)
    {
        var items = job.Items.Values.ToList();
        var done = items.Where(i => i.State == JobItemState.Done).ToList();
        long before = done.Sum(i => i.Stats?.BytesBefore ?? 0);
        long after = done.Sum(i => i.Stats?.BytesAfter ?? 0);
        int deleted = items.Sum(i => i.Stats?.FilesDeleted ?? 0);
        string counts = string.Join(", ", items.GroupBy(i => i.State).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));

        return $"Finished in {Display.Duration(elapsed)}: {counts}." +
               (done.Count > 0 ? $" Saved {Display.Bytes(before - after)} ({(before > 0 ? 1 - (double)after / before : 0):P0})." : "") +
               (job.Config.DeleteAfterVerify ? $" {deleted:N0} original files deleted." : "") +
               (items.Any(i => i.State is JobItemState.NeedsAttention or JobItemState.Failed) ? " See the items marked for attention." : "");
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _runCts?.Cancel();
        Status = "Stopping... unfinished archives are removed.";
    }
}
