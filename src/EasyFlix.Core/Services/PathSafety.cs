namespace EasyFlix.Core.Services;

public static class PathSafety
{
    public static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    public static bool IsSameOrDescendant(string path, string parent)
    {
        var candidate = Normalize(path);
        var ancestor = Normalize(parent);
        var descendantPrefix = ancestor.EndsWith(Path.DirectorySeparatorChar)
            ? ancestor
            : ancestor + Path.DirectorySeparatorChar;
        return candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcluded(string path, IEnumerable<string> excludedPaths)
        => excludedPaths.Any(excluded => IsSameOrDescendant(path, excluded));

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }
}
