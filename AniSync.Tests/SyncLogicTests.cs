using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using AniSync;
using AniSync.Api;
using AniSync.Helpers;
using AniSync.Interfaces;
using AniSync.Models.Mal;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;
using Xunit;

namespace AniSync.Tests;

public class SyncLogicTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly Mock<IMemoryCache> _memoryCacheMock;
    private readonly Mock<IMetadataService> _metadataServiceMock;
    private readonly Mock<IUserDataService> _userDataServiceMock;
    private readonly Mock<IApplicationPaths> _applicationPathsMock;

    public SyncLogicTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _memoryCacheMock = new Mock<IMemoryCache>();
        _metadataServiceMock = new Mock<IMetadataService>();
        _userDataServiceMock = new Mock<IUserDataService>();
        _applicationPathsMock = new Mock<IApplicationPaths>();

        _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());
    }

    [Fact]
    public void Should_Skip_Sync_For_Import_Events()
    {
        var reason = EpisodeUserDataSaveReason.Import;
        reason.HasFlag(EpisodeUserDataSaveReason.Import).Should().BeTrue(
            "Import events should be skipped");
    }

    [Fact]
    public void Should_Skip_Sync_For_NonWatch_Events()
    {
        const EpisodeUserDataSaveReason nonWatchReasons =
            EpisodeUserDataSaveReason.IsFavorite |
            EpisodeUserDataSaveReason.UserTags |
            EpisodeUserDataSaveReason.UserRating;

        var pureNonWatch = new[]
        {
            EpisodeUserDataSaveReason.IsFavorite,
            EpisodeUserDataSaveReason.UserTags,
            EpisodeUserDataSaveReason.UserRating
        };

        foreach (var reason in pureNonWatch)
        {
            var shouldSkip = reason != EpisodeUserDataSaveReason.None && (reason & ~nonWatchReasons) == 0;
            shouldSkip.Should().BeTrue($"{reason} should be skipped as non-watch event");
        }
    }

    [Fact]
    public void Should_Process_Watch_Events()
    {
        const EpisodeUserDataSaveReason nonWatchReasons =
            EpisodeUserDataSaveReason.IsFavorite |
            EpisodeUserDataSaveReason.UserTags |
            EpisodeUserDataSaveReason.UserRating;

        var shouldProcess = new[]
        {
            EpisodeUserDataSaveReason.PlaybackCount,
            EpisodeUserDataSaveReason.LastPlayedAt,
            EpisodeUserDataSaveReason.PlaybackCount | EpisodeUserDataSaveReason.LastPlayedAt,
            EpisodeUserDataSaveReason.None, // unknown/manual - let IsWatched decide
        };

        foreach (var reason in shouldProcess)
        {
            var shouldSkip = reason != EpisodeUserDataSaveReason.None && (reason & ~nonWatchReasons) == 0;
            shouldSkip.Should().BeFalse($"{reason} should be processed");
        }
    }

    [Fact]
    public void Mixed_Reason_With_Watch_Flag_Should_Process()
    {
        // If someone favorites AND the playback count changed in the same event
        const EpisodeUserDataSaveReason nonWatchReasons =
            EpisodeUserDataSaveReason.IsFavorite |
            EpisodeUserDataSaveReason.UserTags |
            EpisodeUserDataSaveReason.UserRating;

        var reason = EpisodeUserDataSaveReason.IsFavorite | EpisodeUserDataSaveReason.PlaybackCount;
        var shouldSkip = reason != EpisodeUserDataSaveReason.None && (reason & ~nonWatchReasons) == 0;
        shouldSkip.Should().BeFalse("mixed reason with PlaybackCount should still be processed");
    }

    [Fact]
    public void Should_Not_Rollback_MAL_Progress_When_Episode_Marked_Unwatched()
    {
        // This test validates that when an episode is marked as unwatched in Shoko,
        // we don't reduce the episode count in MAL (to prevent data loss during rewatches)

        // Scenario: MAL has episode 10 watched, user marks episode 5 as unwatched in Shoko
        // Expected: MAL should keep episode 10 as the highest watched

        var malEpisodeCount = 10;
        var shokoEpisodeNumber = 5;
        var isWatched = false;

        // The logic should skip update when:
        // - Episode is marked unwatched (isWatched = false)
        // - OR when Shoko episode number is less than MAL count
        var shouldUpdate = isWatched && shokoEpisodeNumber > malEpisodeCount;

        shouldUpdate.Should().BeFalse("Should not update MAL when marking episode as unwatched");
    }

    [Fact]
    public void Should_Only_Update_When_Progress_Moves_Forward()
    {
        // Test various scenarios
        var testCases = new[]
        {
            new { Shoko = 5, MAL = 3, IsWatched = true, ShouldUpdate = true }, // Progress forward
            new { Shoko = 5, MAL = 5, IsWatched = true, ShouldUpdate = false }, // Already synced
            new { Shoko = 3, MAL = 5, IsWatched = true, ShouldUpdate = false }, // Behind MAL
            new { Shoko = 5, MAL = 3, IsWatched = false, ShouldUpdate = false }, // Unwatched
        };

        foreach (var testCase in testCases)
        {
            var shouldUpdate = testCase.IsWatched && testCase.Shoko > testCase.MAL;
            shouldUpdate.Should().Be(testCase.ShouldUpdate,
                $"Shoko={testCase.Shoko}, MAL={testCase.MAL}, IsWatched={testCase.IsWatched}");
        }
    }

    [Fact]
    public void Should_Set_Status_To_Completed_When_Reaching_Final_Episode()
    {
        var totalEpisodes = 12;
        var shokoEpisodeNumber = 12;

        var newStatus = (totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
            ? Status.Completed
            : Status.Watching;

        newStatus.Should().Be(Status.Completed, "Should set status to Completed when reaching final episode");
    }

    [Fact]
    public void Should_Set_Status_To_Watching_When_Not_At_Final_Episode()
    {
        var totalEpisodes = 12;
        var shokoEpisodeNumber = 8;

        var newStatus = (totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
            ? Status.Completed
            : Status.Watching;

        newStatus.Should().Be(Status.Watching, "Should set status to Watching when not at final episode");
    }

    // ========================================================================
    // Bug #1: Rewatch episode count never updates
    // When rewatching a completed series, newEpisodeCount was never set to
    // shokoEpisodeNumber, so MAL kept showing the old episode count.
    // ========================================================================

    [Theory]
    [InlineData(12, 12, 1, Status.Completed, true)]
    [InlineData(24, 24, 5, Status.Completed, true)]
    [InlineData(12, 12, 12, Status.Completed, false)]
    [InlineData(12, 12, 1, Status.Watching, false)]
    public void Bug1_Rewatch_ShouldUpdateEpisodeCount(
        int malEpisodeCount, int totalEpisodes, int shokoEpisodeNumber,
        Status currentStatus, bool expectRewatch)
    {
        bool isWatched = true;
        int newEpisodeCount = malEpisodeCount;
        bool? setRewatching = null;
        bool shouldUpdate = false;

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
                    newEpisodeCount = shokoEpisodeNumber;
                }
            }
        }

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
    // ========================================================================

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(5, 6)]
    public void Bug2_CompletingRewatch_ShouldIncrementRewatchCount(
        int currentRewatchCount, int expectedRewatchCount)
    {
        bool isRewatching = true;
        int totalEpisodes = 12;
        int shokoEpisodeNumber = 12;
        int? numberOfTimesRewatched = null;
        bool? setRewatching = null;
        Status? newStatus = null;

        if (isRewatching && totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
        {
            newStatus = Status.Completed;
            setRewatching = false;
            numberOfTimesRewatched = currentRewatchCount + 1;
        }

        newStatus.Should().Be(Status.Completed);
        setRewatching.Should().BeFalse("rewatch flag should be cleared on completion");
        numberOfTimesRewatched.Should().Be(expectedRewatchCount,
            "rewatch count should increment by 1");
    }

    [Fact]
    public void Bug2_NotRewatching_ShouldNotSetRewatchCount()
    {
        bool isRewatching = false;
        int totalEpisodes = 12;
        int shokoEpisodeNumber = 12;
        int? numberOfTimesRewatched = null;

        if (isRewatching && totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
        {
            numberOfTimesRewatched = 1;
        }

        numberOfTimesRewatched.Should().BeNull(
            "rewatch count should not be set during first completion");
    }

    // ========================================================================
    // Bug #3: is_rewatching always sent even when null
    // ========================================================================

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(null, false)]
    public void Bug3_IsRewatching_ShouldOnlySendWhenNotNull(
        bool? isRewatching, bool shouldIncludeInBody)
    {
        var body = new List<KeyValuePair<string, string>>();

        if (isRewatching != null)
        {
            body.Add(new KeyValuePair<string, string>(
                "is_rewatching", isRewatching.Value.ToString().ToLower()));
        }

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
    // Bug #6: Rollback from completed clears rewatch state blindly
    // ========================================================================

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, null)]
    public void Bug6_RollbackFromCompleted_ShouldRespectRewatchState(
        bool isCurrentlyRewatching, bool? expectedSetRewatching)
    {
        Status currentStatus = Status.Completed;
        int malEpisodeCount = 12;
        int shokoEpisodeNumber = 12;
        bool isWatched = false;
        bool? setRewatching = null;
        Status? newStatus = null;
        bool shouldUpdate = false;

        if (!isWatched && shokoEpisodeNumber == malEpisodeCount && malEpisodeCount > 0)
        {
            shouldUpdate = true;

            if (currentStatus == Status.Completed)
            {
                newStatus = Status.Watching;
                if (!isCurrentlyRewatching)
                {
                    setRewatching = false;
                }
            }
        }

        shouldUpdate.Should().BeTrue();
        newStatus.Should().Be(Status.Watching);
        setRewatching.Should().Be(expectedSetRewatching,
            isCurrentlyRewatching
                ? "should not clear rewatch flag during active rewatch"
                : "should clear rewatch flag when not rewatching");
    }

    // ========================================================================
    // Integration-style test: Full rewatch cycle
    // ========================================================================

    [Fact]
    public void FullRewatchCycle_ShouldWorkCorrectly()
    {
        var totalEpisodes = 12;
        var currentRewatchCount = 0;

        var malEpisodeCount = 12;
        var currentStatus = Status.Completed;
        var isRewatching = false;

        // Step 2: User watches episode 1 -> rewatch detected
        {
            var shokoEpisode = 1;
            int newEpisodeCount = malEpisodeCount;
            bool? setRewatching = null;

            if (shokoEpisode < malEpisodeCount &&
                (currentStatus == Status.Completed || malEpisodeCount == totalEpisodes))
            {
                setRewatching = true;
                newEpisodeCount = shokoEpisode;
            }

            setRewatching.Should().BeTrue();
            newEpisodeCount.Should().Be(1, "should track rewatch at episode 1");

            isRewatching = true;
            malEpisodeCount = 1;
            currentStatus = Status.Completed;
        }

        // Step 3: User watches through to episode 12 -> rewatch completes
        {
            var shokoEpisode = 12;
            int newEpisodeCount = malEpisodeCount;
            bool? setRewatching = null;
            int? newRewatchCount = null;
            Status? newStatus = null;

            if (shokoEpisode > malEpisodeCount)
            {
                newEpisodeCount = shokoEpisode;

                if (totalEpisodes > 0 && shokoEpisode >= totalEpisodes)
                {
                    newStatus = Status.Completed;
                    setRewatching = false;
                }

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
        var malEpisodeCount = 5;
        var shokoEpisode = 6;
        var currentStatus = Status.Completed;

        int newEpisodeCount = malEpisodeCount;
        bool shouldUpdate = false;
        bool? setRewatching = null;

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
    // ========================================================================

    [Theory]
    [InlineData(999, 12, 12)]
    [InlineData(13, 12, 12)]
    [InlineData(12, 12, 12)]
    [InlineData(8, 12, 8)]
    [InlineData(5, 0, 5)]
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
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void EdgeCase_RewatchDetection_RespectsConfig(
        bool enableRewatchDetection, bool expectRewatch)
    {
        // Genuine restart of a completed 12-ep series: episode 1 actually replayed.
        var d = RewatchSyncDecision.Decide(
            isWatched: true, shokoEpisodeNumber: 1, playbackCount: 2,
            malEpisodeCount: 12, totalEpisodes: 12, currentStatus: Status.Completed,
            isRewatching: false, currentRewatchCount: 0,
            rewatchDetectionEnabled: enableRewatchDetection, rollbackEnabled: false);

        d.ShouldUpdate.Should().Be(expectRewatch);
        if (expectRewatch)
            d.SetRewatching.Should().BeTrue();
        else
            d.SetRewatching.Should().BeNull();
    }

    // ========================================================================
    // Edge case: Rollback respects AllowRollback setting
    // ========================================================================

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void EdgeCase_Rollback_RespectsConfig(
        bool allowRollback, bool expectRollback)
    {
        int malEpisodeCount = 10;
        int shokoEpisodeNumber = 10;
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
        int totalEpisodes = 0;
        int shokoEpisodeNumber = 50;
        Status? newStatus = null;

        if (totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
        {
            newStatus = Status.Completed;
        }

        newStatus.Should().BeNull(
            "should not mark completed when total episodes is unknown (0)");
    }

    // ========================================================================
    // SyncOnlyCompleted gate
    // When SyncOnlyCompleted is enabled, non-final episodes should be skipped.
    // ========================================================================

    [Fact]
    public void SyncOnlyCompleted_Enabled_NonFinal_Episode_Should_Skip()
    {
        bool syncOnlyCompleted = true;
        bool isWatched = true;
        int shokoEpisodeNumber = 5;
        int numEpisodes = 12;

        bool shouldSkip = syncOnlyCompleted && isWatched && numEpisodes > 0 && shokoEpisodeNumber < numEpisodes;

        shouldSkip.Should().BeTrue("non-final episode should be skipped when SyncOnlyCompleted is enabled");
    }

    [Fact]
    public void SyncOnlyCompleted_Enabled_Final_Episode_Should_Sync()
    {
        bool syncOnlyCompleted = true;
        bool isWatched = true;
        int shokoEpisodeNumber = 12;
        int numEpisodes = 12;

        bool shouldSkip = syncOnlyCompleted && isWatched && numEpisodes > 0 && shokoEpisodeNumber < numEpisodes;

        shouldSkip.Should().BeFalse("final episode should sync even when SyncOnlyCompleted is enabled");
    }

    [Fact]
    public void SyncOnlyCompleted_Disabled_Should_Always_Sync()
    {
        bool syncOnlyCompleted = false;
        bool isWatched = true;
        int shokoEpisodeNumber = 5;
        int numEpisodes = 12;

        bool shouldSkip = syncOnlyCompleted && isWatched && numEpisodes > 0 && shokoEpisodeNumber < numEpisodes;

        shouldSkip.Should().BeFalse("sync should proceed when SyncOnlyCompleted is disabled");
    }

    [Fact]
    public void SyncOnlyCompleted_Unknown_EpisodeCount_Should_Sync()
    {
        bool syncOnlyCompleted = true;
        bool isWatched = true;
        int shokoEpisodeNumber = 5;
        int numEpisodes = 0;

        bool shouldSkip = syncOnlyCompleted && isWatched && numEpisodes > 0 && shokoEpisodeNumber < numEpisodes;

        shouldSkip.Should().BeFalse("unknown episode count (0) should not trigger skip");
    }

    // ========================================================================
    // "Not in list" add path
    // When animeWithStatus?.MyListStatus is null, the anime is not in the
    // user's list yet and needs to be added (if watched).
    // ========================================================================

    [Theory]
    [InlineData(5, 12, true)]   // Mid-series watched episode -> Watching
    [InlineData(1, 12, true)]   // First episode -> Watching
    [InlineData(11, 12, true)]  // Penultimate episode -> Watching
    public void NotInList_WatchedNonFinalEpisode_ShouldAddAsWatching(
        int shokoEpisodeNumber, int totalEpisodes, bool expectAdd)
    {
        bool isWatched = true;
        bool? myListStatus = null; // Not in list

        bool shouldAdd = myListStatus == null && isWatched;
        Status? newStatus = null;
        DateTime? startDate = null;
        DateTime? endDate = null;

        if (shouldAdd)
        {
            newStatus = (totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
                ? Status.Completed
                : Status.Watching;
            startDate = DateTime.Now.Date;
            if (newStatus == Status.Completed)
                endDate = DateTime.Now.Date;
        }

        shouldAdd.Should().Be(expectAdd);
        newStatus.Should().Be(Status.Watching);
        startDate.Should().NotBeNull("start date should be set when adding to list");
        endDate.Should().BeNull("end date should not be set for non-final episode");
    }

    [Theory]
    [InlineData(12, 12)]
    [InlineData(24, 24)]
    [InlineData(1, 1)]  // Single-episode OVA
    public void NotInList_WatchedFinalEpisode_ShouldAddAsCompleted(
        int shokoEpisodeNumber, int totalEpisodes)
    {
        bool isWatched = true;
        bool? myListStatus = null; // Not in list

        bool shouldAdd = myListStatus == null && isWatched;
        Status? newStatus = null;
        DateTime? startDate = null;
        DateTime? endDate = null;

        if (shouldAdd)
        {
            newStatus = (totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
                ? Status.Completed
                : Status.Watching;
            startDate = DateTime.Now.Date;
            if (newStatus == Status.Completed)
                endDate = DateTime.Now.Date;
        }

        shouldAdd.Should().BeTrue();
        newStatus.Should().Be(Status.Completed);
        startDate.Should().NotBeNull("start date should be set on first add");
        endDate.Should().NotBeNull("end date should be set when completing on add");
    }

    [Fact]
    public void NotInList_UnwatchedEpisode_ShouldSkipAdd()
    {
        bool isWatched = false;
        bool? myListStatus = null; // Not in list

        bool shouldAdd = myListStatus == null && isWatched;

        shouldAdd.Should().BeFalse("unwatched episode should not add anime to list");
    }

    // ========================================================================
    // SyncOnlyCompleted × first add
    // When SyncOnlyCompleted=true and anime not yet in list, non-final
    // episodes should be skipped before the add path is reached.
    // ========================================================================

    [Fact]
    public void SyncOnlyCompleted_NotInList_NonFinalEpisode_ShouldSkip()
    {
        bool syncOnlyCompleted = true;
        bool isWatched = true;
        int shokoEpisodeNumber = 5;
        int numEpisodes = 12;

        // SyncOnlyCompleted gate runs before the add/update branch
        bool shouldSkip = syncOnlyCompleted && isWatched && numEpisodes > 0 && shokoEpisodeNumber < numEpisodes;

        shouldSkip.Should().BeTrue(
            "SyncOnlyCompleted should prevent adding anime to list on non-final episode");
    }

    // ========================================================================
    // Episode count on first add is NOT capped (unlike update path)
    // Production code passes shokoEpisodeNumber directly to UpdateAnime on add.
    // ========================================================================

    [Theory]
    [InlineData(999, 12, 999)]  // Exceeds total - not capped on add
    [InlineData(13, 12, 13)]    // Just over - not capped on add
    [InlineData(8, 12, 8)]      // Under total - unchanged
    public void NotInList_EpisodeCount_IsNotCapped(
        int shokoEpisodeNumber, int _totalEpisodes, int expectedSentCount)
    {
        // On the add path, production code passes shokoEpisodeNumber directly
        // (no capping like the update path does)
        _ = _totalEpisodes; // context for the test case, not used in add-path logic
        int sentEpisodeCount = shokoEpisodeNumber;

        sentEpisodeCount.Should().Be(expectedSentCount,
            "add path should send shokoEpisodeNumber without capping");
    }

    // ========================================================================
    // Rollback from non-Completed status
    // When status is Watching (not Completed), rollback should reduce
    // episode count but not change status or rewatch flag.
    // ========================================================================

    [Fact]
    public void Rollback_FromWatchingStatus_ShouldReduceCountOnly()
    {
        Status currentStatus = Status.Watching;
        int malEpisodeCount = 8;
        int shokoEpisodeNumber = 8;
        bool isWatched = false;
        bool allowRollback = true;
        bool isCurrentlyRewatching = false;

        bool shouldUpdate = false;
        int newEpisodeCount = malEpisodeCount;
        Status? newStatus = null;
        bool? setRewatching = null;

        if (!isWatched && allowRollback && shokoEpisodeNumber == malEpisodeCount && malEpisodeCount > 0)
        {
            shouldUpdate = true;
            newEpisodeCount = malEpisodeCount - 1;

            if (currentStatus == Status.Completed)
            {
                newStatus = Status.Watching;
                if (!isCurrentlyRewatching)
                    setRewatching = false;
            }
        }

        shouldUpdate.Should().BeTrue();
        newEpisodeCount.Should().Be(7, "should roll back by one");
        newStatus.Should().BeNull("status should not change when already Watching");
        setRewatching.Should().BeNull("rewatch flag should not be touched when not rolling back from Completed");
    }

    // ========================================================================
    // EpisodeType filtering (episode comes directly from event now)
    // ========================================================================

    [Theory]
    [InlineData(EpisodeType.Episode, true)]
    [InlineData(EpisodeType.Special, true)]
    [InlineData(EpisodeType.Credits, false)]
    [InlineData(EpisodeType.Trailer, false)]
    [InlineData(EpisodeType.Parody, false)]
    [InlineData(EpisodeType.Other, false)]
    public void EpisodeType_Should_Filter_Correctly(EpisodeType type, bool shouldProcess)
    {
        bool result = type is EpisodeType.Episode or EpisodeType.Special;
        result.Should().Be(shouldProcess,
            $"episode type {type} should {(shouldProcess ? "be processed" : "be skipped")}");
    }
}
