namespace Core.Models;

/// <summary>
/// A single marking decision made by the user in tree view (in UI).
/// Persisted as part of the job JSON file.
/// </summary>
public sealed class JobRule
{
    /// <summary>
    /// Path relative to <see cref="JobConfig.RootPath"/>.
    /// </summary>
    public required string RelativePath { get; init; }
    public required JobAction Action { get; init; }
}