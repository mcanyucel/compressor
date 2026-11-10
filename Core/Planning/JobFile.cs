using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Models;

namespace Core.Planning;

/// <summary>
/// Loads and saves <see cref="JobConfig"/> as JSON. The job file is the unit of persistence.
/// Property names are camelCase and enums are strings; comments and trailing commas are allowed so the file can be
/// edited by hand.
/// </summary>
public static class JobFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <exception cref="InvalidDataException">The file is not a valid job file.</exception>
    public static JobConfig Load(string path)
    {
        JobConfig? config;
        try
        {
            using FileStream stream = File.OpenRead(path);
            config = JsonSerializer.Deserialize<JobConfig>(stream, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid job file {path}: {ex.Message}", ex);
        }

        if (config is null)
            throw new InvalidDataException($"Job file is empty: {path}");

        Validate(config, path);
        return config;
    }

    public static void Save(JobConfig config, string path)
    {
        using FileStream stream = File.Create(path);
        JsonSerializer.Serialize(stream, config, Options);
    }

    private static void Validate(JobConfig config, string path)
    {
        if (!Path.IsPathFullyQualified(config.RootPath))
            throw new InvalidDataException($"{path}: rootPath must be an absolute path, got \"{config.RootPath}\".");
        if (config.CompressionLevel is < 0 or > 9)
            throw new InvalidDataException($"{path}: compressionLevel must be 0-9, got {config.CompressionLevel}.");
    }
}
