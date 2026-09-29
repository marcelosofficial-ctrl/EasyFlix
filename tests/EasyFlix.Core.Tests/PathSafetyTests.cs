using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class PathSafetyTests
{
    [Fact]
    public void IsSameOrDescendant_DoesNotConfuseSiblingPrefixes()
    {
        var root = Path.Combine(Path.GetTempPath(), "movies");
        Assert.True(PathSafety.IsSameOrDescendant(Path.Combine(root, "Anime", "Episode.mkv"), root));
        Assert.True(PathSafety.IsSameOrDescendant(root, root + Path.DirectorySeparatorChar));
        Assert.False(PathSafety.IsSameOrDescendant(root + "-backup", root));
    }

    [Fact]
    public void IsExcluded_HandlesNestedProtectedFoldersCaseInsensitively()
    {
        var blocked = Path.Combine(Path.GetTempPath(), "Never Scan");
        Assert.True(PathSafety.IsExcluded(Path.Combine(blocked.ToUpperInvariant(), "Nested", "Movie.mkv"), [blocked]));
    }

    [Fact]
    public void IsSameOrDescendant_HandlesDriveRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(PathSafety.IsSameOrDescendant(Path.Combine(root, "Movies", "Movie.mkv"), root));
    }
}
