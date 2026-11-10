using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Core.SevenZip;

public sealed class SevenZipLocatorOptions
{
    /// <summary>
    /// Path set by the user. When set, it is the only candidate: a missing file is an error, not a reason to fall back.
    /// </summary>
    public string? ConfiguredPath { get; init; }

    /// <summary>
    /// Fall back to a 7-Zip installed on the machine when no bundled binary is present.
    /// </summary>
    public bool AllowInstalled { get; init; }

    /// <summary>
    /// Folder holding <c>SHA256SUMS</c> and one subfolder per platform (<c>win-x64/7za.exe</c>, <c>linux-x64/7zz</c>, ...).
    /// </summary>
    public string BundleDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "7zip");
}

/// <summary>
/// Finds the 7-Zip executable to use. Order: configured path → bundled binary (hash-checked) → installed copy (opt-in).
/// </summary>
public static class SevenZipLocator
{
    public const string ChecksumFileName = "SHA256SUMS";

    public static bool TryLocate(
        SevenZipLocatorOptions options,
        [NotNullWhen(true)] out SevenZipExecutable? executable,
        [NotNullWhen(false)] out string? error)
    {
        executable = null;

        // ── Configured ───────────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(options.ConfiguredPath))
        {
            if (File.Exists(options.ConfiguredPath))
            {
                executable = new SevenZipExecutable(Path.GetFullPath(options.ConfiguredPath), SevenZipSource.Configured);
                error = null;
                return true;
            }

            error = $"The configured 7-Zip path does not exist: {options.ConfiguredPath}";
            return false;
        }

        // ── Bundled ──────────────────────────────────────────────────────────────────
        string? relativeBinary = GetBundledBinaryRelativePath();
        if (relativeBinary is not null)
        {
            string bundled = ToFullBundlePath(options.BundleDirectory, relativeBinary);
            if (File.Exists(bundled))
            {
                // A bundled binary that fails the check is a red flag; never fall back silently.
                if (!VerifyBundledHash(options.BundleDirectory, relativeBinary, out error))
                    return false;

                if (!OperatingSystem.IsWindows() && !EnsureExecutable(bundled, out error))
                    return false;

                executable = new SevenZipExecutable(bundled, SevenZipSource.Bundled);
                return true;
            }
        }

        // ── Installed ────────────────────────────────────────────────────────────────
        if (options.AllowInstalled && FindInstalled() is { } installed)
        {
            executable = new SevenZipExecutable(installed, SevenZipSource.Installed);
            error = null;
            return true;
        }

        error = relativeBinary is null
            ? $"No bundled 7-Zip for this platform ({RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture})."
            : $"7-Zip not found. Expected the bundled binary at {ToFullBundlePath(options.BundleDirectory, relativeBinary)}.";
        return false;
    }

    /// <summary>
    /// Relative path of the bundled binary for the current platform, using forward slashes as in <c>SHA256SUMS</c>.
    /// Null when the platform isn't supported.
    /// </summary>
    public static string? GetBundledBinaryRelativePath()
    {
        if (OperatingSystem.IsMacOS())
            return "osx/7zz"; // 7-Zip ships one universal binary for macOS

        string? arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => null
        };
        if (arch is null) return null;

        if (OperatingSystem.IsWindows()) return $"win-{arch}/7za.exe";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}/7zz";
        return null;
    }

    /// <summary>
    /// Checks the binary against its line in <c>SHA256SUMS</c> (standard <c>sha256sum</c> format: <c>&lt;hex&gt;  &lt;path&gt;</c>).
    /// </summary>
    public static bool VerifyBundledHash(string bundleDirectory, string relativeBinary, [NotNullWhen(false)] out string? error)
    {
        string sumsPath = Path.Combine(bundleDirectory, ChecksumFileName);
        if (!File.Exists(sumsPath))
        {
            error = $"Checksum file missing: {sumsPath}";
            return false;
        }

        string? expected = null;
        foreach (string line in File.ReadLines(sumsPath))
        {
            string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            string path = parts[1].TrimStart('*').Replace('\\', '/'); // '*' marks binary mode in sha256sum output
            if (string.Equals(path, relativeBinary, StringComparison.OrdinalIgnoreCase))
            {
                expected = parts[0];
                break;
            }
        }

        if (expected is null)
        {
            error = $"No checksum for {relativeBinary} in {sumsPath}";
            return false;
        }

        string binaryPath = ToFullBundlePath(bundleDirectory, relativeBinary);
        string actual;
        using (var stream = File.OpenRead(binaryPath))
            actual = Convert.ToHexString(SHA256.HashData(stream));

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Checksum mismatch for {binaryPath}: expected {expected}, got {actual}. The file may have been modified.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// A build or publish made on Windows doesn't carry the Unix execute bit, so restore it (the hash was just checked).
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool EnsureExecutable(string path, [NotNullWhen(false)] out string? error)
    {
        error = null;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The bundled 7-Zip is not executable and its permissions can't be changed: {path} ({ex.Message})";
            return false;
        }
    }

    private static string ToFullBundlePath(string bundleDirectory, string relativeBinary) =>
        Path.Combine(bundleDirectory, relativeBinary.Replace('/', Path.DirectorySeparatorChar));

    private static string? FindInstalled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
            foreach (string valueName in new[] { "Path64", "Path" })
            {
                if (key?.GetValue(valueName) is string dir && File.Exists(Path.Combine(dir, "7z.exe")))
                    return Path.Combine(dir, "7z.exe");
            }

            string programFiles = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
            return File.Exists(programFiles) ? programFiles : null;
        }

        // Linux / macOS: official build is 7zz; p7zip and distro packages provide 7z.
        string[] pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string name in new[] { "7zz", "7z" })
        foreach (string dir in pathDirs)
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
