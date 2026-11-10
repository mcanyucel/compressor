using System.Text;
using Cli;

// compressor scan <folder> [--depth N] [--write <job.json>]
// compressor plan <job.json>
// compressor run  <job.json> [--yes]
// Global: --7zip <path>

Console.OutputEncoding = Encoding.UTF8;

try
{
    var parsed = Arguments.Parse(args);
    if (parsed is null || parsed.Command is "help" or "--help" or "-h")
    {
        Console.WriteLine(Arguments.Usage);
        return parsed is null ? ExitCodes.Usage : ExitCodes.Ok;
    }

    return parsed.Command switch
    {
        "version" or "--version" => PrintVersion(),
        "scan" => ScanCommand.Run(parsed),
        "plan" => await RunCommand.RunAsync(parsed, execute: false),
        "run" => await RunCommand.RunAsync(parsed, execute: true),
        _ => Usage($"Unknown command: {parsed.Command}")
    };
}
catch (UsageException ex)
{
    return Usage(ex.Message);
}

// The release version from Directory.Build.props, e.g. "0.1.0".
static int PrintVersion()
{
    Console.WriteLine(typeof(Arguments).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "unknown");
    return ExitCodes.Ok;
}

static int Usage(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Arguments.Usage);
    return ExitCodes.Usage;
}
