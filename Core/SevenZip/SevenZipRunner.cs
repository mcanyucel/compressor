using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Core.SevenZip;

/// <summary>
/// Runs the 7-Zip command line. One instance can be shared across threads; every call starts its own process.
///
/// Cancelling a call kills its 7-Zip process and throws <see cref="OperationCanceledException"/>.
/// Deleting a partial archive is the caller's job.
/// </summary>
public sealed partial class SevenZipRunner(SevenZipExecutable executable)
{
    public SevenZipExecutable Executable { get; } = executable;

    // Switches shared by all commands:
    //   -sccUTF-8  console output in UTF-8 (matches StandardOutputEncoding below)
    //   -bse2      errors and warnings to stderr
    //   -y         assume "yes" on any prompt; stdin is closed as well, so 7-Zip can never wait for input
    private static readonly string[] CommonSwitches = ["-sccUTF-8", "-bse2", "-y"];

    // ── Add ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new 7z archive from <see cref="SevenZipAddRequest.Files"/>.
    /// </summary>
    /// <param name="progress">Receives 0-100 as 7-Zip reports progress. Called from a background thread.</param>
    public async Task<SevenZipResult> AddAsync(SevenZipAddRequest request, IProgress<int>? progress, CancellationToken ct)
    {
        Validate(request);

        // The file list goes through a list file: no command-line length limit, and names are taken literally (-spd).
        string listFile = Path.Combine(Path.GetTempPath(), $"compressor-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(listFile, request.Files, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);

        try
        {
            List<string> args =
            [
                "a",
                "-t7z", // explicit: the archive name ends in .tmp
                $"-mx={request.CompressionLevel}",
                $"-mmt={request.Threads}",
                "-bsp1", // progress to stdout
                "-bso0", // no other stdout messages
                "-scsUTF-8", // list file encoding
                "-spd", // no wildcard matching in file names
                .. CommonSwitches
            ];
            if (request.StopOnUnreadableFile)
                args.Add("-sse");

            // No "--" before these: it would stop 7-Zip from treating @listfile as a list file.
            // Both paths are absolute, so they can't be mistaken for switches.
            args.Add(request.ArchivePath);
            args.Add("@" + listFile);

            var parser = new ProgressParser();
            int lastReported = -1;

            var output = await RunAsync(args, request.WorkingDirectory, collectStdout: false, chunk =>
            {
                if (progress is null) return;
                int? percent = parser.Feed(chunk);
                if (percent is { } p && p != lastReported)
                {
                    lastReported = p;
                    progress.Report(p);
                }
            }, ct);

            return output.Result;
        }
        finally
        {
            try { File.Delete(listFile); }
            catch { /* temp file; leaving it behind is harmless */ }
        }
    }

    // ── Test ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-reads the archive and checks every CRC (<c>7z t</c>). Succeeds only with exit code 0.
    /// </summary>
    public async Task<SevenZipResult> TestAsync(string archivePath, CancellationToken ct)
    {
        string[] args = ["t", "-bso0", "-bsp0", .. CommonSwitches, Path.GetFullPath(archivePath)];
        var output = await RunAsync(args, workingDirectory: null, collectStdout: false, onStdout: null, ct);
        return output.Result;
    }

    // ── List ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the archive's entries (<c>7z l -slt</c>). Entries are empty when the command fails.
    /// </summary>
    public async Task<SevenZipListResult> ListAsync(string archivePath, CancellationToken ct)
    {
        string[] args = ["l", "-slt", .. CommonSwitches, Path.GetFullPath(archivePath)];
        var output = await RunAsync(args, workingDirectory: null, collectStdout: true, onStdout: null, ct);

        IReadOnlyList<SevenZipEntry> entries = output.Result.Status is SevenZipExitCode.Success or SevenZipExitCode.Warning
            ? ParseTechnicalListing(output.Stdout)
            : [];

        return new SevenZipListResult(output.Result, entries);
    }

    /// <summary>
    /// Parses <c>-slt</c> output: archive properties, a <c>----------</c> line, then one
    /// <c>Key = Value</c> block per entry separated by blank lines.
    /// </summary>
    internal static IReadOnlyList<SevenZipEntry> ParseTechnicalListing(string stdout)
    {
        var entries = new List<SevenZipEntry>();
        Dictionary<string, string>? current = null;
        bool inEntries = false;

        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (!inEntries)
            {
                inEntries = line == "----------";
                continue;
            }

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            int eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;

            string key = line[..eq];
            string value = line[(eq + 3)..];

            if (key == "Path") Flush(); // a new entry always starts with Path
            current ??= new Dictionary<string, string>(StringComparer.Ordinal);
            current[key] = value;
        }

        Flush();
        return entries;

        void Flush()
        {
            if (current is null || !current.TryGetValue("Path", out string? path)) { current = null; return; }

            bool isDirectory =
                (current.TryGetValue("Folder", out string? folder) && folder == "+") ||
                (current.TryGetValue("Attributes", out string? attributes) && attributes.StartsWith('D'));

            long size = current.TryGetValue("Size", out string? sizeText) && long.TryParse(sizeText, out long s) ? s : 0;
            string? crc = current.TryGetValue("CRC", out string? c) && c.Length > 0 ? c : null;

            entries.Add(new SevenZipEntry(path, isDirectory, size, crc));
            current = null;
        }
    }

