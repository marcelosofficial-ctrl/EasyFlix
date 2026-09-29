using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MetadataMatchPolicyTests
{
    [Theory]
    [InlineData("To Love-Ru", "To Love Ru")]
    [InlineData("Dr. Stone", "Dr Stone")]
    [InlineData("Attack on Titan", "Attack on Titan")]
    [InlineData("Nausicaa of the Valley of the Wind", "Nausica\u00E4 of the Valley of the Wind")]
    [InlineData("Amelie", "Am\u00E9lie")]
    public void EquivalentTitles_AreAccepted(string wanted, string candidate)
        => Assert.True(MetadataMatchPolicy.IsAcceptable(wanted, candidate));

    [Theory]
    [InlineData("To Love-Ru", "To Love")]
    [InlineData("To Love-Ru", "Love O2O")]
    [InlineData("Dr. Stone", "Stone House")]
    public void WeakOrPartialShortTitleMatches_AreRejected(string wanted, string candidate)
        => Assert.False(MetadataMatchPolicy.IsAcceptable(wanted, candidate));

    [Fact]
    public void ExactTitleWithMatchingYear_IsHighConfidence()
        => Assert.Equal(
            1,
            MetadataMatchPolicy.Score("Kingdom", "Kingdom", "2019", "2019"),
            3);
}
