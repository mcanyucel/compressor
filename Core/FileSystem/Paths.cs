namespace Core.FileSystem;

/// <summary>
/// Path comparison and normalization shared by the job builder, executor and verification.
/// </summary>
public static class Paths
{
    /// <summary>
    /// Windows and macOS file systems are case-insensitive by default; Linux is case-sensitive.
    /// </summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Full path without a trailing separator (roots such as <c>C:\</c> keep theirs).
    /// </summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// True if <paramref name="path"/> equals <paramref name="ancestor"/> or lies inside it.
    /// A plain StartsWith would wrongly match <c>C:\docs\abc</c> against <c>C:\docs\a</c>.
    /// </summary>
    public static bool IsSameOrUnder(string path, string ancestor)
    {
        if (!path.StartsWith(ancestor, Comparison))
            return false;

        if (path.Length == ancestor.Length || Path.EndsInDirectorySeparator(ancestor))
            return true;

        char next = path[ancestor.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }

    public static bool IsStrictlyUnder(string path, string ancestor) =>
        path.Length > ancestor.Length && IsSameOrUnder(path, ancestor);
}
