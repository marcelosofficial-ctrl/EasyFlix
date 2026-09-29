namespace EasyFlix.Core.Models;

public sealed record ScanOptions(
    IReadOnlyCollection<string> SourceRoots,
    IReadOnlyCollection<string> ExcludedPaths,
    string LibraryPath);
