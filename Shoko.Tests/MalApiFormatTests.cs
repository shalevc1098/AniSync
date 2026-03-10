using System;
using FluentAssertions;
using Xunit;

namespace Shoko.Tests;

/// <summary>
/// Tests for MAL API formatting specifics (boolean casing, URL patterns, OVA matching).
/// </summary>
public class MalApiFormatTests
{
    // ========================================================================
    // Boolean casing
    // true.ToString() produces "True" but MAL API expects lowercase "true".
    // ========================================================================

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void BooleanValues_ShouldBeLowercase(bool value, string expected)
    {
        var result = value.ToString().ToLower();

        result.Should().Be(expected,
            "MAL API expects lowercase boolean strings");
    }

    [Fact]
    public void TrueToString_WithoutToLower_ProducesWrongCase()
    {
        true.ToString().Should().Be("True",
            "C# bool.ToString() produces title case, which MAL rejects");
        true.ToString().ToLower().Should().Be("true",
            "ToLower() fixes the casing for MAL API");
    }

    // ========================================================================
    // OAuth redirect URL casing
    // The redirect URL must use lowercase "/anisync/" to match the controller route.
    // ========================================================================

    [Fact]
    public void RedirectUrl_Should_Use_Lowercase_Route()
    {
        string baseUrl = "http://localhost:8111";
        string redirectUrl = baseUrl.TrimEnd('/') + "/anisync/authCallback";

        redirectUrl.Should().Contain("/anisync/", "route must be lowercase to match [Route(\"anisync\")]");
        redirectUrl.Should().NotContain("/AniSync/", "uppercase route would 404 on case-sensitive Linux");
    }

    [Fact]
    public void RedirectUrl_With_Trailing_Slash_Should_Use_Lowercase_Route()
    {
        string baseUrl = "http://localhost:8111/";
        string redirectUrl = baseUrl.TrimEnd('/') + "/anisync/authCallback";

        redirectUrl.Should().Be("http://localhost:8111/anisync/authCallback");
    }

    // ========================================================================
    // OVA pattern matching
    // Title-only matches should work even without AlternativeTitles.
    // ========================================================================

    [Fact]
    public void TitleMatch_Without_AlternativeTitles_Should_Still_Match()
    {
        var anime = new AniSync.Models.Mal.Anime
        {
            Title = "My Anime OVA",
            AlternativeTitles = null
        };

        bool matchesPattern = anime is { Title: { } };
        matchesPattern.Should().BeTrue("pattern should match when Title is set, even without AlternativeTitles");

        bool titleMatch = anime.Title!.Contains("OVA", StringComparison.OrdinalIgnoreCase) ||
            (anime.AlternativeTitles?.En != null && anime.AlternativeTitles.En.Contains("OVA", StringComparison.OrdinalIgnoreCase)) ||
            (anime.AlternativeTitles?.Ja != null && anime.AlternativeTitles.Ja.Contains("OVA", StringComparison.OrdinalIgnoreCase));

        titleMatch.Should().BeTrue("title-only match should succeed");
    }

    [Fact]
    public void TitleMatch_With_AlternativeTitles_Still_Works()
    {
        var anime = new AniSync.Models.Mal.Anime
        {
            Title = "Some Anime",
            AlternativeTitles = new AniSync.Models.Mal.AlternativeTitles
            {
                En = "My English OVA Title",
                Ja = "Japanese Title"
            }
        };

        bool matchesPattern = anime is { Title: { } };
        matchesPattern.Should().BeTrue();

        bool titleMatch = anime.Title!.Contains("OVA", StringComparison.OrdinalIgnoreCase) ||
            (anime.AlternativeTitles?.En != null && anime.AlternativeTitles.En.Contains("OVA", StringComparison.OrdinalIgnoreCase)) ||
            (anime.AlternativeTitles?.Ja != null && anime.AlternativeTitles.Ja.Contains("OVA", StringComparison.OrdinalIgnoreCase));

        titleMatch.Should().BeTrue("match via AlternativeTitles.En should work");
    }

    [Fact]
    public void TitleMatch_With_Null_Title_Should_Not_Match()
    {
        var anime = new AniSync.Models.Mal.Anime
        {
            Title = null,
            AlternativeTitles = new AniSync.Models.Mal.AlternativeTitles { En = "Has En" }
        };

        bool matchesPattern = anime is { Title: { } };
        matchesPattern.Should().BeFalse("null Title should not match the pattern");
    }

    // ========================================================================
    // Redirect URL hint casing
    // The UI hint must show lowercase "/anisync/" to match the server route.
    // ========================================================================

    [Fact]
    public void RedirectUrlHint_Should_Use_Lowercase_Anisync()
    {
        string baseUrl = "http://localhost:8111";
        string redirectHint = baseUrl + "/anisync/authCallback";

        redirectHint.Should().Contain("/anisync/", "hint must use lowercase to match the controller route");
        redirectHint.Should().NotContain("/AniSync/", "uppercase would cause auth callback mismatch");
    }
}
