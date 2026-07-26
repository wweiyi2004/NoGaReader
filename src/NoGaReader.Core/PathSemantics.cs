namespace NoGaReader.Services;

/// <summary>
/// Centralizes filesystem comparisons so Windows remains case-insensitive while
/// Android/Linux preserve distinct paths whose names differ only by case.
/// </summary>
internal static class PathSemantics
{
    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string ToKey(string normalizedPath) => OperatingSystem.IsWindows()
        ? normalizedPath.ToUpperInvariant()
        : normalizedPath;

    public static bool Equals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), Comparison);

    /// <summary>
    /// True only when <paramref name="candidate"/> is strictly inside
    /// <paramref name="root"/>; the root itself does not count as inside,
    /// matching the historical "StartsWith(root + separator)" checks.
    /// </summary>
    public static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, ".", StringComparison.Ordinal) &&
               !string.Equals(relative, "..", Comparison) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, Comparison) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, Comparison);
    }
}
