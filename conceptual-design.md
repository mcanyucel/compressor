# Selective Compression Backup Tool - Design Document

## Philosophy

Most backup tools treat the archives as the primary artifact. This tool treats the **folder tree** as the primary artifact: compression is a property applied to individual nodes, not a transformation of the whole tree.

Goals:
* Preserve navigability: The output is still a browsable directory structure (unless the topmost node is marked to compress)
* Avoid re-compressing already-incompressible content (images, videos, existing archives)
* Allow per-folder and per-file compression decisions
* Run silently after a pre-flight gate - no prompts mid-job
* Target old hardware gracefully: Controllable parallelism, low overhead.

--- 

## Tech Stack

| Concern | Choice |
| --      | --     |
|Language, runtime | C#, dotnet 10 |
| UI Framework | WPF, CommunityToolkit.MVVM | 
| Compression | SharpCompress |
| Serialization | Json | 
| FileSystem Events | FileSystemWatcher |

## Core Concept: Effective Compression Units

A node is an **effective unit if it is marked AND no ancestor is also marked. Children of a marked node are automatically covered - marking them individually is redundant and the UI should show this visually (dimmed / shadowed state).

The algorithm to build the actual job list is a DFS:

```csharp
IEnumerable<FileSystemNode> GetEffectiveUnits(FileSystemNode node, bool ancestorMarked = false)
{
  bool isEffective  = node.IsMarked && !ancestorMarked;

  if (isEffective)
    yield return node; // do not recurse - children are covered
  else
    foreach (var child in node.Children)
      foreach (var unit in GetEffectiveUnits(child, ancestorMarked || node.IsMarked)
        yield return unit;
```

The reverse does not apply; if all subfolders of a folder are individually marked, compression is performed per-subfolder. The parent's compression pointer does not move up automatically.

--- 

## Data Model

`JobItem`

Pure data class - no observable pattern, thus safe to mutate from background threads via `volatile` and `Interlocked`:

```csharp
public enum JobItemState { Pending, InProgress, Verified, Done, Skipped, Cancelled, NeedsAttention } 
public enum ItemKind { File, Directory }

public sealed class JobItem
{
  // Identity - set once at job-build time, never mutated
  public string SourcePath { get; init; }
  public string OutputPath { get; init; } // final .7z path
  public ItemKind Kind { get; init; }
  public long TotalBytes { get; init; } // prescanned at job-build time

  // Runtime state - background-thread-safe
  public volatile JobItemState State;
  public long BytesProcessed // should be mutated via Interlocked.Add

  // Control
  public CancellationTokenSource Cts { get; } = new();

  // Post-run metadata - written once after completion
  public JobItemError? Error { get; set; }
  oybkuc JobItemStats? Stats { get; set; }
}
```

`BackupJob`

Wraps the item dictionary and overall lifecycle. Sealed after job-build so no structural mutation happens during execution.

```csharp
public sealed class BackupJob
{
  public JobConfig Config { get; init; }
  public ICompressionStrategy Strategy { get; init; }

  // Keyed by normalized absolute path - OrdinalIgnoreCase for Windows
  public ConcurrentDictionary<string JobItem> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

  // Sorted descending by length (deepest first) for O(log n) FSW prefix match
  public IReadOnlyList<string> SortedPaths { get; private set; }

  public JobState OverallState OverallState { get; private set; }
  public DateTimeOffset? StartedAt { get; private set; }
  public DateTimeOffset? FinishedAt { get; private set; }

  public PreflightReport Preflight { get; private set; }

  // Called once after all items added, before execution begins
  public void Seal()
  {
    SortedPaths = Items.Keys.OrderByDescending(p => p.Length).ToList();
  }
}
```

`JobConfig`

Serialized as JSON. The job file is the unit of persistence

```csharp
public sealed class JobConfig
{
    public string              RootPath          { get; init; }
    public ParallelismLevel    Parallelism        { get; init; } = ParallelismLevel.Normal;
    public CompressionStrategy Strategy           { get; init; } = CompressionStrategy.Safe;
    public CollisionBehavior   OnCollision        { get; init; } = CollisionBehavior.RenameNew;
    public LockedFileBehavior  OnLockedFile       { get; init; } = LockedFileBehavior.Skip;
    public SourceChangeBehavior OnSourceChange    { get; init; } = SourceChangeBehavior.CancelSubJob;
    public bool                DeleteAfterVerify  { get; init; } = false;
    public List<JobRule>       Rules              { get; init; } = [];
}

public sealed class JobRule
{
    public string     Path   { get; init; }   // relative to RootPath
    public JobAction  Action { get; init; }   // Compress | Skip
}
```

Example job file:

