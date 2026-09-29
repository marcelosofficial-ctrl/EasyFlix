using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class OpenSubtitlesMovieHashTests
{
    [Fact]
    public void ComputesKnownHashForMinimumSizedZeroFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[128 * 1024]);
            Assert.Equal("0000000000020000", OpenSubtitlesMovieHash.Compute(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsFilesTooSmallForTheHashAlgorithm()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[127 * 1024]);
            Assert.Throws<InvalidDataException>(() => OpenSubtitlesMovieHash.Compute(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
