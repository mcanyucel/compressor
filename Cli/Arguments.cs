namespace Cli;

public static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Finished, but some items need attention, failed or were cancelled.</summary>
    public const int CompletedWithErrors = 1;
    /// <summary>Bad arguments, unreadable job file, 7-Zip not found.</summary>
    public const int Usage = 2;
    /// <summary>Stopped with Ctrl+C, or the user declined to start.</summary>
    public const int Cancelled = 3;
}

public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Minimal argument parsing: a command, positional arguments, and <c>--name value</c> / <c>--flag</c> options.
/// </summary>
public sealed class Arguments
{
    public const string Usage = """
        Usage:
          compressor scan <folder> [--depth N] [--write <job.json>]
              Show size and compressibility per subfolder (default depth 1).
              --write creates a starter job file that compresses the top-level folders
              rated Good or Average. Review it before running.

          compressor plan <job.json>
              Build the job and run pre-flight checks. Changes nothing on disk.

          compressor run <job.json> [--yes]
              Build, check, confirm, then compress and verify. --yes skips the confirmation.

        Options:
          --7zip <path>   7-Zip executable to use (default: bundled copy, else installed 7-Zip)

        Press Ctrl+C during a run to stop it; unfinished archives are removed.
        """;

    private static readonly HashSet<string> Flags = ["--yes"];
    private static readonly HashSet<string> ValueOptions = ["--depth", "--write", "--7zip"];

    public required string Command { get; init; }
    public List<string> Positional { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Arguments? Parse(string[] args)
    {
        if (args.Length == 0) return null;

        var parsed = new Arguments { Command = args[0].ToLowerInvariant() };
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (Flags.Contains(arg))
                parsed.Options[arg] = null;
            else if (ValueOptions.Contains(arg))
            {
                if (i + 1 >= args.Length) throw new UsageException($"{arg} needs a value.");
                parsed.Options[arg] = args[++i];
            }
            else if (arg.StartsWith("--"))
                throw new UsageException($"Unknown option: {arg}");
            else
                parsed.Positional.Add(arg);
        }

        return parsed;
    }

    public string RequirePositional(string name) =>
        Positional.Count == 1 ? Positional[0] : throw new UsageException($"Expected exactly one {name}.");

    public bool Has(string option) => Options.ContainsKey(option);

    public string? Get(string option) => Options.GetValueOrDefault(option);
}
