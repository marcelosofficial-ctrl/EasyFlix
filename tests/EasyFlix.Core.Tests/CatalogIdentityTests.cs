using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class CatalogIdentityTests
{
    [Fact]
    public void TitleId_IsStableAcrossCaseAndExtraSpaces()
    {
        var left = CatalogIdentity.TitleId(MediaKind.Movie, "Blade Runner 2049", "2017");
        var right = CatalogIdentity.TitleId(MediaKind.Movie, "  blade   runner 2049 ", "2017");

        Assert.Equal(left, right);
    }

    [Fact]
    public void TitleId_DistinguishesKinds()
    {
        var movie = CatalogIdentity.TitleId(MediaKind.Movie, "Monster", null);
        var anime = CatalogIdentity.TitleId(MediaKind.Anime, "Monster", null);

        Assert.NotEqual(movie, anime);
    }
}
