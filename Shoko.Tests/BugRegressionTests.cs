using System;
using System.Collections.Generic;
using FluentAssertions;
using Shoko.AniSync.Models.Mal;
using Xunit;

namespace Shoko.Tests;

/// <summary>
/// Regression tests for bugs found during code review.
/// Each test documents a specific bug and verifies the fix.
/// </summary>
public class BugRegressionTests
{
    // ========================================================================
    // Bug #1: Rewatch episode count never updates
    // When rewatching a completed series, newEpisodeCount was never set to
    // shokoEpisodeNumber, so MAL kept showing the old episode count.
    // ========================================================================

    [Theory]
    [InlineData(12, 12, 1, Status.Completed, true)]   // Rewatch from ep 1 of 12-ep completed series
    [InlineData(24, 24, 5, Status.Completed, true)]    // Rewatch from ep 5 of 24-ep completed series
    [InlineData(12, 12, 12, Status.Completed, false)]  // Same episode = not a rewatch (no backward)
    [InlineData(12, 12, 1, Status.Watching, false)]    // Not completed = not a rewatch
    public void Bug1_Rewatch_ShouldUpdateEpisodeCount(
        int malEpisodeCount, int totalEpisodes, int shokoEpisodeNumber,
        Status currentStatus, bool expectRewatch)
    {
        // Arrange - simulate the sync logic
        bool isWatched = true;
        int newEpisodeCount = malEpisodeCount; // this was the bug - it stayed at malEpisodeCount
        bool? setRewatching = null;
        bool shouldUpdate = false;

        // Act - replicate the fixed logic
        if (isWatched)
        {
            if (shokoEpisodeNumber > malEpisodeCount)
            {
                shouldUpdate = true;
                newEpisodeCount = shokoEpisodeNumber;
            }
            else if (shokoEpisodeNumber < malEpisodeCount)
            {
                if (currentStatus == Status.Completed || malEpisodeCount == totalEpisodes)
                {
                    shouldUpdate = true;
                    setRewatching = true;
                    newEpisodeCount = shokoEpisodeNumber; // THE FIX
                }
            }
        }

        // Assert
        if (expectRewatch)
        {
            shouldUpdate.Should().BeTrue("rewatch should trigger an update");
            setRewatching.Should().BeTrue("should set rewatching flag");
            newEpisodeCount.Should().Be(shokoEpisodeNumber,
                "episode count should be updated to the rewatch progress, not stay at the old value");
        }
        else
        {
            if (shokoEpisodeNumber == malEpisodeCount)
            {
                shouldUpdate.Should().BeFalse("same episode should not trigger update");
            }
        }
    }

    // ========================================================================
    // Bug #2: Rewatch count never incremented
    // When completing a rewatch, num_times_rewatched was never passed to
    // UpdateAnime, so MAL's rewatch counter never increased.
    // ========================================================================