```json
{
  "rootPath": "C:\\OldPC\\Users\\mustafa",
  "parallelism": "Normal",
  "strategy": "Safe",
  "onCollision": "RenameNew",
  "onLockedFile": "Skip",
  "onSourceChange": "CancelSubJob",
  "deleteAfterVerify": false,
  "rules": [
    { "path": "Documents\\contracts", "action": "Compress" },
    { "path": "Documents\\photos",    "action": "Skip" },
    { "path": "Documents\\notes.txt", "action": "Compress" }
  ]
}
```
---

## Parallelism

Since LZMA is CPU-saturating, default is set to `Normal`. Old hardware may default to `Low` (based on CPU count, maybe?):

```csharp
public enum ParallelismLevel
{
    Minimum,  // max(1, ProcessorCount * 0.10)
    Low,      // ProcessorCount * 0.25
    Normal,   // ProcessorCount * 0.50
    High,     // ProcessorCount * 0.75
    Maximum   // ProcessorCount
}
```

---

## Strategy Pattern

Pre-flight is strategy-agnostic and always runs. Strategies only differ in what happens during execution:

```csharp
public interface ICompressionStrategy
{
    Task CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct);
}
```

`FastStrategy`

No FSW consumption, no reactive handling. For offline or disconnected drives / PCs where nothing is actively touching the files during execution.

`SafeStrategy`

Consumes FSW events, per-item `CancellationTokenSource`. For live machines. See FSW section below.

Shared implementation lives in static helper classes consumed by both strategies - ShartCompress write loop, `.7z.tmp` rename, post-compression verification.

---

## Compression Pipeline (Per Item)

```
Write to .7z.tmp
      ↓
Rename .7z.tmp → .7z
      ↓
Verify: SharpCompress re-reads and checks CRC
Verify: file count in archive == file count enumerated at job-build time
      ↓
[Optional] Delete originals
```

Writing to `.7z.tmp` first means interrupted jobs leave no orphan corrupted .7z files. On next launch, stale `.7z.temp` files are detected and either cleaned up or flagged.

### Cancellation Hook

```csharp
foreach (var file in filesToCompress)
{
    ct.ThrowIfCancellationRequested();
    writer.Write(relativePath, file.FullPath, file.LastModified);
}
```

`ThrowIfCancellationRequested` between each file is the foundation of clean cancellation, and the `catch` block deletes any `.7z.tmp`. 

## Locked File Handling

SharpCompress exceptions from locked files are caught per-file. The item is marked `NeedsAttention` rather than silently producing an incomplete archive. The post-compression file-count verification provides a second safety net.

## Name Collisions

`folder.7z` already existing when we try to create it. Options:

* `RenameNew` append unique suffix to newly created archive (safe, recommended)
* `Skip` leave the existing archive, skip this job item
* `Overwrite` will cause issues if the archive is already open, may remove this option in implementation

---

## Pre-Flight

Always runs before execution, and strategy-agnostic. Surfaces all issues while the user can still act on them:

```csharp
public async Task<PreflightReport> RunPreflightAsync(CancellationToken ct)
{
  // 1. Try opening each file with FileAccess.Read - collect failures
  // 2. Estimate compressibility per effective unit - see below
  // 3. Detect name collisions
  // 4. Pre-scan total bytes per item for accurate progress tracking
  // 5. Detect and filter patterns: ~*.docx, symlinks, empty directory chains, etc
}
```

### Pre-Flight Summary UI

```
Analyzing job...
  ✔  12 items ready
  ⚠   2 folders contain mostly images — compression savings likely <5%
  ⚠   1 file currently locked: C:\...\report.docx
  ⚠   1 name collision: contracts.7z already exists

  On collision:      [Rename new ▾]
  On locked file:    [Skip ▾]
  On source change:  [Cancel sub-job ▾]
  Delete originals after verification: [ ]

  [Start Job]   [Save Job File]
```

---

## FileSystemWatcher

One watcher instance on the topmost root folder, `IncludeSubdirectories = true`. Two consumers:

```
FSW on root/
    │
    ├── Consumer 1: Job execution — cancels affected JobItems
    │
    └── Consumer 2: Hint cache — invalidates ancestor nodes
```

### FSW Path Resolution

A sorted path list (DF) enables prefix-match resolutions:

```csharp
// Built once at Seal() time
var sortedPaths = jobs
    .OrderByDescending(p => p.Length)
    .ToList();

JobItem? FindOwningJob(string changedPath)
{
    return sortedPaths
        .Select(p => (Path: p, Job: items[p]))
        .FirstOrDefault(x => changedPath.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase))
        .Job;
}
```


Deepest-first odering ensures the most specific match wins.

### FSW Job Handler

