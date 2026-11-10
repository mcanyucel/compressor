using Core.Compression;
using Core.Models;
using Tests.Support;

namespace Tests;

public class OriginalsDeleterTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static string P(params string[] parts) => Path.Combine(parts);

    /// <summary>The archived list as compression would record it: current size and time of each file.</summary>
    private List<ArchivedFile> Archived(string sourceRoot, params string[] relativePaths) =>
        relativePaths.Select(r =>
        {
            var info = new FileInfo(Path.Combine(sourceRoot, r));
            return new ArchivedFile(r, info.Length, info.LastWriteTimeUtc);
        }).ToList();

    [Fact]
    public void DeletesArchivedFiles_AndTheFoldersTheyEmptied()
    {
        string docs = _dir.Dir("docs");
        _dir.WriteFile(P("docs", "a.txt"));
        _dir.WriteFile(P("docs", "sub", "b.txt"));
        _dir.WriteFile(P("docs", "sub", "deeper", "c.txt"));
        var archived = Archived(docs, "a.txt", P("sub", "b.txt"), P("sub", "deeper", "c.txt"));

        DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(docs), archived);

        Assert.True(result.Complete);
        Assert.Equal(3, result.Deleted);
        Assert.False(Directory.Exists(docs)); // the item folder itself became empty
        Assert.True(Directory.Exists(_dir.Root));
    }

    [Fact]
    public void KeepsFoldersThatWereAlreadyEmpty()
    {
        string docs = _dir.Dir("docs");
        _dir.WriteFile(P("docs", "a.txt"));
        string emptyBefore = _dir.Dir(P("docs", "empty-before"));

        OriginalsDeleter.Delete(TestJobs.Item(docs), Archived(docs, "a.txt"));

        Assert.True(Directory.Exists(emptyBefore)); // not in the archive, so it must stay
        Assert.False(File.Exists(_dir.P(P("docs", "a.txt"))));
    }

    [Fact]
    public void NeverTouchesFilesThatWereNotArchived()
    {
        string docs = _dir.Dir("docs");
        _dir.WriteFile(P("docs", "a.txt"));
        string excluded = _dir.WriteFile(P("docs", "bin", "app.dll"));
        string lockFile = _dir.WriteFile(P("docs", "~$report.docx"));
        string hidden = _dir.WriteFile(P("docs", "sub", "hidden.ini"));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        _dir.WriteFile(P("docs", "sub", "b.txt"));

        DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(docs), Archived(docs, "a.txt", P("sub", "b.txt")));

        Assert.True(result.Complete);
        Assert.True(File.Exists(excluded));
        Assert.True(File.Exists(lockFile));
        Assert.True(File.Exists(hidden)); // a hidden leftover keeps its folder
        Assert.False(File.Exists(_dir.P(P("docs", "sub", "b.txt"))));
    }

    [Fact]
    public void ChangedFiles_AreKept()
    {
        string docs = _dir.Dir("docs");
        string grown = _dir.WriteFile(P("docs", "grown.txt"), "abc");
        string touched = _dir.WriteFile(P("docs", "sub", "touched.txt"), "abc");
        var archived = Archived(docs, "grown.txt", P("sub", "touched.txt"));

        File.AppendAllText(grown, "more");                                   // size differs
        File.SetLastWriteTimeUtc(touched, DateTime.UtcNow.AddMinutes(5));    // same size, newer time

        DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(docs), archived);

        Assert.False(result.Complete);
        Assert.Equal(2, result.Kept.Count);
        Assert.All(result.Kept, k => Assert.Contains("changed since it was archived", k));
        Assert.True(File.Exists(grown));
        Assert.True(File.Exists(touched));
    }

    [Fact]
    public void AlreadyGoneFiles_AreCounted_NotKept()
    {
        string docs = _dir.Dir("docs");
        string gone = _dir.WriteFile(P("docs", "gone.txt"));
        _dir.WriteFile(P("docs", "a.txt"));
        var archived = Archived(docs, "gone.txt", "a.txt");
        File.Delete(gone);

        DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(docs), archived);

        Assert.True(result.Complete);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.AlreadyGone);
    }

    [Fact]
    public void ReadOnlyFiles_AreDeleted()
    {
        string docs = _dir.Dir("docs");
        string readOnly = _dir.WriteFile(P("docs", "cd-copy.txt"));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(docs), Archived(docs, "cd-copy.txt"));

        Assert.True(result.Complete);
        Assert.False(File.Exists(readOnly));
    }

    [Fact]
    public void PathsOutsideTheItem_AreRefused()
    {
        string docs = _dir.Dir("docs");
        string outside = _dir.WriteFile("outside.txt");
        var info = new FileInfo(outside);

        DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(docs),
            [new ArchivedFile(P("..", "outside.txt"), info.Length, info.LastWriteTimeUtc)]);

        Assert.Single(result.Kept);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void FileItem_DeletesOnlyTheFile_ParentStays()
    {
        string folder = _dir.Dir("folder");
        string notes = _dir.WriteFile(P("folder", "notes.txt"));

        DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(notes, ItemKind.File), Archived(folder, "notes.txt"));

        Assert.True(result.Complete);
        Assert.False(File.Exists(notes));
        Assert.True(Directory.Exists(folder)); // even though it's now empty
    }

    [WindowsFact]
    public void LockedFiles_AreKept()
    {
        string docs = _dir.Dir("docs");
        string locked = _dir.WriteFile(P("docs", "locked.txt"));
        var archived = Archived(docs, "locked.txt");

        using (TestJobs.Lock(locked))
        {
            DeletionResult result = OriginalsDeleter.Delete(TestJobs.Item(docs), archived);

            Assert.Contains("could not delete", Assert.Single(result.Kept));
        }

        Assert.True(File.Exists(locked));
    }
}
