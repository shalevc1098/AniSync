using FluentAssertions;
using AniSync.Helpers;
using AniSync.Models;
using Xunit;

namespace AniSync.Tests;

public class RatingTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(10.0, 10)]
    [InlineData(7.5, 8)]   // banker's rounding: 7.5 -> 8 (nearest even)
    [InlineData(7.4, 7)]
    [InlineData(10.4, 10)]
    [InlineData(11.0, 10)] // clamp upper
    [InlineData(-1.0, 0)]  // clamp lower
    [InlineData(9.5, 10)]  // banker's rounding: 9.5 -> 10
    public void NormalizeRating_clamps_and_rounds(double input, int expected)
    {
        RatingHelpers.NormalizeRating(input).Should().Be(expected);
    }

    [Fact]
    public void SyncAction_parses_rated()
    {
        SyncActionHelper.ParseAction("rated").Should().Be(SyncAction.Rated);
        SyncActionHelper.ParseAction("Rated").Should().Be(SyncAction.Rated);
    }

    [Fact]
    public void SyncAction_text_for_rated_is_Rated()
    {
        SyncActionHelper.GetActionText(SyncAction.Rated).Should().Be("Rated");
    }
}
