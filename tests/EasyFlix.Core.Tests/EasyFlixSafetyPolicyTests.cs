using EasyFlix.Core;

namespace EasyFlix.Core.Tests;

public sealed class EasyFlixSafetyPolicyTests
{
    [Fact]
    public void Configured_protected_paths_are_hard_excluded_and_merged()
    {
        var previous = EasyFlixSafetyPolicy.ProtectedPaths.ToArray();
        try
        {
            EasyFlixSafetyPolicy.ConfigureProtectedPaths([@"X:\Protected Media", @"X:\Private Media"]);

            Assert.True(EasyFlixSafetyPolicy.IsHardExcluded(@"X:\Protected Media"));
            Assert.True(EasyFlixSafetyPolicy.IsHardExcluded(@"X:\Protected Media\private-video.mp4"));
            Assert.True(EasyFlixSafetyPolicy.IsHardExcluded(@"x:\PRIVATE MEDIA\clip.mkv"));

            var merged = EasyFlixSafetyPolicy.MergeExclusions([@"D:\Skip"]).ToArray();
            Assert.Contains(merged, value => value.Equals(@"D:\Skip", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(merged, value => value.Equals(@"X:\Protected Media", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(merged, value => value.Equals(@"X:\Private Media", StringComparison.OrdinalIgnoreCase));

            Assert.False(EasyFlixSafetyPolicy.IsHardExcluded(@"X:\Movies\Example Movie\movie.mkv"));
        }
        finally
        {
            EasyFlixSafetyPolicy.ConfigureProtectedPaths(previous);
        }
    }
}
