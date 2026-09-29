using EasyFlix.Core.Services;

namespace EasyFlix.Core;

public static class EasyFlixSafetyPolicy
{
    private static string[] _protectedPaths = [];

    public static IReadOnlyList<string> ProtectedPaths
        => System.Threading.Volatile.Read(ref _protectedPaths);

    public static void ConfigureProtectedPaths(IEnumerable<string> protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(protectedPaths);

        var configured = protectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        System.Threading.Volatile.Write(ref _protectedPaths, configured);
    }

    public static IEnumerable<string> MergeExclusions(IEnumerable<string> configuredExclusions)
        => configuredExclusions
            .Concat(ProtectedPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public static bool IsHardExcluded(string path)
        => ProtectedPaths.Any(protectedPath => PathSafety.IsSameOrDescendant(path, protectedPath));
}
