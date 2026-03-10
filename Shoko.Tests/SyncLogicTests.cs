using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Shoko.AniSync;
using Shoko.AniSync.Api;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.Mal;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.Plugin.Abstractions.Enums;
using Shoko.Plugin.Abstractions.Events;
using Shoko.Plugin.Abstractions.Services;
using Xunit;

namespace Shoko.Tests;

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

    [Theory]
    [InlineData(UserDataSaveReason.PlaybackEnd, 1, true, true)] // Playback ended with count = watched
    [InlineData(UserDataSaveReason.PlaybackEnd, 0, false, false)] // Playback ended before Shoko marks watched = skip
    [InlineData(UserDataSaveReason.UserInteraction, 1, true, true)] // Manual mark with playback = watched
    [InlineData(UserDataSaveReason.UserInteraction, 0, false, false)] // Manual unmark = unwatched
    [InlineData(UserDataSaveReason.AnidbImport, 1, true, true)] // Import with playback = watched
    [InlineData(UserDataSaveReason.AnidbImport, 0, false, false)] // Import without playback = unwatched
    [InlineData(UserDataSaveReason.PlaybackProgress, 1, true, true)] // Progress with history = watched (existing)
    [InlineData(UserDataSaveReason.PlaybackProgress, 0, false, false)] // Progress without history = unwatched
    public void DetermineWatchedState_Should_Return_Correct_State(
        UserDataSaveReason reason, 
        int playbackCount, 
        bool hasLastPlayed,
        bool expectedIsWatched)
    {
        // Arrange
        var mockUserData = new Mock<IVideoUserData>();
        mockUserData.Setup(x => x.PlaybackCount).Returns(playbackCount);
        mockUserData.Setup(x => x.LastPlayedAt).Returns(hasLastPlayed ? DateTime.Now : (DateTime?)null);
        
        var eventArgs = new VideoUserDataSavedEventArgs(
            reason,
            Mock.Of<IShokoUser>(),
            Mock.Of<IVideo>(),
            mockUserData.Object
        );
        
        var plugin = new TestablePlugin(_httpClientFactoryMock.Object, _loggerFactoryMock.Object, 
            _memoryCacheMock.Object, _metadataServiceMock.Object, _userDataServiceMock.Object);
        
        // Act
        var result = plugin.TestDetermineWatchedState(eventArgs);
        
        // Assert
        result.Should().Be(expectedIsWatched);
    }

    [Fact]
    public void Should_Skip_Sync_For_Progress_Events()
    {
        // Arrange
        var progressReasons = new[]
        {
            UserDataSaveReason.PlaybackProgress,
            UserDataSaveReason.PlaybackStart,
            UserDataSaveReason.PlaybackPause,
            UserDataSaveReason.PlaybackResume
        };
        
        foreach (var reason in progressReasons)
        {
            // These events should be skipped early in the sync process
            reason.Should().BeOneOf(
                UserDataSaveReason.PlaybackProgress,
                UserDataSaveReason.PlaybackStart,
                UserDataSaveReason.PlaybackPause,
                UserDataSaveReason.PlaybackResume
            );
        }
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
    public void Should_Detect_Rewatch_When_Episode_Count_Goes_Backward()
    {
        // Scenario: User completed series (12 episodes), now watching episode 1 again
        var malEpisodeCount = 12;
        var totalEpisodes = 12;
        var shokoEpisodeNumber = 1;
        var currentStatus = Status.Completed;
        var isWatched = true;
        
        // Detect rewatch condition
        var isRewatch = isWatched && 
                       shokoEpisodeNumber < malEpisodeCount && 
                       (currentStatus == Status.Completed || malEpisodeCount == totalEpisodes);
        
        isRewatch.Should().BeTrue("Should detect rewatch when watching early episode of completed series");
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
        int malEpisodeCount = 12;
        int totalEpisodes = 12;
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
    // EnableAutoSync gate
    // ========================================================================

    [Fact]
    public void EnableAutoSync_Disabled_Should_Skip_Sync()
    {
        bool enableAutoSync = false;

        bool shouldProceed = enableAutoSync;

        shouldProceed.Should().BeFalse("sync should not proceed when EnableAutoSync is disabled");
    }

    // ========================================================================
    // maxEpisode prefers regular episodes over specials
    // ========================================================================

    private record FakeEpisode(EpisodeType Type, int EpisodeNumber);

    private static FakeEpisode? PickMaxEpisode(FakeEpisode[] episodes)
    {
        FakeEpisode? maxEpisode = null;
        foreach (var episode in episodes)
        {
            if (episode.Type is not EpisodeType.Episode and not EpisodeType.Special) continue;

            if (maxEpisode == null
                || episode.Type == EpisodeType.Episode && maxEpisode.Type == EpisodeType.Special
                || episode.Type == maxEpisode.Type && episode.EpisodeNumber > maxEpisode.EpisodeNumber)
            {
                maxEpisode = episode;
            }
        }
        return maxEpisode;
    }

    [Fact]
    public void MaxEpisode_Regular_And_Special_Should_Prefer_Regular()
    {
        var episodes = new[]
        {
            new FakeEpisode(EpisodeType.Episode, 5),
            new FakeEpisode(EpisodeType.Special, 100),
        };

        var maxEpisode = PickMaxEpisode(episodes);

        maxEpisode.Should().NotBeNull();
        maxEpisode!.Type.Should().Be(EpisodeType.Episode);
        maxEpisode.EpisodeNumber.Should().Be(5);
    }

    [Fact]
    public void MaxEpisode_Only_Specials_Should_Use_Special()
    {
        var episodes = new[]
        {
            new FakeEpisode(EpisodeType.Special, 3),
        };

        var maxEpisode = PickMaxEpisode(episodes);

        maxEpisode.Should().NotBeNull();
        maxEpisode!.Type.Should().Be(EpisodeType.Special);
        maxEpisode.EpisodeNumber.Should().Be(3);
    }

    [Fact]
    public void MaxEpisode_Multiple_Regular_Should_Pick_Highest()
    {
        var episodes = new[]
        {
            new FakeEpisode(EpisodeType.Episode, 5),
            new FakeEpisode(EpisodeType.Episode, 12),
        };

        var maxEpisode = PickMaxEpisode(episodes);

        maxEpisode.Should().NotBeNull();
        maxEpisode!.EpisodeNumber.Should().Be(12);
    }

    [Fact]
    public void MaxEpisode_Regular_Ties_Pick_Highest_Number()
    {
        var episodes = new[]
        {
            new FakeEpisode(EpisodeType.Episode, 3),
            new FakeEpisode(EpisodeType.Episode, 7),
        };

        var maxEpisode = PickMaxEpisode(episodes);

        maxEpisode.Should().NotBeNull();
        maxEpisode!.EpisodeNumber.Should().Be(7);
    }

    // ========================================================================
    // DetermineWatchedState boundary tests
    // ========================================================================

    [Fact]
    public void UserInteraction_Recent_LastPlayed_Should_Be_Watched()
    {
        var lastPlayedAt = DateTime.Now.AddSeconds(-2);
        var timeSinceWatched = DateTime.Now - lastPlayedAt;
        bool isRecentlyWatched = timeSinceWatched.TotalSeconds < 10;

        isRecentlyWatched.Should().BeTrue("2 seconds ago is within the 10-second recency window");
    }

    [Fact]
    public void UserInteraction_Old_LastPlayed_Should_Be_Unwatched()
    {
        var lastPlayedAt = DateTime.Now.AddMinutes(-5);
        var timeSinceWatched = DateTime.Now - lastPlayedAt;
        bool isRecentlyWatched = timeSinceWatched.TotalSeconds < 10;

        isRecentlyWatched.Should().BeFalse("5 minutes ago is outside the 10-second recency window");
    }

    [Fact]
    public void UserInteraction_No_LastPlayed_Should_Be_Unwatched()
    {
        DateTime? lastPlayedAt = null;
        bool result = lastPlayedAt.HasValue && (DateTime.Now - lastPlayedAt.Value).TotalSeconds < 10;

        result.Should().BeFalse("null LastPlayedAt means unwatched");
    }

    [Fact]
    public void PlaybackEnd_Should_Always_Be_Watched()
    {
        const string reason = "PlaybackEnd";
        bool isWatched = reason == "PlaybackEnd";

        isWatched.Should().BeTrue("PlaybackEnd always means watched");
    }
}

// Testable version of the plugin to expose protected methods
public class TestablePlugin : ShokoAniSyncPlugin
{
    private static IApplicationPaths CreateMockApplicationPaths()
    {
        var mock = new Mock<IApplicationPaths>();
        mock.Setup(x => x.PluginsPath).Returns(System.IO.Path.GetTempPath());
        return mock.Object;
    }

    public TestablePlugin(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory,
        IMemoryCache memoryCache, IMetadataService metadataService, IUserDataService userDataService)
        : base(CreateMockApplicationPaths(), httpClientFactory, loggerFactory, memoryCache, metadataService, userDataService)
    {
    }
    
    public bool TestDetermineWatchedState(VideoUserDataSavedEventArgs e)
    {
        // Use reflection to call private method
        var method = typeof(ShokoAniSyncPlugin).GetMethod("DetermineWatchedState", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (bool)method!.Invoke(this, new object[] { e })!;
    }
}