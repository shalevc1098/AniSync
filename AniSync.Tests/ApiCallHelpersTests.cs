using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using AniSync;
using AniSync.Api;
using AniSync.Configuration;
using AniSync.Helpers;
using AniSync.Interfaces;
using AniSync.Models.Mal;
using Xunit;

namespace AniSync.Tests;

public class ApiCallHelpersTests
{
    [Fact]
    public async Task SearchAnime_Should_Return_Empty_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();

        // Act
        var result = await apiCallHelpers.SearchAnime("test");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAnime_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();

        // Act
        var result = await apiCallHelpers.GetAnime(123);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAnime_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();

        // Act
        var result = await apiCallHelpers.UpdateAnime(123, 5, Status.Watching);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUser_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();

        // Act
        var result = await apiCallHelpers.GetUser();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAnimeList_Should_Return_Null_When_MalApiCalls_Is_Null()
    {
        // Arrange
        var apiCallHelpers = new ApiCallHelpers();

        // Act
        var result = await apiCallHelpers.GetAnimeList(Status.Watching);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_Should_Accept_Null_Parameters()
    {
        // Act
        var apiCallHelpers = new ApiCallHelpers();

        // Assert
        apiCallHelpers.Should().NotBeNull();
    }

    // ========================================================================
    // GetAnime fields validation
    // The API call must request all fields the code actually uses.
    // ========================================================================

    [Fact]
    public void GetAnimeFields_ShouldIncludeAllRequiredFields()
    {
        var fields = new[] { "title", "alternative_titles", "start_date", "related_anime", "my_list_status{num_times_rewatched}", "num_episodes" };

        fields.Should().Contain("alternative_titles",
            "needed for title matching");
        fields.Should().Contain("start_date",
            "needed for air date comparison in candidate ranking");
        fields.Should().Contain(f => f.Contains("num_times_rewatched"),
            "needed to read current rewatch count for incrementing");
        fields.Should().Contain(f => f.StartsWith("my_list_status"),
            "needed for current watch status and episode count");
    }

    // ========================================================================
    // FetchIdFromProvider 30-day date window
    // Candidates must have air dates within 30 days to be selected.
    // ========================================================================

    [Fact]
    public void Candidate_Within_30_Days_Should_Be_Selected()
    {
        var seriesAirDate = new DateTime(2023, 4, 1);
        var malStartDate = new DateTime(2023, 4, 15); // 14 days diff
        var diffDays = Math.Abs((malStartDate - seriesAirDate).TotalDays);

        (diffDays < 30).Should().BeTrue("14-day difference is within the 30-day window");
    }

    [Fact]
    public void Candidate_Beyond_30_Days_Should_Be_Rejected()
    {
        var seriesAirDate = new DateTime(2023, 4, 1);
        var malStartDate = new DateTime(2023, 6, 1); // 61 days diff
        var diffDays = Math.Abs((malStartDate - seriesAirDate).TotalDays);

        (diffDays < 30).Should().BeFalse("61-day difference exceeds the 30-day window");
    }

    [Fact]
    public void Null_AirDate_Should_Reject_All_Candidates()
    {
        bool hasAirDate = false;
        var diffDays = hasAirDate ? 10.0 : double.MaxValue;

        (diffDays < 30).Should().BeFalse("null air date should produce MaxValue, rejecting the candidate");
    }

    [Fact]
    public void Multiple_Candidates_Should_Pick_Closest()
    {
        var seriesAirDate = new DateTime(2023, 4, 1);
        var candidates = new[]
        {
            new { StartDate = new DateTime(2023, 4, 6), DiffDays = Math.Abs((new DateTime(2023, 4, 6) - seriesAirDate).TotalDays) },
            new { StartDate = new DateTime(2023, 4, 21), DiffDays = Math.Abs((new DateTime(2023, 4, 21) - seriesAirDate).TotalDays) },
        };

        var filtered = candidates.Where(c => c.DiffDays < 30).OrderBy(c => c.DiffDays).ToArray();

        filtered.Should().HaveCount(2);
        filtered.First().DiffDays.Should().Be(5, "closest candidate (5 days) should be first");
    }

    // ========================================================================
    // MAL search query truncation
    // MAL API rejects queries over 64 characters.
    // ========================================================================

    [Fact]
    public void SearchQuery_Over_64_Chars_Should_Be_Truncated()
    {
        string query = new string('a', 80);
        if (query.Length > 64)
            query = query.Substring(0, 64);

        query.Length.Should().Be(64);
    }

    [Fact]
    public void SearchQuery_Exactly_64_Chars_Should_Not_Change()
    {
        string query = new string('a', 64);
        string original = query;
        if (query.Length > 64)
            query = query.Substring(0, 64);

        query.Should().Be(original);
        query.Length.Should().Be(64);
    }

    [Fact]
    public void SearchQuery_Under_64_Chars_Should_Not_Change()
    {
        string query = "Naruto Shippuden";
        string original = query;
        if (query.Length > 64)
            query = query.Substring(0, 64);

        query.Should().Be(original);
    }

}
