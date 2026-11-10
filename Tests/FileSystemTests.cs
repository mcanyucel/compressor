using Core.FileSystem;
using Tests.Support;

namespace Tests;

public class PathsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "docs");

    [Fact]
    public void SameOrUnder_MatchesOnlyAtFolderBoundaries()
    {
        string a = Path.Combine(Base, "a");

        Assert.True(Paths.IsSameOrUnder(a, a));
        Assert.True(Paths.IsSameOrUnder(Path.Combine(a, "x.txt"), a));
        Assert.False(Paths.IsSameOrUnder(Path.Combine(Base, "abc"), a)); // plain StartsWith would say true
        Assert.False(Paths.IsSameOrUnder(Base, a));
    }

    [Fact]
    public void SameOrUnder_AncestorWithTrailingSeparator()
    {
        string root = Path.GetPathRoot(Base)!; // e.g. C:\ or /
        Assert.True(Paths.IsSameOrUnder(Base, root));
    }

    [Fact]
    public void StrictlyUnder_ExcludesThePathItself()
    {
        string a = Path.Combine(Base, "a");
        Assert.False(Paths.IsStrictlyUnder(a, a));
        Assert.True(Paths.IsStrictlyUnder(Path.Combine(a, "b"), a));
    }

    [Fact]
    public void Normalize_RemovesTrailingSeparator()
    {
        Assert.Equal(Path.Combine(Base, "a"), Paths.Normalize(Path.Combine(Base, "a") + Path.DirectorySeparatorChar));
    }

    [WindowsFact]
    public void Comparison_IsCaseInsensitiveOnWindows()
    {
        Assert.True(Paths.IsSameOrUnder(@"C:\DOCS\A\x.txt", @"c:\docs\a"));
        Assert.True(Paths.Comparer.Equals(@"C:\Docs", @"c:\docs"));
    }
}

public class SourceEnumeratorTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly IReadOnlySet<string> NoExclusions = new HashSet<string>();

    private List<string> Relative(IEnumerable<FileInfo> files) =>
        files.Select(f => Path.GetRelativePath(_dir.Root, f.FullName)).Order().ToList();

    [Fact]
    public void IncludesHiddenAndSystemFiles()
    {
        string hidden = _dir.WriteFile("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        Assert.Equal(["hidden.txt"], Relative(SourceEnumerator.EnumerateFiles(_dir.Root, NoExclusions)));
    }

    [Fact]
    public void SkipsLockAndTempFiles()
    {
        _dir.WriteFile("keep.docx");
        _dir.WriteFile("~$keep.docx");
        _dir.WriteFile("x.tmp");
        _dir.WriteFile("archive.7z.tmp");
        _dir.WriteFile("db.lck");
        _dir.WriteFile("app.lock");

        Assert.Equal(["keep.docx"], Relative(SourceEnumerator.EnumerateFiles(_dir.Root, NoExclusions)));
    }

    [Fact]
    public void PrunesExcludedFoldersAndFiles()
    {
        _dir.WriteFile(Path.Combine("src", "a.cs"));
        _dir.WriteFile(Path.Combine("bin", "app.dll"));
        _dir.WriteFile(Path.Combine("src", "skip.me"));
        var excluded = new HashSet<string>(Paths.Comparer) { _dir.P("bin"), _dir.P(Path.Combine("src", "skip.me")) };

        Assert.Equal([Path.Combine("src", "a.cs")], Relative(SourceEnumerator.EnumerateFiles(_dir.Root, excluded)));
    }

    [WindowsFact]
    public void SkipsJunctionsWithoutFollowing()
    {
        _dir.WriteFile(Path.Combine("real", "a.txt"));
        string junction = _dir.Junction("link", _dir.P("real"));
        var skipped = new List<string>();

        var files = Relative(SourceEnumerator.EnumerateFiles(_dir.Root, NoExclusions, onLinkSkipped: skipped.Add));

        Assert.Equal([Path.Combine("real", "a.txt")], files);
        Assert.Equal([junction], skipped);
    }

    [Fact]
    public void MissingFolder_ThrowsWithoutCallback_ReportsWithCallback()
    {
        string missing = _dir.P("missing");

        Assert.ThrowsAny<IOException>(() => SourceEnumerator.EnumerateFiles(missing, NoExclusions).ToList());

        var reported = new List<string>();
        Assert.Empty(SourceEnumerator.EnumerateFiles(missing, NoExclusions, onInaccessible: (p, _) => reported.Add(p)));
        Assert.Equal([missing], reported);
    }

    [Theory]
    [InlineData("~$report.docx", true)]
    [InlineData("data.TMP", true)]
    [InlineData("x.lck", true)]
    [InlineData("x.lock", true)]
    [InlineData("report.docx", false)]
    [InlineData("tmp.txt", false)]
    public void IsLockFile(string name, bool expected) => Assert.Equal(expected, SourceEnumerator.IsLockFile(name));
}
