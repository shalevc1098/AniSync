using FluentAssertions;
using AniSync.Api;
using AniSync.Configuration;
using AniSync.Helpers;
using AniSync.Models.Mal;
using Xunit;

namespace AniSync.Tests;

public class AniListMappingTests
{
    [Theory]
    [InlineData(Status.Watching, "CURRENT")]
    [InlineData(Status.Completed, "COMPLETED")]
    [InlineData(Status.Plan_to_watch, "PLANNING")]
    [InlineData(Status.On_hold, "PAUSED")]
    [InlineData(Status.Dropped, "DROPPED")]
    public void MapToAniListStatus_MapsCanonicalToAniList(Status canonical, string expected)
    {
        AniListApiCalls.MapToAniListStatus(canonical).Should().Be(expected);
    }

    [Theory]
    [InlineData("CURRENT", Status.Watching, false)]
    [InlineData("COMPLETED", Status.Completed, false)]
    [InlineData("PLANNING", Status.Plan_to_watch, false)]
    [InlineData("PAUSED", Status.On_hold, false)]
    [InlineData("DROPPED", Status.Dropped, false)]
    [InlineData("REPEATING", Status.Completed, true)]
    public void MapFromAniListStatus_MapsAniListToCanonical(string aniList, Status expectedStatus, bool expectedRewatching)
    {
        var (status, isRewatching) = AniListApiCalls.MapFromAniListStatus(aniList);
        status.Should().Be(expectedStatus);
        isRewatching.Should().Be(expectedRewatching);
    }

    [Fact]
    public void Repeating_RoundTrips_AsRewatch()
    {
        // A rewatch (Completed + isRewatching) maps to REPEATING on write,
        // and REPEATING maps back to (Completed + isRewatching) on read.
        var (status, isRewatching) = AniListApiCalls.MapFromAniListStatus("REPEATING");
        status.Should().Be(Status.Completed);
        isRewatching.Should().BeTrue();
    }

    [Theory]
    [InlineData("AniList", ApiName.AniList)]
    [InlineData("anilist", ApiName.AniList)]
    [InlineData("Mal", ApiName.Mal)]
    [InlineData("mal", ApiName.Mal)]
    public void ProviderApiFactory_ParsesKnownProviders(string name, ApiName expected)
    {
        ProviderApiFactory.TryParseProvider(name, out var provider).Should().BeTrue();
        provider.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    public void ProviderApiFactory_RejectsUnknownProviders(string name)
    {
        ProviderApiFactory.TryParseProvider(name, out _).Should().BeFalse();
    }
}