```charp
void OnFswJobEvent(string changedPath)
{
    var owningJob = FindOwningJob(changedPath);
    if (owningJob is null) return;

    switch (owningJob.State)
    {
        case JobItemState.Pending:
            owningJob.Cts.Cancel();
            // worker dequeues, sees cancellation, skips
            break;

        case JobItemState.InProgress:
            owningJob.Cts.Cancel();           // SharpCompress loop cancels on next file
            owningJob.State = JobItemState.NeedsAttention;
            NotifyUI(owningJob, "Source changed during compression");
            break;

        // Done/Verified/Cancelled — nothing to do
    }
}
```

`NeedsAttention` is intentionally distinct from `Failed`. Failed means internal error but the other means the user should review and decide whether to rerun the item.

Note: FSW events can be coalesced or dropped on high-activity volumes. Treat them as hints, not guarantees. Always verify actual path existence before acting.

---

## Execution Loop

```csharp
await Parallel.ForEachAsync(
    job.Items.Values,
    new ParallelOptions { MaxDegreeOfParallelism = config.DegreeOfParallelism },
    async (item, globalCt) =>
    {
        if (item.Cts.IsCancellationRequested) return;

        using var linked = CancellationTokenSource
            .CreateLinkedTokenSource(item.Cts.Token, globalCt);

        item.State = JobItemState.InProgress;
        stateProgress.Report(new JobStateChange(item.SourcePath, JobItemState.InProgress));

        try
        {
            await strategy.CompressItemAsync(item, config, linked.Token);
        }
        catch (OperationCanceledException)
        {
            CleanupTmp(item);
        }
    });
```

`globalCt` is the 'cancel all' token from the UI stop button. It links to every item's token automatically.

---

## Edge Cases

### Symlinks

Skipped silently at job-build time. Logged in preflight report.

### Empty Directory Chains

`root/sub1/sub2/sub3` where all are empty. SharpCompress does not include empty directories by default, so these are left in place - no archive produced, no deletion. Log in preflight. 

### Lock Files

Filter patterns like `*.tmp`, `~$*.docx`, `*.lck` at job-build time before pre-flight attempts to open them.

### Interrupted Previous Run

On launch, scan for `.7z.tmp` files under the root. Present them in a 'stale jobs' panel, and leave any action to user.

### File Added to In-Progress Folder

If `root/sub1` is being compressed (sub1 is the effective unit) and a file is added to `root/sub1/sub2/sub3` during compression, FSW fires and the entire sub1 job is canceled. If sub3 had been the effective unit and sub1/sub2 were not marked, only sub3 is canceled. The owning-job resolution handles this naturally.

---

## UI / ViewModel Architecture

### Layer Separation

* `JobItem`: data layer, background-thread safe, no Observable
* `JobItemViewModel`: UI layer, UI-thread only, Observable. One per `JobItem`
* `MainWindowViewModel`: single window VM, holds an observable collection of `JobItemViewModel`s

`JobItemViewModel`

Once instance per `JobItem`. The UI binds a `ListView` or `DataGrid` to the collection, with a `DataTemplate` per row:

```csharp
public sealed partial class JobItemViewModel : ObservableObject
{
    public string SourcePath { get; init; }
    public long   TotalBytes { get; init; }

    [ObservableProperty] private JobItemState _state;
    [ObservableProperty] private long         _bytesProcessed;
    [ObservableProperty] private double       _progressPercent;

    public void SyncFrom(JobItem item)
    {
        // Called only from UI thread (via DispatcherTimer)
        State           = item.State;
        BytesProcessed  = Interlocked.Read(ref item.BytesProcessed);
        ProgressPercent = TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100 : 0;
    }
}
```

### Update Mechanism

Two separate update paths depending on update frequency:

#### High Frequency

`BytesProcessed` : `DispatcherTimer` at ~150ms polls only currently active items. Never touches the full collection

```csharp
// UI thread only — bounded by MaxDegreeOfParallelism
private readonly HashSet<JobItemViewModel> _activeItems = new();

var syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
syncTimer.Tick += (_, _) =>
{
    foreach (var vm in _activeItems)
    {
        var item = _job.Items[vm.SourcePath];
        vm.SyncFrom(item);
    }
    OverallProgress = (double)CompletedCount / TotalCount * 100;
};
```

#### Low Frequency

State transitions, `Progress<T>` captures the UI `SynchronizationContext` at construction time and marshals `.Report()` automatically.

```csharp
IProgress<JobStateChange> stateProgress = new Progress<JobStateChange>(change =>
{
    var vm = ItemViewModels[change.Path];
    vm.State = change.NewState;

    if (change.NewState == JobItemState.InProgress)
        _activeItems.Add(vm);
    else
        _activeItems.Remove(vm);  // terminal state

    CompletedCount = _job.Items.Values.Count(i => i.State == JobItemState.Done);
    FailedCount    = _job.Items.Values.Count(i => i.State == JobItemState.NeedsAttention);
});
```

on job completion, stop the timer and do one final authoritative sync pass across all items

`MainWindowViewModel` Aggregates