    // ── Process Plumbing ─────────────────────────────────────────────────────────────

    private sealed record ProcessOutput(SevenZipResult Result, string Stdout);

    private async Task<ProcessOutput> RunAsync(
        IReadOnlyList<string> args,
        string? workingDirectory,
        bool collectStdout,
        Action<ReadOnlyMemory<char>>? onStdout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(Executable.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? ""
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start 7-Zip: {Executable.Path}");

        process.StandardInput.Close();

        var stdout = new StringBuilder();
        Task stdoutTask = PumpAsync(process.StandardOutput, collectStdout ? stdout : null, onStdout);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        await using (ct.Register(() => KillQuietly(process)))
        {
            // Not cancellable on purpose: after a kill, wait for the process and its pipes to close
            // so no file handles are left open on the archive.
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);
        }

        ct.ThrowIfCancellationRequested();

        return new ProcessOutput(new SevenZipResult(process.ExitCode, stderrTask.Result.Trim()), stdout.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder? sink, Action<ReadOnlyMemory<char>>? onChunk)
    {
        // Progress is redrawn with '\r' and never ends with a newline, so read raw chunks instead of lines.
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            sink?.Append(buffer, 0, read);
            onChunk?.Invoke(buffer.AsMemory(0, read));
        }
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
        catch (System.ComponentModel.Win32Exception) { /* exiting; nothing more to do */ }
    }

    private static void Validate(SevenZipAddRequest request)
    {
        if (!Path.IsPathFullyQualified(request.ArchivePath))
            throw new ArgumentException($"Archive path must be absolute: {request.ArchivePath}", nameof(request));
        if (File.Exists(request.ArchivePath))
            throw new IOException($"Archive already exists and would be updated instead of created: {request.ArchivePath}");
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new ArgumentException($"Working directory must be an existing absolute path: {request.WorkingDirectory}", nameof(request));
        if (request.Files.Count == 0)
            throw new ArgumentException("No files to add.", nameof(request));
        if (request.CompressionLevel is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(request), request.CompressionLevel, "Compression level must be 0-9.");
        if (request.Threads < 1)
            throw new ArgumentOutOfRangeException(nameof(request), request.Threads, "Threads must be at least 1.");

        foreach (string file in request.Files)
        {
            if (Path.IsPathRooted(file))
                throw new ArgumentException($"File paths must be relative to the working directory: {file}", nameof(request));
            if (file.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException($"File name contains a line break and can't go in a list file: {file}", nameof(request));
        }
    }

    // ── Progress Parsing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the latest percentage from <c>-bsp1</c> output such as <c>" 43% + big.bin\r"</c>.
    /// Keeps a short tail between chunks so a number split across two reads is still found.
    /// </summary>
    internal sealed partial class ProgressParser
    {
        private string _tail = "";

        public int? Feed(ReadOnlyMemory<char> chunk)
        {
            string text = _tail + chunk.Span.ToString();
            _tail = text.Length > 8 ? text[^8..] : text;

            int? last = null;
            foreach (Match m in PercentRegex().Matches(text))
            {
                if (int.TryParse(m.Groups[1].ValueSpan, out int value) && value <= 100)
                    last = value;
            }
            return last;
        }

        [GeneratedRegex(@"(?<!\d)(\d{1,3})%")]
        private static partial Regex PercentRegex();
    }
}
