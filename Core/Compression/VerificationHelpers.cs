using Core.FileSystem;
using Core.SevenZip;

namespace Core.Compression;

public sealed record VerificationResult(IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;

    public static VerificationResult Success { get; } = new([]);
}

/// <summary>
/// Post-compression integrity verification. Two checks, both through 7-Zip:
/// <list type="number">
/// <item><c>7z t</c> re-reads the whole archive and checks every CRC.</item>
/// <item><c>7z l</c> lists the archive, which must contain exactly the files handed to 7-Zip, with the same sizes.
/// This catches files that were skipped, and files that changed size while being compressed.</item>
/// </list>
/// Only a passing result should allow the originals to be deleted.
/// </summary>
public static class VerificationHelpers
{
    /// <param name="archivePath">The final <c>.7z</c> (after the rename from <c>.7z.tmp</c>).</param>
    /// <param name="expected">From <see cref="CompressionResult.Files"/>.</param>
    public static async Task<VerificationResult> VerifyAsync(
        string archivePath,
        IReadOnlyList<ArchivedFile> expected,
        SevenZipRunner runner,
        CancellationToken ct)
    {
        if (!File.Exists(archivePath))
            return new VerificationResult([$"Archive not found: {archivePath}"]);

        // ── 1. Integrity ─────────────────────────────────────────────────────────────
        SevenZipResult test = await runner.TestAsync(archivePath, ct);
        if (!test.Succeeded)
            return new VerificationResult([$"Integrity test failed ({test.Status}, exit code {test.ExitCode}): {test.ErrorOutput}"]);

        // ── 2. Contents ──────────────────────────────────────────────────────────────
        SevenZipListResult list = await runner.ListAsync(archivePath, ct);
        if (!list.Result.Succeeded)
            return new VerificationResult([$"Could not list the archive ({list.Result.Status}, exit code {list.Result.ExitCode}): {list.Result.ErrorOutput}"]);

        return CompareContents(expected, list.Entries);
    }

    /// <summary>
    /// Compares the archive's file entries with the expected files: same set of paths, same sizes.
    /// Directory entries are ignored; only files are handed to 7-Zip.
    /// </summary>
    public static VerificationResult CompareContents(IReadOnlyList<ArchivedFile> expected, IReadOnlyList<SevenZipEntry> entries)
    {
        var problems = new List<string>();
        var inArchive = new Dictionary<string, SevenZipEntry>(PathComparer);

        foreach (SevenZipEntry entry in entries)
        {
            if (entry.IsDirectory) continue;
            if (!inArchive.TryAdd(Normalize(entry.Path), entry))
                problems.Add($"Duplicate entry in archive: {entry.Path}");
        }

        foreach (ArchivedFile file in expected)
        {
            if (!inArchive.Remove(Normalize(file.RelativePath), out SevenZipEntry? entry))
            {
                problems.Add($"Missing from archive: {file.RelativePath}");
                continue;
            }

            if (entry.Size != file.Size)
                problems.Add($"Size mismatch for {file.RelativePath}: {file.Size:N0} bytes before compression, {entry.Size:N0} bytes in archive");
        }

        // Whatever is left was never handed to 7-Zip.
        foreach (SevenZipEntry extra in inArchive.Values)
            problems.Add($"Unexpected file in archive: {extra.Path}");

        return problems.Count == 0 ? VerificationResult.Success : new VerificationResult(problems);
    }

    // 7-Zip lists paths with '\' on Windows; we may have built them with either separator.
    // Elsewhere '\' is a legal file-name character, so leave paths alone.
    private static string Normalize(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    private static StringComparer PathComparer => Paths.Comparer;
}