```csharp
[ObservableProperty] private int    _totalCount;
[ObservableProperty] private int    _completedCount;
[ObservableProperty] private int    _failedCount;
[ObservableProperty] private double _overallProgress;
```

---

## Tree View and Compressibility Hints

### Node States

| Icon | Meaning |
|---|---|
| ⚪ Unscanned | Not yet expanded, nothing known |
| 🟢 Good | Leaf folder — files compress well |
| 🟡 Average | Leaf folder — mixed compressibility |
| 🔴 Poor | Leaf folder — mostly incompressible |
| 🔍 Unknown | Has subfolders — deep scan required |

#### Shallow Scan (On Expand)

Triggered automatically when the user expands a tree node. Enumerates direct file children only (no recursion). For each child directory, checks `directory.EnumerateDirectories(childPath).Any()` (which stops at first hit) ot determine whether it is a leaf or has subfolders:

* Leaf: run extension heuristics -> assign Good/Average/Poor
* Has subfolders: assign unknown, show deep scan button

#### Deep Scan (On Button Click)

Recursive enumeration of all files under the node. Assings aggregate hint to that specific node. The button disappears and is replaced by the result label.

```csharp
public async Task<CompressibilityHint> DeepScanAsync(string path, CancellationToken ct)
{
    var extensions = await Task.Run(() =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                 .Select(f => Path.GetExtension(f).ToLowerInvariant()), ct);

    int total = 0, compressible = 0, incompressible = 0;
    foreach (var ext in extensions)
    {
        total++;
        if (KnownCompressible.Contains(ext))   compressible++;
        if (KnownIncompressible.Contains(ext)) incompressible++;
    }

    if (total == 0) return CompressibilityHint.Empty;

    double goodRatio = (double)compressible / total;
    double poorRatio = (double)incompressible / total;

    return (goodRatio, poorRatio) switch
    {
        (> 0.65, _) => CompressibilityHint.Good,
        (_, > 0.65) => CompressibilityHint.Poor,
        _           => CompressibilityHint.Average
    };
}
```

**Extension Reference Lists**

```csharp
private static readonly HashSet<string> KnownIncompressible =
[
    ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic",
    ".mp4", ".mkv", ".mov", ".avi", ".webm",
    ".mp3", ".aac", ".flac", ".ogg",
    ".zip", ".7z", ".gz", ".rar", ".br", ".zst"
];

private static readonly HashSet<string> KnownCompressible =
[
    ".txt", ".csv", ".json", ".xml", ".yaml", ".yml",
    ".log", ".html", ".htm", ".md", ".sql",
    ".cs", ".py", ".js", ".ts", ".go", ".rs", ".cpp", ".h",
    ".svg", ".docx", ".xlsx", ".pptx"  // these are zips internally but still compress moderately
];
```

** Hint Cache**

Shallow scans are usually cheap enough to not bother caching. Deep scan results are cached in a dictionary, keyed by absolute path.

FSW hint consumer invalidates by prefix match - a change anywhere inside a subtree invalidates that node's cached deep scan result, resetting the tree to unknown.

```csharp
void OnFswHintEvent(string changedPath)
{
    var toInvalidate = _hintCache.Keys
        .Where(k => changedPath.StartsWith(k, StringComparison.OrdinalIgnoreCase))
        .ToList();

    foreach (var key in toInvalidate)
        _hintCache.Remove(key);
}
```

---

## Suggested Project Structure

CompressorTool/
├── Core/
│   ├── Models/
│   │   ├── JobItem.cs
│   │   ├── BackupJob.cs
│   │   ├── JobConfig.cs
│   │   └── JobRule.cs
│   ├── Strategies/
│   │   ├── ICompressionStrategy.cs
│   │   ├── FastStrategy.cs
│   │   └── SafeStrategy.cs
│   ├── Compression/
│   │   ├── CompressionHelpers.cs       // shared SharpCompress logic
│   │   └── VerificationHelpers.cs
│   ├── Analysis/
│   │   ├── CompressibilityAnalyzer.cs  // shallow + deep scan
│   │   └── ExtensionLists.cs
│   └── Preflight/
│       ├── PreflightRunner.cs
│       └── PreflightReport.cs
├── UI/
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs
│   │   ├── JobItemViewModel.cs
│   │   └── TreeNodeViewModel.cs
│   └── Views/
│       ├── MainWindow.xaml
│       └── MainWindow.xaml.cs
└── CompressorTool.csproj
```
---

## Open/Deferred Questions

* Inflate/Extract: The output is already a browsable directory tree, any 7z-compatible tool can open individual archives.
* VSS (Volume Shadow Copy): Not in v1. The correct solution for backing up fully live systems, but out of scope for the targeted use case of relatively older PC backup.
* Sidecar manifest: A small json files next to each archive with metadata.
* Swap WPF with Avalon UI.
*
