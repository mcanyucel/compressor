using Core.Models;
using Core.Planning;
using Tests.Support;

namespace Tests;

public class JobBuilderTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private BackupJob Build(params (string Path, JobAction Action)[] rules) =>
        Build(CollisionBehavior.RenameNew, rules);

    private BackupJob Build(CollisionBehavior collisions, params (string Path, JobAction Action)[] rules) =>
        JobBuilder.Build(new JobConfig
        {
            RootPath = _dir.Root,
            CollisionBehavior = collisions,
            Rules = rules.Select(r => new JobRule { RelativePath = r.Path, Action = r.Action }).ToList()
        });

    private JobItem? Item(BackupJob job, string relative) => job.Items.GetValueOrDefault(_dir.P(relative));

    private static string P(params string[] parts) => Path.Combine(parts);

    [Fact]
    public void CompressRule_BecomesPreScannedItem()
    {
        _dir.WriteFile(P("Docs", "a.txt"), "12345");
        _dir.WriteFile(P("Docs", "sub", "b.txt"), "123");

        BackupJob job = Build(("Docs", JobAction.Compress));

        JobItem item = Assert.Single(job.Items.Values);
        Assert.Equal(_dir.P("Docs"), item.SourcePath);
        Assert.Equal(_dir.P("Docs") + ".7z", item.OutputPath);
        Assert.Equal(ItemKind.Directory, item.ItemKind);
        Assert.Equal(2, item.TotalFileCount);
        Assert.Equal(8, item.TotalBytes);
        Assert.Equal(JobState.ReadyForPreflight, job.OverallState);
    }

    [Fact]
    public void FileRule_BecomesFileItem()
    {
        _dir.WriteFile("notes.txt");

        JobItem item = Assert.Single(Build(("notes.txt", JobAction.Compress)).Items.Values);

        Assert.Equal(ItemKind.File, item.ItemKind);
        Assert.Equal(_dir.P("notes.txt.7z"), item.OutputPath);
    }

    [Fact]
    public void NearestRuleWins()
    {
        _dir.WriteFile(P("Projects", "src", "a.cs"));
        _dir.WriteFile(P("Projects", "bin", "app.dll"));
        _dir.WriteFile(P("Projects", "vendor", "lib.js"));
        _dir.WriteFile(P("Projects", "vendor", "keep", "k.txt"));
        _dir.WriteFile(P("Projects", "nested", "n.txt"));

        BackupJob job = Build(
            ("Projects", JobAction.Compress),
            (P("Projects", "bin"), JobAction.Skip),
            (P("Projects", "vendor"), JobAction.Skip),
            (P("Projects", "vendor", "keep"), JobAction.Compress),  // own item again
            (P("Projects", "nested"), JobAction.Compress));         // redundant: covered

        Assert.Equal(2, job.Items.Count);

        JobItem projects = Item(job, "Projects")!;
        Assert.Equal(2, projects.TotalFileCount); // src\a.cs, nested\n.txt
        Assert.Contains(_dir.P(P("Projects", "bin")), projects.ExcludedPaths);
        Assert.Contains(_dir.P(P("Projects", "vendor")), projects.ExcludedPaths);

        Assert.Equal(1, Item(job, P("Projects", "vendor", "keep"))!.TotalFileCount);
    }

    [Fact]
    public void RulePathsMayUseForwardSlashes()
    {
        _dir.WriteFile(P("A", "keep.txt"));
        _dir.WriteFile(P("A", "skip", "x.txt"));

        BackupJob job = Build(("A", JobAction.Compress), ("A/skip", JobAction.Skip));

        Assert.Equal(1, Item(job, "A")!.TotalFileCount);
    }

    [Fact]
    public void WholeRoot_ArchiveGoesNextToRoot()
    {
        _dir.WriteFile("a.txt");

        JobItem item = Assert.Single(Build(("", JobAction.Compress)).Items.Values);

        Assert.Equal(_dir.Root + ".7z", item.OutputPath);
    }

    [Fact]
    public void Collision_RenameNew_PicksNumberedName()
    {
        _dir.WriteFile(P("Music", "m.txt"));
        _dir.WriteFile("Music.7z", "existing");

        BackupJob job = Build(("Music", JobAction.Compress));

        Assert.Equal(_dir.P("Music_001.7z"), Item(job, "Music")!.OutputPath);
        Assert.Contains(_dir.P("Music.7z"), job.Preflight!.Collisions);
    }

    [Fact]
    public void Collision_Skip_LeavesItemOut()
    {
        _dir.WriteFile(P("Music", "m.txt"));
        _dir.WriteFile("Music.7z", "existing");

        BackupJob job = Build(CollisionBehavior.Skip, ("Music", JobAction.Compress));

        Assert.Empty(job.Items);
        Assert.Contains(_dir.P("Music.7z"), job.Preflight!.Collisions);
    }

    [Fact]
    public void LeftoverTmp_IsReportedAndAvoided()
    {
        _dir.WriteFile(P("Old", "o.txt"));
        _dir.WriteFile("Old.7z.tmp", "stale");

        BackupJob job = Build(CollisionBehavior.Skip, ("Old", JobAction.Compress));

        Assert.Equal(_dir.P("Old_001.7z"), Item(job, "Old")!.OutputPath); // even under Skip: it's not an archive collision
        Assert.Contains(_dir.P("Old.7z.tmp"), job.Preflight!.StaleTmpFiles);
    }

    [Fact]
    public void EmptyFolder_IsReportedWithoutItem()
    {
        _dir.Dir(P("Empty", "sub"));
        _dir.WriteFile(P("Empty", "~$lock.docx")); // filtered out, so still empty

        BackupJob job = Build(("Empty", JobAction.Compress));

        Assert.Empty(job.Items);
        Assert.Contains(_dir.P("Empty"), job.Preflight!.EmptyItems);
    }

    [Fact]
    public void InvalidRules_AreReportedAndIgnored()
    {
        _dir.WriteFile(P("Docs", "photos", "p.jpg"));

        BackupJob job = Build(
            ("Missing", JobAction.Compress),
            (P("..", "outside"), JobAction.Compress),
            (Path.GetFullPath(_dir.P("Docs")), JobAction.Compress),
            (P("Docs", "photos"), JobAction.Compress),
            (P("Docs", "photos"), JobAction.Skip));

        Assert.Empty(job.Items);
        List<string> invalid = job.Preflight!.InvalidRules;
        Assert.Equal(4, invalid.Count);
        Assert.Contains(invalid, m => m.Contains("does not exist"));
        Assert.Contains(invalid, m => m.Contains("outside the root"));
        Assert.Contains(invalid, m => m.Contains("must be relative"));
        Assert.Contains(invalid, m => m.Contains("Conflicting"));
    }

    [WindowsFact]
    public void Junctions_AreNeverFollowed()
    {
        _dir.WriteFile(P("Docs", "d.txt"));
        _dir.WriteFile(P("Profile", "real.txt"));
        string junction = _dir.Junction(P("Profile", "Application Data"), _dir.P("Docs"));

        BackupJob job = Build(("Profile", JobAction.Compress), (P("Profile", "Application Data"), JobAction.Compress));

        Assert.Equal(1, Item(job, "Profile")!.TotalFileCount);
        Assert.Contains(junction, job.Preflight!.SkippedLinks);
        Assert.Contains(job.Preflight.InvalidRules, m => m.Contains("link or junction"));
    }

    [Fact]
    public void MissingRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => JobBuilder.Build(new JobConfig { RootPath = _dir.P("nope") }));
        Assert.Throws<ArgumentException>(() => JobBuilder.Build(new JobConfig { RootPath = "relative" }));
    }
}