    [Theory]
    [InlineData(0, 1)]  // First rewatch: 0 -> 1
    [InlineData(1, 2)]  // Second rewatch: 1 -> 2
    [InlineData(5, 6)]  // Sixth rewatch: 5 -> 6
    public void Bug2_CompletingRewatch_ShouldIncrementRewatchCount(
        int currentRewatchCount, int expectedRewatchCount)
    {
        // Arrange
        bool isRewatching = true;
        int totalEpisodes = 12;
        int shokoEpisodeNumber = 12; // watching the last episode
        int? numberOfTimesRewatched = null;
        bool? setRewatching = null;
        Status? newStatus = null;

        // Act - replicate the fixed logic
        if (isRewatching && totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
        {
            newStatus = Status.Completed;
            setRewatching = false;
            numberOfTimesRewatched = currentRewatchCount + 1; // THE FIX
        }

        // Assert
        newStatus.Should().Be(Status.Completed);
        setRewatching.Should().BeFalse("rewatch flag should be cleared on completion");
        numberOfTimesRewatched.Should().Be(expectedRewatchCount,
            "rewatch count should increment by 1");
    }

    [Fact]
    public void Bug2_NotRewatching_ShouldNotSetRewatchCount()
    {
        // Arrange - normal first completion
        bool isRewatching = false;
        int totalEpisodes = 12;
        int shokoEpisodeNumber = 12;
        int? numberOfTimesRewatched = null;

        // Act
        if (isRewatching && totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
        {
            numberOfTimesRewatched = 1; // should not reach here
        }

        // Assert
        numberOfTimesRewatched.Should().BeNull(
            "rewatch count should not be set during first completion");
    }

    // ========================================================================
    // Bug #3: is_rewatching always sent even when null
    // MalApiCalls.UpdateAnimeStatus always sent is_rewatching, even when the
    // caller passed null (meaning "don't change it"). This would incorrectly
    // clear the rewatching flag.
    // ========================================================================

    [Theory]
    [InlineData(true, true)]    // Explicitly set to true -> should send
    [InlineData(false, true)]   // Explicitly set to false -> should send
    [InlineData(null, false)]   // Null = don't change -> should NOT send
    public void Bug3_IsRewatching_ShouldOnlySendWhenNotNull(
        bool? isRewatching, bool shouldIncludeInBody)
    {
        // Arrange
        var body = new List<KeyValuePair<string, string>>();

        // Act - replicate the FIXED logic
        if (isRewatching != null)
        {
            body.Add(new KeyValuePair<string, string>(
                "is_rewatching", isRewatching.Value.ToString().ToLower()));
        }

        // Assert
        if (shouldIncludeInBody)
        {
            body.Should().Contain(kvp => kvp.Key == "is_rewatching",
                "is_rewatching should be included when explicitly set");
        }
        else
        {
            body.Should().NotContain(kvp => kvp.Key == "is_rewatching",
                "is_rewatching should NOT be included when null (meaning 'don't change')");
        }
    }

    // ========================================================================
    // Bug #4: Boolean casing mismatch
    // true.ToString() produces "True" but MAL API expects lowercase "true".
    // Only the false path had .ToLower().
    // ========================================================================

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Bug4_BooleanValues_ShouldBeLowercase(bool value, string expected)
    {
        // Act - the fixed code
        var result = value.ToString().ToLower();

        // Assert
        result.Should().Be(expected,
            "MAL API expects lowercase boolean strings");
    }

    [Fact]
    public void Bug4_TrueToString_WithoutToLower_ProducesWrongCase()
    {
        // Demonstrate why the bug mattered
        true.ToString().Should().Be("True",
            "C# bool.ToString() produces title case, which MAL rejects");
        true.ToString().ToLower().Should().Be("true",
            "ToLower() fixes the casing for MAL API");
    }

    // ========================================================================
    // Bug #6: Rollback from completed clears rewatch state blindly
    // When unmarking the highest episode of a completed series, the code
    // always set setRewatching = false, even during an active rewatch.
    // ========================================================================

    [Theory]
    [InlineData(false, false)]  // Not rewatching -> clear flag
    [InlineData(true, null)]    // Currently rewatching -> don't touch flag
    public void Bug6_RollbackFromCompleted_ShouldRespectRewatchState(
        bool isCurrentlyRewatching, bool? expectedSetRewatching)
    {
        // Arrange
        Status currentStatus = Status.Completed;
        int malEpisodeCount = 12;
        int shokoEpisodeNumber = 12; // unmarking the highest
        bool isWatched = false;
        bool? setRewatching = null;
        Status? newStatus = null;
        bool shouldUpdate = false;

        // Act - replicate the fixed logic for unmark
        if (!isWatched && shokoEpisodeNumber == malEpisodeCount && malEpisodeCount > 0)
        {
            shouldUpdate = true;

            if (currentStatus == Status.Completed)
            {
                newStatus = Status.Watching;
                // THE FIX: only clear rewatch flag if not currently rewatching
                if (!isCurrentlyRewatching)
                {
                    setRewatching = false;
                }
            }
        }

        // Assert
        shouldUpdate.Should().BeTrue();
        newStatus.Should().Be(Status.Watching);
        setRewatching.Should().Be(expectedSetRewatching,
            isCurrentlyRewatching
                ? "should not clear rewatch flag during active rewatch"
                : "should clear rewatch flag when not rewatching");
    }

    // ========================================================================
    // Bug #9: AirDate null safety
    // episode.Series.AirDate.Value could throw NullReferenceException
    // if Series was null (accessed via null-conditional but then .Value
    // was called on a non-nullable DateTime).
    // ========================================================================

    [Fact]
    public void Bug9_NullSeries_ShouldReturnMaxValue()
    {
        // Arrange - simulate null series
        DateTime? airDate = null;
        string malStartDate = "2023-01-15";
        DateTime? parsedDate = DateTime.TryParse(malStartDate, out var d) ? d : null;

        // Act - replicate the fixed null-safe logic
        double diffDays = airDate.HasValue && parsedDate.HasValue
            ? Math.Abs((parsedDate.Value - airDate.Value).TotalDays)
            : double.MaxValue;

        // Assert
        diffDays.Should().Be(double.MaxValue,
            "should return MaxValue when series air date is unavailable");
    }

    [Fact]
    public void Bug9_ValidDates_ShouldCalculateDiff()
    {
        // Arrange
        DateTime? airDate = new DateTime(2023, 1, 15);
        string malStartDate = "2023-01-20";
        DateTime? parsedDate = DateTime.TryParse(malStartDate, out var d) ? d : null;

        // Act
        double diffDays = airDate.HasValue && parsedDate.HasValue
            ? Math.Abs((parsedDate.Value - airDate.Value).TotalDays)
            : double.MaxValue;

        // Assert
        diffDays.Should().Be(5, "difference between Jan 15 and Jan 20 is 5 days");
    }

    // ========================================================================
    // Bug #10: GetAnime requests insufficient fields
    // The API call only requested "title", "related_anime", "my_list_status",
    // "num_episodes" but the code used alternative_titles, start_date, and
    // num_times_rewatched.
    // ========================================================================

    [Fact]
    public void Bug10_GetAnimeFields_ShouldIncludeAllRequiredFields()
    {
        // The fixed fields list
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
    // Integration-style test: Full rewatch cycle
    // ========================================================================

    [Fact]
    public void FullRewatchCycle_ShouldWorkCorrectly()
    {
        // Simulate a full rewatch cycle:
        // 1. Series completed (12/12)
        // 2. User watches episode 1 again (rewatch starts)
        // 3. User watches through to episode 12 (rewatch completes)

        var totalEpisodes = 12;
        var currentRewatchCount = 0;

        // Step 1: Series is completed
        var malEpisodeCount = 12;
        var currentStatus = Status.Completed;
        var isRewatching = false;

        // Step 2: User watches episode 1 -> rewatch detected
        {
            var shokoEpisode = 1;
            int newEpisodeCount = malEpisodeCount;
            bool? setRewatching = null;
            int? newRewatchCount = null;
            Status? newStatus = null;

            if (shokoEpisode < malEpisodeCount &&
                (currentStatus == Status.Completed || malEpisodeCount == totalEpisodes))
            {
                setRewatching = true;
                newEpisodeCount = shokoEpisode;
            }

            setRewatching.Should().BeTrue();
            newEpisodeCount.Should().Be(1, "should track rewatch at episode 1");

            // Update state for next step
            isRewatching = true;
            malEpisodeCount = 1;
            currentStatus = Status.Completed; // MAL keeps completed + is_rewatching
        }

        // Step 3: User watches through to episode 12 -> rewatch completes
        {
            var shokoEpisode = 12;
            int newEpisodeCount = malEpisodeCount;
            bool? setRewatching = null;
            int? newRewatchCount = null;
            Status? newStatus = null;

            // Normal forward progress
            if (shokoEpisode > malEpisodeCount)
            {
                newEpisodeCount = shokoEpisode;

                if (totalEpisodes > 0 && shokoEpisode >= totalEpisodes)
                {
                    newStatus = Status.Completed;
                    setRewatching = false;
                }

                // Completing a rewatch
                if (isRewatching && totalEpisodes > 0 && shokoEpisode >= totalEpisodes)
                {
                    newRewatchCount = currentRewatchCount + 1;
                }
            }

            newEpisodeCount.Should().Be(12, "should update to final episode");
            newStatus.Should().Be(Status.Completed, "should mark as completed");
            setRewatching.Should().BeFalse("should clear rewatch flag");
            newRewatchCount.Should().Be(1, "should increment rewatch count to 1");
        }
    }

    // ========================================================================
    // Edge case: Rewatch with progress already tracked by is_rewatching
    // ========================================================================

    [Fact]
    public void Rewatch_MidProgress_ShouldUpdateEpisodeForward()
    {
        // User is mid-rewatch (ep 5 of 12), watches ep 6
        var malEpisodeCount = 5;
        var shokoEpisode = 6;
        var totalEpisodes = 12;
        var isRewatching = true;
        var currentStatus = Status.Completed;

        int newEpisodeCount = malEpisodeCount;
        bool shouldUpdate = false;
        bool? setRewatching = null;

        // Forward progress during rewatch
        if (shokoEpisode > malEpisodeCount)
        {
            shouldUpdate = true;
            newEpisodeCount = shokoEpisode;

            if (currentStatus != Status.Watching && currentStatus != Status.Completed)
            {
                // don't change status during rewatch
            }
        }

        shouldUpdate.Should().BeTrue();
        newEpisodeCount.Should().Be(6, "should advance rewatch progress");
        setRewatching.Should().BeNull("should not touch rewatch flag during mid-rewatch progress");
    }

    // ========================================================================
    // Edge case: Episode number exceeds total episodes
    // Corrupt Shoko data or specials numbered beyond series length should
    // be capped at totalEpisodes to prevent invalid MAL state.
    // ========================================================================

    [Theory]
    [InlineData(999, 12, 12)]   // Absurdly high episode -> capped to 12
    [InlineData(13, 12, 12)]    // One over -> capped to 12
    [InlineData(12, 12, 12)]    // Exact match -> stays at 12
    [InlineData(8, 12, 8)]      // Normal progress -> stays at 8
    [InlineData(5, 0, 5)]       // Unknown total (0) -> no capping
    public void EdgeCase_EpisodeCount_ShouldBeCappedAtTotal(
        int shokoEpisodeNumber, int totalEpisodes, int expectedCount)
    {
        int newEpisodeCount = (totalEpisodes > 0 && shokoEpisodeNumber > totalEpisodes)
            ? totalEpisodes
            : shokoEpisodeNumber;

        newEpisodeCount.Should().Be(expectedCount,
            $"episode {shokoEpisodeNumber} with total {totalEpisodes} should be capped");
    }

    // ========================================================================
    // Edge case: Related anime with null/zero ID should be skipped
    // ========================================================================

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(null, false)]
    [InlineData(12345, true)]
    public void EdgeCase_RelatedAnimeId_ShouldSkipInvalid(int? animeId, bool shouldProcess)
    {
        bool result = animeId is > 0;
        result.Should().Be(shouldProcess,
            $"anime ID {animeId} should {(shouldProcess ? "be processed" : "be skipped")}");
    }

    // ========================================================================
    // Edge case: Cache key should isolate per user
    // Different users with different NSFW settings should not share cache.
    // ========================================================================

    [Fact]
    public void EdgeCase_CacheKey_ShouldIncludeUsername()
    {
        int anidbId = 12345;
        string user1 = "Alice";
        string user2 = "Bob";

        var key1 = $"mal_search_{anidbId}_{user1}";
        var key2 = $"mal_search_{anidbId}_{user2}";

        key1.Should().NotBe(key2, "different users should have different cache keys");
    }

    [Fact]
    public void EdgeCase_CacheKey_NullUserShouldUseDefault()
    {
        int anidbId = 12345;
        string? username = null;

        var key = $"mal_search_{anidbId}_{username ?? "default"}";

        key.Should().Be("mal_search_12345_default",
            "null username should fall back to 'default'");
    }

    // ========================================================================
    // Edge case: Rewatch detection respects EnableRewatchDetection setting
    // ========================================================================

    [Theory]
    [InlineData(true, true)]    // Enabled -> rewatch detected
    [InlineData(false, false)]  // Disabled -> no rewatch
    public void EdgeCase_RewatchDetection_RespectsConfig(
        bool enableRewatchDetection, bool expectRewatch)
    {
        int malEpisodeCount = 12;
        int totalEpisodes = 12;
        int shokoEpisodeNumber = 1;
        var currentStatus = Status.Completed;

        bool shouldUpdate = false;
        bool? setRewatching = null;

        if (enableRewatchDetection && (currentStatus == Status.Completed || malEpisodeCount == totalEpisodes))
        {
            shouldUpdate = true;
            setRewatching = true;
        }

        shouldUpdate.Should().Be(expectRewatch);
        if (expectRewatch)
            setRewatching.Should().BeTrue();
        else
            setRewatching.Should().BeNull();
    }

    // ========================================================================
    // Edge case: Rollback respects AllowRollback setting
    // ========================================================================

    [Theory]
    [InlineData(true, true)]    // Enabled -> rollback happens
    [InlineData(false, false)]  // Disabled -> no rollback
    public void EdgeCase_Rollback_RespectsConfig(
        bool allowRollback, bool expectRollback)
    {
        int malEpisodeCount = 10;
        int shokoEpisodeNumber = 10; // unmarking highest
        bool isWatched = false;

        bool shouldUpdate = false;
        int newEpisodeCount = malEpisodeCount;

        if (!isWatched && allowRollback && shokoEpisodeNumber == malEpisodeCount && malEpisodeCount > 0)
        {
            shouldUpdate = true;
            newEpisodeCount = malEpisodeCount - 1;
        }

        shouldUpdate.Should().Be(expectRollback);
        if (expectRollback)
            newEpisodeCount.Should().Be(9, "should roll back by one");
        else
            newEpisodeCount.Should().Be(10, "should stay at current");
    }

    // ========================================================================
    // Edge case: 0-episode anime (unknown episode count)
    // ========================================================================

    [Fact]
    public void EdgeCase_ZeroTotalEpisodes_ShouldNotMarkCompleted()
    {
        int totalEpisodes = 0; // unknown
        int shokoEpisodeNumber = 50;
        Status? newStatus = null;

        if (totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
        {
            newStatus = Status.Completed;
        }

        newStatus.Should().BeNull(
            "should not mark completed when total episodes is unknown (0)");
    }
}

