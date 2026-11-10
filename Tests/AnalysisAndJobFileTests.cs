using Core.Analysis;
using Core.Models;
using Core.Planning;
using Tests.Support;

namespace Tests;

public class CompressibilityTallyTests
{
    [Fact]
    public void Empty()
    {
        var tally = new CompressibilityTally();
        Assert.Equal(CompressibilityHint.Empty, tally.Hint);
        Assert.Equal(0, tally.EstimatedArchiveBytes);
    }

    [Fact]
    public void MostlyText_IsGood()
    {
        var tally = new CompressibilityTally();
        tally.Add("a.txt", 900);
        tally.Add("b.jpg", 100);
        Assert.Equal(CompressibilityHint.Good, tally.Hint);
    }

    [Fact]
    public void WeightedByBytesNotFileCount()
    {
        // One big video next to many small text files: poor, even though most files compress well.
        var tally = new CompressibilityTally();
        for (int i = 0; i < 200; i++) tally.Add($"{i}.txt", 10);
        tally.Add("movie.MP4", 100_000);

        Assert.Equal(CompressibilityHint.Poor, tally.Hint);
        Assert.Equal(201, tally.FileCount);
    }

    [Fact]
    public void UnknownOrMixed_IsAverage()
    {
        var unknown = new CompressibilityTally();
        unknown.Add("program.exe", 1000);
        Assert.Equal(CompressibilityHint.Average, unknown.Hint);

        var mixed = new CompressibilityTally();
        mixed.Add("a.txt", 500);
        mixed.Add("b.jpg", 500);
        Assert.Equal(CompressibilityHint.Average, mixed.Hint);
    }

    [Fact]
    public void Estimate_UsesPerCategoryRatios()
    {
        var tally = new CompressibilityTally();
        tally.Add("a.txt", 1000);  // x0.3
        tally.Add("b.exe", 1000);  // x0.7
        tally.Add("c.zip", 1000);  // x1.0
        Assert.Equal(2000, tally.EstimatedArchiveBytes);
    }

    [Fact]
    public void ZipBasedOfficeFormats_CountAsUnknown()
    {
        var tally = new CompressibilityTally();
        tally.Add("report.docx", 1000);
        Assert.Equal(0, tally.CompressibleBytes);
        Assert.Equal(0, tally.IncompressibleBytes);
    }
}

public class JobFileTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void RoundTrip()
    {
        var config = new JobConfig
        {
            RootPath = _dir.Root,
            Parallelism = ParallelismLevel.Low,
            CompressionLevel = 7,
            CollisionBehavior = CollisionBehavior.Skip,
            LockedFileBehavior = LockedFileBehavior.AbortSubJob,
            Rules = [new JobRule { RelativePath = "Documents", Action = JobAction.Compress }]
        };
        string path = _dir.P("job.json");

        JobFile.Save(config, path);
        JobConfig loaded = JobFile.Load(path);

        Assert.Equal(config.RootPath, loaded.RootPath);
        Assert.Equal(ParallelismLevel.Low, loaded.Parallelism);
        Assert.Equal(7, loaded.CompressionLevel);
        Assert.Equal(CollisionBehavior.Skip, loaded.CollisionBehavior);
        Assert.Equal(LockedFileBehavior.AbortSubJob, loaded.LockedFileBehavior);
        Assert.Equal("Documents", Assert.Single(loaded.Rules).RelativePath);
    }

    [Fact]
    public void WritesCamelCaseAndEnumNames_NotComputedProperties()
    {
        string path = _dir.P("job.json");
        JobFile.Save(new JobConfig { RootPath = _dir.Root }, path);
        string json = File.ReadAllText(path);

        Assert.Contains("\"rootPath\"", json);
        Assert.Contains("\"RenameNew\"", json);
        Assert.DoesNotContain("degreeOfParallelism", json);
        Assert.DoesNotContain("threadsPerItem", json);
    }

    [Fact]
    public void AllowsCommentsAndTrailingCommas()
    {
        string root = _dir.Root.Replace("\\", "\\\\");
        string path = _dir.WriteFile("job.json", $$"""
            {
              // hand-edited
              "rootPath": "{{root}}",
              "rules": [ { "relativePath": "a/b", "action": "Skip" }, ],
            }
            """);

        Assert.Equal(JobAction.Skip, Assert.Single(JobFile.Load(path).Rules).Action);
    }

    [Theory]
    [InlineData("""{ "rules": [] }""", "rootPath")]
    [InlineData("""{ "rootPath": "relative\\path" }""", "absolute")]
    [InlineData("""{ "rootPath": "ROOT", "compressionLevel": 12 }""", "compressionLevel")]
    [InlineData("""not json""", "Invalid job file")]
    public void InvalidFiles_ThrowInvalidData(string json, string expectedInMessage)
    {
        string path = _dir.WriteFile("job.json", json.Replace("ROOT", _dir.Root.Replace("\\", "\\\\")));
        var ex = Assert.Throws<InvalidDataException>(() => JobFile.Load(path));
        Assert.Contains(expectedInMessage, ex.Message);
    }
}
