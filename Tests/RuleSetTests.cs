using Core.Analysis;
using Core.Models;
using Core.Planning;
using Tests.Support;

namespace Tests;

public class RuleSetTests
{
    private static readonly string AB = Path.Combine("A", "B");
    private static readonly string ABC = Path.Combine("A", "B", "C");

    [Fact]
    public void CheckingAFolder_MakesItAUnit_AndCoversChildren()
    {
        var rules = new RuleSet();
        rules.Toggle("A");

        Assert.Equal(MarkState.Unit, rules.StateOf("A"));
        Assert.Equal(MarkState.Covered, rules.StateOf(AB));
        Assert.Equal(MarkState.None, rules.StateOf("Other"));
        Assert.Equal([("A", JobAction.Compress)], rules.ToRules().Select(r => (r.RelativePath, r.Action)));
    }

    [Fact]
    public void UncheckingACoveredFolder_ExcludesIt()
    {
        var rules = new RuleSet();
        rules.Toggle("A");
        rules.Toggle(AB);

        Assert.Equal(MarkState.Excluded, rules.StateOf(AB));
        Assert.Equal(MarkState.None, rules.StateOf(ABC));          // inside the excluded subtree
        Assert.Equal(JobAction.Skip, rules.RuleFor(AB));
    }

    [Fact]
    public void CheckingInsideAnExcludedFolder_MakesAnotherUnit()
    {
        var rules = new RuleSet();
        rules.Toggle("A");
        rules.Toggle(AB);
        rules.Toggle(ABC);

        Assert.Equal(MarkState.Unit, rules.StateOf(ABC));
    }

    [Fact]
    public void TogglingTwice_RestoresTheOriginalRules()
    {
        var rules = new RuleSet();
        rules.Toggle("A");
        rules.Toggle(AB);
        rules.Toggle(AB);

        Assert.Equal(MarkState.Covered, rules.StateOf(AB));
        Assert.Null(rules.RuleFor(AB));
        Assert.Single(rules.ToRules());
    }

    [Fact]
    public void ToRules_DropsRulesThatNoLongerHaveAnEffect()
    {
        var rules = new RuleSet();
        rules.Toggle("A");
        rules.Toggle(AB);  // Skip under A
        rules.Toggle("A"); // A off: the Skip is now meaningless

        Assert.Empty(rules.ToRules());
        Assert.Equal(MarkState.None, rules.StateOf(AB));
    }

    [Fact]
    public void Root_CanBeMarked()
    {
        var rules = new RuleSet();
        rules.Toggle("");

        Assert.Equal(MarkState.Unit, rules.StateOf(""));
        Assert.Equal(MarkState.Covered, rules.StateOf("A"));
    }

    [Fact]
    public void From_NormalizesSeparatorsAndRoundTrips()
    {
        var rules = RuleSet.From(
        [
            new JobRule { RelativePath = "A/", Action = JobAction.Compress },
            new JobRule { RelativePath = "A/B", Action = JobAction.Skip },
            new JobRule { RelativePath = ".", Action = JobAction.Skip }, // no effect at the root
        ]);

        Assert.Equal(MarkState.Unit, rules.StateOf("A"));
        Assert.Equal(MarkState.Excluded, rules.StateOf(AB));
        Assert.Equal([("A", JobAction.Compress), (AB, JobAction.Skip)], rules.ToRules().Select(r => (r.RelativePath, r.Action)));
    }
}

public class FolderScannerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TotalsIncludeEverythingBelow()
    {
        _dir.WriteFile("root.txt", "12");
        _dir.WriteFile(Path.Combine("A", "a.txt"), "123");
        _dir.WriteFile(Path.Combine("A", "B", "b.jpg"), "1234");
        _dir.Dir("Empty");

        FolderScan scan = FolderScanner.Scan(_dir.Root);

        Assert.Equal(3, scan.FileCount);
        Assert.Equal(9, scan.For("")!.TotalBytes);
        Assert.Equal(7, scan.For("A")!.TotalBytes);
        Assert.Equal(2, scan.For("A")!.FileCount);
        Assert.Equal(4, scan.For(Path.Combine("A", "B"))!.IncompressibleBytes);
        Assert.Null(scan.For("Empty"));
    }

    [WindowsFact]
    public void ReportsSkippedLinks()
    {
        _dir.WriteFile(Path.Combine("real", "a.txt"));
        string junction = _dir.Junction("link", _dir.P("real"));

        FolderScan scan = FolderScanner.Scan(_dir.Root);

        Assert.Equal(1, scan.FileCount);
        Assert.Equal([junction], scan.SkippedLinks);
    }
}
