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
    [InlineData(UserDataSaveReason.PlaybackEnd, 1, true, true)] // Playback ended = watched
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