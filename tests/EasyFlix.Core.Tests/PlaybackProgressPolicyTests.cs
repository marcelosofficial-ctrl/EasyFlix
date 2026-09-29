using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class PlaybackProgressPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(59.9)]
    public void EarlyPlayback_IsNotContinueWatching(double position)
        => Assert.False(PlaybackProgressPolicy.IsContinueWatching(position, 3600));

    [Fact]
    public void InProgressPlayback_IsContinueWatching()
        => Assert.True(PlaybackProgressPolicy.IsContinueWatching(900, 3600));

    [Fact]
    public void UnknownDuration_AfterMinimum_IsContinueWatching()
        => Assert.True(PlaybackProgressPolicy.IsContinueWatching(180, null));

    [Theory]
    [InlineData(3420, 3600)]
    [InlineData(3500, 3600)]
    public void NearCompletion_IsNotContinueWatching(double position, double duration)
        => Assert.False(PlaybackProgressPolicy.IsContinueWatching(position, duration));

    [Theory]
    [InlineData(3600, 3600, true)]
    [InlineData(3420, 3600, true)]
    [InlineData(3300, 3600, false)]
    public void CompletionPolicy_UsesKnownDuration(double position, double duration, bool expected)
        => Assert.Equal(expected, PlaybackProgressPolicy.IsCompleted(position, duration));

    [Fact]
    public void UnknownDuration_IsNotCompleted()
        => Assert.False(PlaybackProgressPolicy.IsCompleted(3600, null));

    [Fact]
    public void NewestProgressForTitle_WinsEvenWhenCompleted()
    {
        var title = "title-1";
        var now = DateTimeOffset.UtcNow;
        var selected = PlaybackProgressPolicy.SelectContinueWatching(
            new[]
            {
                new PlaybackProgressEntry(title, "newest", 3550, 3600, now),
                new PlaybackProgressEntry(title, "older", 900, 3600, now.AddMinutes(-10))
            },
            20);

        Assert.Empty(selected);
    }

    [Fact]
    public void Selection_PreservesRecencyAndLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var selected = PlaybackProgressPolicy.SelectContinueWatching(
            new[]
            {
                new PlaybackProgressEntry("b", "b-file", 600, 3600, now),
                new PlaybackProgressEntry("a", "a-file", 300, 3600, now.AddMinutes(-1)),
                new PlaybackProgressEntry("c", "c-file", 120, null, now.AddMinutes(-2))
            },
            2);

        Assert.Equal(new[] { "b", "a" }, selected.Select(value => value.TitleId));
    }
}
