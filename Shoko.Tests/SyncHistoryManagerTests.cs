using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Models;
using Shoko.AniSync.Models.Mal;
using Xunit;

namespace Shoko.Tests
{
    public class SyncHistoryManagerTests : IDisposable
    {
        private readonly string _testPath;
        private readonly SyncHistoryManager _syncHistoryManager;
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<ILogger<SyncHistoryManager>> _loggerMock;

        public SyncHistoryManagerTests()
        {
            _testPath = Path.Combine(Path.GetTempPath(), $"test-sync-history-{Guid.NewGuid()}");
            Directory.CreateDirectory(_testPath);
            
            _loggerMock = new Mock<ILogger<SyncHistoryManager>>();
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_loggerMock.Object);
            
            _syncHistoryManager = new SyncHistoryManager(_testPath, _loggerFactoryMock.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testPath))
            {
                Directory.Delete(_testPath, true);
            }
        }

        [Fact]
        public async Task LogSync_AddsEntrySuccessfully()
        {
            // Arrange
            var username = "testuser";
            var animeId = 12345;
            var animeTitle = "Test Anime";
            var episodeNumber = 5;
            var action = "Watched";
            var success = true;
            var status = Status.Watching;
            var providerName = "MAL";
            var providerUsername = "maluser";

            // Act
            _syncHistoryManager.LogSync(
                username, 
                animeId, 
                animeTitle, 
                episodeNumber, 
                action, 
                success, 
                status,
                providerName,
                null,
                providerUsername
            );

            // Give async operation time to complete
            await Task.Delay(100);

            // Assert - verify the file was created
            var historyFile = Path.Combine(_testPath, "sync-history.json");
            Assert.True(File.Exists(historyFile));
        }

        [Fact]
        public async Task GetUserStatsAsync_ReturnsCorrectStats()
        {
            // Arrange
            var username = "testuser";
            
            // Add multiple entries
            _syncHistoryManager.LogSync(username, 1, "Anime 1", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync(username, 2, "Anime 2", 12, "Completed", true, Status.Completed);
            _syncHistoryManager.LogSync(username, 3, "Anime 3", 5, "Watched", false, Status.Watching); // Failed sync
            
            // Give async operations time to complete
            await Task.Delay(200);

            // Act
            var stats = await _syncHistoryManager.GetUserStatsAsync(username);

            // Assert
            Assert.NotNull(stats);
            Assert.Equal(3, stats.TotalSyncs);
            Assert.Equal(1, stats.FailedSyncs);
            Assert.Equal(2, stats.SuccessfulSyncs);
            Assert.Equal(3, stats.History.Count);
        }

        [Fact]
        public async Task GetUserHistoryAsync_ReturnsCorrectHistory()
        {
            // Arrange
            var username = "testuser";
            
            // Add entries
            _syncHistoryManager.LogSync(username, 1, "Anime 1", 1, "Watched", true, Status.Watching, "MAL", "image1.jpg", "maluser");
            _syncHistoryManager.LogSync(username, 2, "Anime 2", 12, "Completed", true, Status.Completed, "MAL", "image2.jpg", "maluser");
            
            await Task.Delay(200);

            // Act
            var history = await _syncHistoryManager.GetUserHistoryAsync(username);

            // Assert
            Assert.NotNull(history);
            Assert.Equal(2, history.Count);
            
            // Check first entry (most recent should be first)
            var firstEntry = history[0];
            Assert.Equal(2, firstEntry.AnimeId);
            Assert.Equal("Anime 2", firstEntry.AnimeTitle);
            Assert.Equal(12, firstEntry.EpisodeNumber);
            Assert.Equal((int)SyncAction.Completed, firstEntry.Action);
            Assert.Equal((int)Status.Completed, firstEntry.Status);
            Assert.True(firstEntry.Success);
            Assert.Equal("MAL", firstEntry.Provider.Name);
            Assert.Equal("maluser", firstEntry.Provider.Username);
        }

        [Fact]
        public async Task GetUserHistoryAsync_WithLimit_ReturnsLimitedHistory()
        {
            // Arrange
            var username = "testuser";
            
            // Add 5 entries
            for (int i = 1; i <= 5; i++)
            {
                _syncHistoryManager.LogSync(username, i, $"Anime {i}", i, "Watched", true, Status.Watching);
                await Task.Delay(50); // Small delay to ensure different timestamps
            }
            
            await Task.Delay(200);

            // Act
            var history = await _syncHistoryManager.GetUserHistoryAsync(username, limit: 3);

            // Assert
            Assert.NotNull(history);
            Assert.Equal(3, history.Count);
        }

        [Fact]
        public async Task GetStatsAsync_AggregatesAllUsers()
        {
            // Arrange
            _syncHistoryManager.LogSync("user1", 1, "Anime 1", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync("user1", 2, "Anime 2", 1, "Watched", false, Status.Watching);
            _syncHistoryManager.LogSync("user2", 3, "Anime 3", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync("user2", 4, "Anime 4", 12, "Completed", true, Status.Completed);
            
            await Task.Delay(200);

            // Act
            var stats = await _syncHistoryManager.GetStatsAsync();

            // Assert
            Assert.NotNull(stats);
            Assert.Equal(4, stats.TotalSyncs);
            Assert.Equal(1, stats.FailedSyncs);
            Assert.Equal(3, stats.SuccessfulSyncs);
            Assert.Equal(2, stats.SyncsByUser.Count);
            Assert.Equal(2, stats.SyncsByUser["user1"]);
            Assert.Equal(2, stats.SyncsByUser["user2"]);
        }

        [Fact]
        public async Task ClearHistoryAsync_ClearsUserHistory()
        {
            // Arrange
            var username = "testuser";
            _syncHistoryManager.LogSync(username, 1, "Anime 1", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync(username, 2, "Anime 2", 2, "Watched", true, Status.Watching);
            
            await Task.Delay(200);

            // Act
            await _syncHistoryManager.ClearHistoryAsync(username);
            
            // Assert
            var stats = await _syncHistoryManager.GetUserStatsAsync(username);
            Assert.Null(stats);
        }

        [Fact]
        public async Task ClearHistoryAsync_ClearsAllHistory()
        {
            // Arrange
            _syncHistoryManager.LogSync("user1", 1, "Anime 1", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync("user2", 2, "Anime 2", 2, "Watched", true, Status.Watching);
            
            await Task.Delay(200);

            // Act
            await _syncHistoryManager.ClearHistoryAsync();
            
            // Assert
            var stats = await _syncHistoryManager.GetStatsAsync();
            Assert.Equal(0, stats.TotalSyncs);
            Assert.Empty(stats.SyncsByUser);
        }

        [Fact]
        public async Task LogSync_HandlesSpecialCharactersInTitle()
        {
            // Arrange
            var username = "testuser";
            var animeTitle = "Test: Anime & \"Special\" <Characters>";
            
            // Act
            _syncHistoryManager.LogSync(username, 1, animeTitle, 1, "Watched", true, Status.Watching);
            
            await Task.Delay(200);
            
            // Assert
            var history = await _syncHistoryManager.GetUserHistoryAsync(username);
            Assert.NotNull(history);
            Assert.Single(history);
            Assert.Equal(animeTitle, history[0].AnimeTitle);
        }

        [Fact]
        public async Task LogSync_HandlesDifferentActions()
        {
            // Arrange
            var username = "testuser";
            var actions = new[] { "Watched", "Unwatched", "Completed", "Rewatching", "Rolled back", "Added to list", "Updated" };
            
            // Act
            int id = 1;
            foreach (var action in actions)
            {
                _syncHistoryManager.LogSync(username, id++, $"Anime {id}", 1, action, true, Status.Watching);
            }
            
            await Task.Delay(300);
            
            // Assert
            var history = await _syncHistoryManager.GetUserHistoryAsync(username);
            Assert.NotNull(history);
            Assert.Equal(actions.Length, history.Count);
            
            // Verify each action was parsed correctly
            Assert.Contains(history, h => h.Action == (int)SyncAction.Watched);
            Assert.Contains(history, h => h.Action == (int)SyncAction.Unwatched);
            Assert.Contains(history, h => h.Action == (int)SyncAction.Completed);
            Assert.Contains(history, h => h.Action == (int)SyncAction.Rewatching);
            Assert.Contains(history, h => h.Action == (int)SyncAction.RolledBack);
            Assert.Contains(history, h => h.Action == (int)SyncAction.AddedToList);
            Assert.Contains(history, h => h.Action == (int)SyncAction.Updated);
        }

        [Fact]
        public async Task LogSync_HandlesDifferentStatuses()
        {
            // Arrange
            var username = "testuser";
            var statuses = new[] { Status.Watching, Status.Completed, Status.On_hold, Status.Dropped, Status.Plan_to_watch };
            
            // Act
            int id = 1;
            foreach (var status in statuses)
            {
                _syncHistoryManager.LogSync(username, id++, $"Anime {id}", 1, "Watched", true, status);
            }
            
            await Task.Delay(300);
            
            // Assert
            var history = await _syncHistoryManager.GetUserHistoryAsync(username);
            Assert.NotNull(history);
            Assert.Equal(statuses.Length, history.Count);
            
            // Verify each status was stored correctly
            foreach (var status in statuses)
            {
                Assert.Contains(history, h => h.Status == (int)status);
            }
        }

        [Fact]
        public async Task GetUserStatsAsync_ReturnsNullForNonExistentUser()
        {
            // Act
            var stats = await _syncHistoryManager.GetUserStatsAsync("nonexistentuser");
            
            // Assert
            Assert.Null(stats);
        }

        [Fact]
        public async Task GetUserHistoryAsync_ReturnsEmptyForNonExistentUser()
        {
            // Act
            var history = await _syncHistoryManager.GetUserHistoryAsync("nonexistentuser");
            
            // Assert
            Assert.NotNull(history);
            Assert.Empty(history);
        }

        [Fact]
        public async Task History_MaintainsMaxEntries()
        {
            // Arrange
            var username = "testuser";
            
            // Add more than 1000 entries (the max limit in UserHistory.AddEntry)
            for (int i = 1; i <= 1005; i++)
            {
                _syncHistoryManager.LogSync(username, i, $"Anime {i}", 1, "Watched", true, Status.Watching);
            }
            
            await Task.Delay(500);
            
            // Act
            var stats = await _syncHistoryManager.GetUserStatsAsync(username);
            
            // Assert
            Assert.NotNull(stats);
            Assert.Equal(1005, stats.TotalSyncs); // Total count is maintained
            Assert.Equal(1000, stats.History.Count); // But only 1000 entries are kept
        }

        [Fact]
        public async Task LogSync_HandlesNullAnimeId()
        {
            // Arrange
            var username = "testuser";
            
            // Act
            _syncHistoryManager.LogSync(username, null, "Unknown Anime", 1, "Watched", true, Status.Watching);
            
            await Task.Delay(100);
            
            // Assert
            var history = await _syncHistoryManager.GetUserHistoryAsync(username);
            Assert.NotNull(history);
            Assert.Single(history);
            Assert.Null(history[0].AnimeId);
        }

        [Fact]
        public async Task LogSync_HandlesMultipleProviders()
        {
            // Arrange
            var username = "testuser";
            var providers = new[] { "MAL", "AniList", "Kitsu", "Annict", "Shikimori" };
            
            // Act
            int id = 1;
            foreach (var provider in providers)
            {
                _syncHistoryManager.LogSync(username, id++, $"Anime {id}", 1, "Watched", true, Status.Watching, provider);
            }
            
            await Task.Delay(300);
            
            // Assert
            var history = await _syncHistoryManager.GetUserHistoryAsync(username);
            Assert.NotNull(history);
            Assert.Equal(providers.Length, history.Count);
            
            // Verify each provider was stored correctly
            foreach (var provider in providers)
            {
                Assert.Contains(history, h => h.Provider.Name == provider);
            }
        }

        [Fact]
        public async Task GetStatsAsync_CalculatesSuccessRateCorrectly()
        {
            // Arrange
            var username = "testuser";
            
            // Add 3 successful and 2 failed syncs
            _syncHistoryManager.LogSync(username, 1, "Anime 1", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync(username, 2, "Anime 2", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync(username, 3, "Anime 3", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync(username, 4, "Anime 4", 1, "Watched", false, Status.Watching);
            _syncHistoryManager.LogSync(username, 5, "Anime 5", 1, "Watched", false, Status.Watching);
            
            await Task.Delay(200);
            
            // Act
            var stats = await _syncHistoryManager.GetUserStatsAsync(username);
            
            // Assert
            Assert.NotNull(stats);
            Assert.Equal(60.0, stats.SuccessRate); // 3/5 = 60%
        }

        [Fact]
        public async Task History_PreservesChronologicalOrder()
        {
            // Arrange
            var username = "testuser";
            var timestamps = new DateTime[3];
            
            // Act - Add entries with small delays to ensure different timestamps
            for (int i = 0; i < 3; i++)
            {
                _syncHistoryManager.LogSync(username, i + 1, $"Anime {i + 1}", i + 1, "Watched", true, Status.Watching);
                await Task.Delay(50);
                timestamps[i] = DateTime.Now;
            }
            
            await Task.Delay(200);
            
            // Assert
            var history = await _syncHistoryManager.GetUserHistoryAsync(username);
            Assert.Equal(3, history.Count);
            
            // Most recent should be first
            Assert.Equal(3, history[0].AnimeId);
            Assert.Equal(2, history[1].AnimeId);
            Assert.Equal(1, history[2].AnimeId);
        }

        [Fact]
        public async Task LogSync_HandlesEmptyUsername()
        {
            // Arrange & Act
            _syncHistoryManager.LogSync("", 1, "Anime", 1, "Watched", true, Status.Watching);
            
            await Task.Delay(100);
            
            // Assert
            var stats = await _syncHistoryManager.GetUserStatsAsync("");
            Assert.NotNull(stats);
            Assert.Equal(1, stats.TotalSyncs);
        }

        [Fact]
        public async Task GetStatsAsync_GroupsByAction()
        {
            // Arrange
            var username = "testuser";
            
            _syncHistoryManager.LogSync(username, 1, "Anime 1", 1, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync(username, 2, "Anime 2", 2, "Watched", true, Status.Watching);
            _syncHistoryManager.LogSync(username, 3, "Anime 3", 12, "Completed", true, Status.Completed);
            _syncHistoryManager.LogSync(username, 4, "Anime 4", 0, "Unwatched", true, Status.Watching);
            
            await Task.Delay(200);
            
            // Act
            var stats = await _syncHistoryManager.GetStatsAsync(username);
            
            // Assert
            Assert.NotNull(stats);
            Assert.Equal(2, stats.SyncsByAction["Watched"]);
            Assert.Equal(1, stats.SyncsByAction["Completed"]);
            Assert.Equal(1, stats.SyncsByAction["Unwatched"]);
        }

        [Fact]
        public async Task ConcurrentWrites_AreHandledSafely()
        {
            // Arrange
            var username = "testuser";
            var tasks = new Task[10];

            // Act - Fire off multiple concurrent writes
            for (int i = 0; i < 10; i++)
            {
                var id = i;
                tasks[i] = Task.Run(() =>
                    _syncHistoryManager.LogSync(username, id, $"Anime {id}", id, "Watched", true, Status.Watching)
                );
            }

            await Task.WhenAll(tasks);
            await Task.Delay(500);

            // Assert
            var stats = await _syncHistoryManager.GetUserStatsAsync(username);
            Assert.NotNull(stats);
            Assert.Equal(10, stats.TotalSyncs);
        }

    }
}
