using Core.Models;

namespace Cli;

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    public static string Hint(CompressibilityHint hint) => hint switch
    {
        CompressibilityHint.Good => "good",
        CompressibilityHint.Average => "average",
        CompressibilityHint.Poor => "poor",
        CompressibilityHint.Empty => "empty",
        _ => "?"
    };

    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s"
        : $"{span.TotalSeconds:0.0}s";

    /// <summary>Path relative to <paramref name="root"/>, or "." for the root itself.</summary>
    public static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "." ? Path.GetFileName(path) is { Length: > 0 } name ? name : path : relative;
    }

    /// <summary>First line only, for one-line summaries of multi-line 7-Zip messages.</summary>
    public static string FirstLine(string? text) =>
        text is null ? "" : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
