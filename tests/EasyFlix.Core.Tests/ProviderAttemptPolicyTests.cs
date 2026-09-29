using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class ProviderAttemptPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("NotFound")]
    [InlineData("UnsafeCandidate")]
    [InlineData("notfound")]
    public void DefersRecentProviderSpecificTerminalAttempts(string status)
        => Assert.True(ProviderAttemptPolicy.ShouldDefer(
            status, "SubDL ┬╖ no exact SRT", "SubDL", Now.AddDays(-1), Now, TimeSpan.FromDays(7)));

    [Theory]
    [InlineData("Rejected", "SubDL ┬╖ temporary network error")]
    [InlineData("Installed", "SubDL ┬╖ release")]
    [InlineData("NotFound", "OpenSubtitles ┬╖ no exact safe match")]
    public void DoesNotDeferTransientSuccessfulOrDifferentProviderAttempts(string status, string detail)
        => Assert.False(ProviderAttemptPolicy.ShouldDefer(
            status, detail, "SubDL", Now.AddDays(-1), Now, TimeSpan.FromDays(7)));

    [Fact]
    public void ExpiredAttemptCanBeRetried()
        => Assert.False(ProviderAttemptPolicy.ShouldDefer(
            "NotFound", "SubDL ┬╖ no exact SRT", "SubDL", Now.AddDays(-8), Now, TimeSpan.FromDays(7)));

    [Fact]
    public void RejectsNegativeRetryWindow()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ProviderAttemptPolicy.ShouldDefer(
            "NotFound", "SubDL ┬╖ no exact SRT", "SubDL", Now, Now, TimeSpan.FromDays(-1)));
}
