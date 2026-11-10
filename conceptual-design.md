# Selective Compression Backup Tool - Design Document

## Philosophy

Most backup tools treat the archives as the primary artifact. This tool treats the **folder tree** as the primary artifact: compression is a property applied to individual nodes, not a transformation of the whole tree.

Motivation: compressing an old directory into a single archive makes its contents invisible to file-name indexers such as Everything and Windows Search. Compressing selected nodes instead keeps the folder structure (and every uncompressed name) indexable, while the bulky, compressible parts shrink.

Goals:
* Preserve navigability: The output is still a browsable directory structure (unless the topmost node is marked to compress)
* Avoid re-compressing already-incompressible content (images, videos, existing archives)
* Allow per-folder and per-file compression decisions
* Run silently after a pre-flight gate - no prompts mid-job
* Target old hardware gracefully: Controllable parallelism, low overhead.
* Free and open-source; Windows is the primary target, but the app should also run on Linux and macOS.

--- 

## Decisions (November 2026)

| Decision | Reason |
| -- | -- |
| **UI: Avalonia instead of WPF** | FOSS project: contributors on Linux/macOS can build and run the UI. Same XAML + MVVM model as WPF, and CommunityToolkit.Mvvm works unchanged. |
| **Compression engine: bundled 7-Zip command line instead of SharpCompress** | Native, multithreaded LZMA2 is much faster than a managed implementation and is battle-tested. 7-Zip also handles verification (`t` and `l` commands). See [Bundling 7-Zip](#bundling-7-zip). |
| **File watching deferred to v2** | Folders being archived are normally idle. v1 treats sources as static and relies on pre-flight checks plus post-compression verification. The FSW design is kept below, marked v2. |
| **One compression strategy (Fast and Safe merged)** | With 7-Zip as a subprocess, cancellation always kills the process, so the strategies had become identical. Reacting to source changes belongs to the executor and is controlled by `SourceChangeBehavior`. |
| **Console front end alongside the GUI** | Lets the engine be tested end-to-end before the UI exists, runs on CI, and gives power users something to script. |

---

## Tech Stack

| Concern | Choice |
| --      | --     |
| Language, runtime | C#, .NET 10 |
| UI Framework | Avalonia, CommunityToolkit.Mvvm |
| Compression | 7-Zip command line (`7za.exe` on Windows, `7zz` on Linux/macOS), run as a subprocess |
| Serialization | System.Text.Json |
| FileSystem Events | FileSystemWatcher (v2) |

---

## Bundling 7-Zip

7-Zip is mostly LGPL 2.1+, with some BSD 3-clause parts. Running the unmodified binary as a separate process places no license requirement on the app's own code.

* Ship the standalone console build from the **7-Zip Extra** package (`7za.exe`) on Windows, and the official `7zz` builds on Linux/macOS. `7za` does not contain the unRAR code, so the unRAR license restriction does not apply. It supports every format this tool needs (7z, zip, tar, gzip, xz).
* Include 7-Zip's `License.txt`, name the bundled version, and link to the matching source download on 7-zip.org.
* Pin one version. Store its SHA-256 next to the binary and check it at startup; refuse to run a binary that doesn't match.
* Lookup order (`SevenZipLocator`): path set in app settings → bundled binary (hash-checked). An installed 7-Zip (Windows registry `HKLM\SOFTWARE\7-Zip`, value `Path`) may be offered as an option, but the bundled copy is the default so behavior is predictable across machines.
* Do not modify 7-Zip. If that ever becomes necessary, the modified source must be published under the LGPL.

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

### Rules: Nearest Rule Wins

Marks are stored as `JobRule`s (`Compress` or `Skip`). `JobBuilder` applies them so that each file and folder follows the **closest rule at or above it**:

* A `Compress` rule whose nearest ruled ancestor is not `Compress` starts an item. One under another `Compress` is redundant (covered). With `Compress` rules only, this is exactly the DFS above.
* A `Skip` rule inside a compressed folder excludes that subtree from the archive. It stays on disk unchanged. Example: compress `Projects` but skip `Projectsin`.
* A `Compress` rule under such a `Skip` starts its own item again.

Invalid rules are reported in the pre-flight report and ignored: paths that don't exist, are outside the root, are absolute, or are links/junctions, and paths with both `Compress` and `Skip` (both ignored).

Each item is pre-scanned at build time (file count and bytes, using the same file listing as compression). Marked folders with no files are reported and get no item. A drive root can't be an item, because its archive would have nowhere to go.

--- 

## Data Model

`JobItem`

Pure data class - no observable pattern, thus safe to mutate from background threads via `volatile` and `Interlocked`:

```csharp
public enum JobItemState { Pending, InProgress, Verified, Done, Skipped, Cancelled, NeedsAttention, Failed }
public enum ItemKind { File, Directory }

public sealed class JobItem
{
  // Identity - set once at job-build time, never mutated
  public string SourcePath { get; init; }
  public string OutputPath { get; init; } // final .7z path
  public ItemKind ItemKind { get; init; }
  public long TotalBytes { get; init; } // prescanned at job-build time
  public int TotalFileCount { get; init; } // prescanned at job-build time, used by verification

  // Runtime state - background-thread-safe
  public volatile JobItemState State;
  public long BytesProcessed; // mutated via Interlocked; estimated from 7-Zip's percentage output
  public int FilesProcessed;  // mutated via Interlocked

  // Control
  public CancellationTokenSource Cts { get; } = new();

  // Post-run metadata - written once after completion
  public JobItemError? Error { get; set; }
  public JobItemStats? Stats { get; set; }
}
```

`BackupJob`

Wraps the item dictionary and overall lifecycle. Sealed after job-build so no structural mutation happens during execution.

```csharp
public sealed class BackupJob
{
  public JobConfig Config { get; init; }

  // Keyed by normalized absolute path - OrdinalIgnoreCase for Windows
  public ConcurrentDictionary<string, JobItem> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

  // Sorted descending by length (deepest first) for FSW prefix match (v2)
  public IReadOnlyList<string> SortedPaths { get; private set; }

  public JobState OverallState { get; private set; }
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

Serialized as JSON by `JobFile` (camelCase, enums as strings, comments and trailing commas allowed so it can be edited by hand). The job file is the unit of persistence. `rootPath` must be absolute; rule paths may use `\` or `/`.

```csharp
public sealed class JobConfig
{
    public string                  RootPath             { get; init; }
    public ParallelismLevel        Parallelism          { get; init; } = ParallelismLevel.Normal;
    public int                     CompressionLevel     { get; init; } = 5;     // 7-Zip -mx (0-9)
    public CollisionBehavior       CollisionBehavior    { get; init; } = CollisionBehavior.RenameNew;
    public LockedFileBehavior      LockedFileBehavior   { get; init; } = LockedFileBehavior.Skip;
    public SourceChangeBehavior    SourceChangeBehavior { get; init; } = SourceChangeBehavior.CancelSubJob; // v2
    public bool                    DeleteAfterVerify    { get; init; } = false;
    public List<JobRule>           Rules                { get; init; } = [];
}

public sealed class JobRule
{
    public string     RelativePath { get; init; }   // relative to RootPath
    public JobAction  Action       { get; init; }   // Compress | Skip
}
```

Example job file:

```json
{
  "rootPath": "C:\\OldPC\\Users\\mustafa",
  "parallelism": "Normal",
  "compressionLevel": 5,
  "collisionBehavior": "RenameNew",
  "lockedFileBehavior": "Skip",
  "deleteAfterVerify": false,
  "rules": [
    { "relativePath": "Documents\\contracts", "action": "Compress" },
    { "relativePath": "Documents\\photos",    "action": "Skip" },
    { "relativePath": "Documents\\notes.txt", "action": "Compress" }
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

With 7-Zip, the level defines a **thread budget** `B` rather than a number of items. Two knobs share that budget:

* Items compressed concurrently: `C = max(1, B / 2)`
* Threads per 7-Zip process: `-mmt=max(1, B / C)` (normally 2)

Rationale: LZMA2 splits large inputs into blocks, so one big folder benefits from extra threads, while many small items benefit from concurrency. These defaults should be revisited with benchmarks. Memory use grows with both threads and compression level, so old hardware should stay at `-mx=5` or below.

---

## Strategy Pattern

Pre-flight always runs and doesn't depend on the strategy. The strategy defines how one item is compressed:

```csharp
public interface ICompressionStrategy
{
    Task CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct);
}
```

There is one implementation, `SevenZipStrategy`, which runs 7-Zip. Cancelling its token (per-item cancel, the global stop button, or the v2 file watcher cancelling `JobItem.Cts`) kills the 7-Zip process.

An earlier design had two strategies: `Fast` (no file watching) and `Safe` (checks for cancellation between files, for live machines). With 7-Zip running as a separate process there is no "between files" any more, so they were merged. Whether to react to source changes is now an executor concern, controlled by `SourceChangeBehavior` (`CancelSubJob` or `Ignore`).

The interface stays so the executor can be tested with a fake strategy.

Shared implementation lives in static helpers: building the file list, running 7-Zip, the `.7z.tmp` rename, and post-compression verification.

---

## Compression Pipeline (Per Item)

```
Write file list (UTF-8) for the item
      ↓
7za a → .7z.tmp
      ↓
Rename .7z.tmp → .7z
      ↓
Verify: 7za t   (re-reads the archive and checks every CRC)
Verify: 7za l   (archive holds exactly the files handed to 7-Zip, same paths and sizes)
      ↓
[Optional] Delete originals (only if each source file's size and modified time still match)
```

### Verification

`CompressAsync` records each file's relative path, size and modified time just before handing the list to 7-Zip (`CompressionResult.Files`). `VerificationHelpers.VerifyAsync` then:

1. Runs `7za t`: any CRC error fails verification.
2. Runs `7za l -slt` and compares the file entries with the recorded list. It reports files missing from the archive, unexpected files, and size mismatches (a file that changed while being compressed).

This is stricter than comparing file counts, which would miss one file skipped and another added. Path comparison ignores case on Windows and macOS.

The recorded modified times are for the delete step: a source file that changed after compression must not be deleted.

Writing to `.7z.tmp` first means interrupted jobs leave no orphan corrupted .7z files. On next launch, stale `.7z.tmp` files are detected and either cleaned up or flagged.

### 7-Zip Invocation

```
7za a -t7z -mx=<level> -mmt=<threads> -bsp1 -bso0 -scsUTF-8 "<output>.7z.tmp" @"<listfile>"
```

* Working directory = the item's source folder (for a file item: its parent), so the archive stores paths relative to the item.
* `@listfile` contains exactly the files collected at job-build time (symlinks and lock files already removed). That keeps the archive and the file-count check in agreement, and avoids command-line length limits.
* `-bsp1` writes progress percentages to stdout. Parse them to update `BytesProcessed` (`percent × TotalBytes`).
* Add `-sse` when `LockedFileBehavior == AbortSubJob`: 7-Zip then stops (exit `2`, no archive) if it can't open an input file. Note: a file that no longer exists is only a scan warning (exit `1`), even with `-sse`.
* Always pass `-t7z` explicitly, since the output name ends in `.tmp`.
* The archive must not exist beforehand: `7z a` on an existing archive updates it instead of creating a new one. `SevenZipRunner` refuses to run in that case.
* Do not put `--` before the arguments: it stops 7-Zip from treating `@listfile` as a list file. Pass absolute paths instead, so they can't be mistaken for switches.
* stdin is closed and `-y` is passed, so 7-Zip can never block waiting for input (e.g. a password prompt).

Exit codes: `0` OK · `1` warning (e.g. some files could not be opened) · `2` fatal error · `7` command-line error · `8` not enough memory · `255` stopped by user. Exit `1` marks the item `NeedsAttention` (the archive is kept as `.7z.tmp` for the user to review, never renamed). `2` and above are errors.

### Cancellation

The unit of cancellation is the 7-Zip process. When the item's linked token fires:

```csharp
using var reg = ct.Register(() => process.Kill(entireProcessTree: true));
await process.WaitForExitAsync(CancellationToken.None);
ct.ThrowIfCancellationRequested(); // caller deletes the .7z.tmp
```

## Locked File Handling

Pre-flight already reports files that can't be opened. During compression, 7-Zip reports files it couldn't open as warnings (exit code `1`):

* `LockedFileBehavior.Skip`: the archive is completed without the file but left as `.7z.tmp`, the item is marked `NeedsAttention`, and the warning text from stderr is stored in `JobItem.Error`. The file-count check would also catch the missing file.
* `LockedFileBehavior.AbortSubJob`: run with `-sse`; the item fails and its `.7z.tmp` is deleted.

Either way, an item with a locked file never reaches `Done`, and its originals are never deleted.

## Name Collisions

`folder.7z` already existing when we try to create it. Options:

* `RenameNew` append unique suffix to newly created archive (safe, recommended)
* `Skip` leave the existing archive, skip this job item
* ~~`Overwrite`~~: dropped; overwriting an archive that may be open or still needed is too risky.

The output path is `<source>.7z` next to the source (for a file: `notes.txt.7z`). A path counts as taken if a file or folder exists there, another item already plans to use it, or a leftover `.7z.tmp` sits next to it. A leftover `.7z.tmp` is reported separately, and the item gets a suffixed name even under `Skip`, since compression refuses to touch a leftover.

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
  // 6. Locate 7-Zip and check its hash
  // 7. Check free space on the output drive against an estimate of the archive sizes
}
```

### Implementation

Split between two steps that fill the same `PreflightReport`:

* `JobBuilder` (build time): invalid rules, collisions, leftover `.7z.tmp` files, skipped links, unreadable folders, empty items, and the per-item size pre-scan.
* `PreflightRunner` (touches every file): locked files, compressibility per item, and free space per output drive.
  * Locked files are found by opening each file with `FileShare.Read`, the way 7-Zip opens it, so the result matches what compression will hit.
  * Free space is compared with the estimated archive sizes (see below), not the source sizes.

Locating 7-Zip is done by the front end before building the job, since a missing 7-Zip blocks everything.

### Pre-Flight Summary UI

```
Analyzing job...
  ✔  12 items ready
  ⚠   2 folders contain mostly images — compression savings likely <5%
  ⚠   1 file currently locked: C:\...\report.docx
  ⚠   1 name collision: contracts.7z already exists

  On collision:      [Rename new ▾]
  On locked file:    [Skip ▾]
  Compression level: [Normal (5) ▾]
  Delete originals after verification: [ ]

  [Start Job]   [Save Job File]
```

---

## FileSystemWatcher (v2)

> **Deferred to v2.** v1 does not watch the file system. The folders being archived are expected to be idle; pre-flight checks and post-compression verification (CRC check, plus paths and sizes) catch most problems. The design below is kept for v2.

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


Deepest-first ordering ensures the most specific match wins.

A plain `StartsWith` is not enough: `C:\docs\a` would also match `C:\docs\abc\file.txt`. A match must end at a path separator (or be the whole path). `BackupJob.FindOwningItem` implements this check.

### FSW Job Handler

```csharp
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
            owningJob.Cts.Cancel();           // kills the item's 7-Zip process
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

`JobExecutor.RunAsync(job, stateProgress, ct)` runs a sealed job whose pre-flight has completed (`JobState.ReadyToExecute`); otherwise it throws.

* Only `Pending` items run, so a job can be rerun after resetting the items to retry.
* Items are ordered **largest first**, so long items don't end up running alone at the end.
* Up to `JobConfig.ConcurrentItems` items run at once (`Parallel.ForEachAsync`). Each item's token is linked to the global stop token.
* One item's failure never stops the others: exceptions are caught per item.
* `Error` and `Stats` are written before the terminal state, so they are safe to read when the state change is reported.

State mapping per item:

| Result | Item state |
|---|---|
| Compressed and verified | `InProgress → Verified → Done` (stats recorded) |
| Nothing to compress | `Skipped` |
| 7-Zip warnings (e.g. locked file) | `NeedsAttention`, archive kept as `.7z.tmp` |
| Verification failed | `NeedsAttention`, archive kept as `.7z` |
| 7-Zip failed, unreadable source, unexpected exception | `Failed`, no archive |
| Item cancelled (before or during) | `Cancelled` |
| Global stop | running and queued items → `Cancelled`; job → `JobState.Cancelled` |

The job ends as `Completed`, or `CompletedWithErrors` if any item is `NeedsAttention`, `Failed` or `Cancelled`.

Delete-after-verify will run between `Verified` and `Done`. Until it exists, `RunAsync` refuses a job with `DeleteAfterVerify = true`.

---

## Edge Cases

### Symlinks

Symbolic links and junctions are never followed, at job-build time or during compression; they are listed in the pre-flight report. This matters for old Windows profiles, which contain junctions such as `Application Data` that would otherwise be archived twice or loop forever.

Other reparse points (e.g. OneDrive files, deduplicated files) are treated as normal files: skipping everything with the reparse-point attribute would silently drop real data.

Hidden and system files are included.

### Empty Directory Chains

`root/sub1/sub2/sub3` where all are empty. The list file passed to 7-Zip contains only files, so empty directories are not stored. An item with no files is left in place - no archive produced, no deletion. Log in preflight.

Open point: an empty subfolder *inside* a unit that does contain files is not stored either, so delete-after-verify would lose it. Either add directory entries to the list file (without letting 7-Zip recurse into them), or keep empty subfolders when deleting originals.

### Lock Files

Files matching `~$*`, `*.tmp`, `*.lck` and `*.lock` are skipped when listing a folder (`SourceEnumerator`), at build time and during compression. A file marked explicitly is archived even if its name matches.

### Interrupted Previous Run

On launch, scan for `.7z.tmp` files under the root. Present them in a 'stale jobs' panel, and leave any action to user.

### File Added to In-Progress Folder (v2)

If `root/sub1` is being compressed (sub1 is the effective unit) and a file is added to `root/sub1/sub2/sub3` during compression, FSW fires and the entire sub1 job is canceled. If sub3 had been the effective unit and sub1/sub2 were not marked, only sub3 is canceled. The owning-job resolution handles this naturally.

---

## UI / ViewModel Architecture

The UI is built with **Avalonia** and CommunityToolkit.Mvvm. The layering below is framework-agnostic. Avalonia-specific notes:

* Views are `.axaml` files. Use compiled bindings (`x:DataType`) so binding errors show up at build time.
* `Avalonia.Threading.DispatcherTimer` replaces the WPF one; the API is the same.
* `Progress<T>` works unchanged: Avalonia installs a `SynchronizationContext` on the UI thread.
* The folder tree is a `TreeView` whose children are loaded lazily on expand (each unexpanded node holds a placeholder child so the expander shows). Large trees must never be loaded up front.
* The job list uses a virtualizing `ListBox`, or `DataGrid` from the `Avalonia.Controls.DataGrid` package.

### Layer Separation

* `JobItem`: data layer, background-thread safe, no Observable
* `JobItemViewModel`: UI layer, UI-thread only, Observable. One per `JobItem`
* `MainWindowViewModel`: single window VM, holds an observable collection of `JobItemViewModel`s

`JobItemViewModel`

One instance per `JobItem`. The UI binds a `ListBox` or `DataGrid` to the collection, with a `DataTemplate` per row:

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

#### Weighting by Bytes

The implementation (`CompressibilityTally`) weights by **bytes**, not file count as in the sketch below: one 4 GB video next to 200 small text files is a poor candidate even though most of its files compress well. The same tally gives a rough archive size (known-compressible ×0.3, unknown ×0.7, incompressible ×1.0), used for the disk-space check and the savings preview.

#### Deep Scan (On Button Click)

Recursive enumeration of all files under the node. Assigns aggregate hint to that specific node. The button disappears and is replaced by the result label.

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

The current lists live in `Core/Analysis/ExtensionLists.cs`. Changes from the sketch below: more audio/video/archive types; old binary Office formats (`.doc`, `.xls`, `.ppt`), `.pst`, `.mdb`, `.bmp`, `.tif`, `.wav` added as compressible (common on old PCs); `.docx`/`.xlsx`/`.pptx` moved out of the compressible list, since they are zip files internally and gain little.

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

**Hint Cache**

Shallow scans are usually cheap enough to not bother caching. Deep scan results are cached in a dictionary, keyed by absolute path.

In v1, a cached result is cleared by a **Refresh** action on the node and after a job finishes. In v2, the FSW hint consumer invalidates by prefix match - a change anywhere inside a subtree invalidates that node's cached deep scan result, resetting the tree to unknown.

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

```
Compressor/
├── Compressor.slnx
├── Core/                               // class library, no UI references
│   ├── Models/
│   │   ├── JobItem.cs
│   │   ├── BackupJob.cs
│   │   ├── JobConfig.cs
│   │   └── JobRule.cs
│   ├── FileSystem/
│   │   ├── Paths.cs                    // path comparison and normalization
│   │   └── SourceEnumerator.cs         // file listing: links, lock files, exclusions
│   ├── Planning/
│   │   ├── JobBuilder.cs               // rules → effective units → JobItems
│   │   └── JobFile.cs                  // JSON load / save
│   ├── Strategies/
│   │   ├── ICompressionStrategy.cs
│   │   └── SevenZipStrategy.cs
│   ├── SevenZip/
│   │   ├── SevenZipLocator.cs          // find bundled/configured binary, check hash
│   │   └── SevenZipRunner.cs           // process wrapper: args, progress parsing, exit codes, kill on cancel
│   ├── Compression/
│   │   ├── CompressionHelpers.cs       // file collection, collisions, .7z.tmp handling
│   │   └── VerificationHelpers.cs      // 7za t + contents check via 7za l
│   ├── Execution/
│   │   └── JobExecutor.cs              // Parallel.ForEachAsync loop
│   ├── Analysis/
│   │   ├── CompressibilityTally.cs     // hint + size estimate, weighted by bytes
│   │   ├── ExtensionLists.cs
│   │   └── (tree shallow/deep scan: with the UI)
│   └── Preflight/
│       ├── PreflightRunner.cs
│       └── PreflightReport.cs
├── Cli/                                // console front end (assembly: compressor)
│   ├── ScanCommand.cs                  // scan <folder> [--depth N] [--write job.json]
│   └── RunCommand.cs                   // plan <job.json> / run <job.json> [--yes]
├── Ui/                                 // Avalonia app
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs
│   │   ├── JobItemViewModel.cs
│   │   └── TreeNodeViewModel.cs
│   └── Views/
│       ├── MainWindow.axaml
│       └── MainWindow.axaml.cs
├── Tests/
└── third-party/7zip/
    ├── win-x64/7za.exe
    ├── linux-x64/7zz
    ├── osx/7zz
    ├── License.txt
    ├── VERSION                         // 7-Zip version shipped
    └── SHA256SUMS                      // sha256sum format, e.g. "<hex>  win-x64/7za.exe"; checked at startup
```

SharpCompress has been removed; all compression and verification goes through 7-Zip.

---

## Open/Deferred Questions

* Inflate/Extract: The output is already a browsable directory tree, any 7z-compatible tool can open individual archives.
* VSS (Volume Shadow Copy): Not in v1. The correct solution for backing up fully live systems, but out of scope for the targeted use case of relatively older PC backup.
* Sidecar manifest: A small json files next to each archive with metadata.
* App license: pick one (MIT, GPL-3.0, ...). Bundling the unmodified 7-Zip binary is compatible with either.
* Empty subfolders inside a unit (see Edge Cases).
* Benchmark the thread-budget defaults (see Parallelism) on old hardware.
* NTFS compression (`compact /exe:lzx`) as an alternative mode for nodes that should stay fully browsable on NTFS drives.
