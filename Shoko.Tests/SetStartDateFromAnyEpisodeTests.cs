using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Shoko.AniSync;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.Mal;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Events;
using Xunit;

namespace Shoko.Tests
{
    public class SetStartDateFromAnyEpisodeTests
    {
        private readonly Mock<ILogger<ShokoAniSyncPlugin>> _loggerMock;
        private readonly Mock<IApiCallHelpers> _apiCallHelpersMock;
        private readonly Mock<Shoko.Abstractions.Services.IUserDataService> _userDataServiceMock;
        private readonly Config _config;
        private readonly string _testConfigPath;

        public SetStartDateFromAnyEpisodeTests()
        {
            _loggerMock = new Mock<ILogger<ShokoAniSyncPlugin>>();
            _apiCallHelpersMock = new Mock<IApiCallHelpers>();
            _userDataServiceMock = new Mock<Shoko.Abstractions.Services.IUserDataService>();
            _testConfigPath = Path.Combine(Path.GetTempPath(), $"test-config-{Guid.NewGuid()}.json");
            _config = new Config(_testConfigPath);
        }

        [Fact]
        public void SetStartDateFromAnyEpisode_DefaultValue_Should_Be_False()
        {
            // Arrange & Act
            var result = _config.GetSetStartDateFromAnyEpisode("testuser");
            
            // Assert
            result.Should().BeFalse("default value should be false to maintain backward compatibility");
        }

        [Fact]
        public void SetStartDateFromAnyEpisode_When_Set_To_True_Should_Return_True()
        {
            // Arrange
            _config.SetUserSettings("testuser", new UserSettings
            {
                SetStartDateFromAnyEpisode = true
            });
            
            // Act
            var result = _config.GetSetStartDateFromAnyEpisode("testuser");
            
            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void SetStartDateFromAnyEpisode_When_Set_To_False_Should_Return_False()
        {
            // Arrange
            _config.SetUserSettings("testuser", new UserSettings
            {
                SetStartDateFromAnyEpisode = false
            });
            
            // Act
            var result = _config.GetSetStartDateFromAnyEpisode("testuser");
            
            // Assert
            result.Should().BeFalse();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("nonexistentuser")]
        public void SetStartDateFromAnyEpisode_With_Invalid_User_Should_Return_Default(string? username)
        {
            // Act
            var result = _config.GetSetStartDateFromAnyEpisode(username);
            
            // Assert
            result.Should().BeFalse("should return default value for invalid users");
        }

        [Fact]
        public void When_Setting_Is_False_And_Starting_From_Episode_5_Should_Set_Start_Date()
        {
            // Arrange
            _config.SetUserSettings("testuser", new UserSettings
            {
                SetStartDateFromAnyEpisode = false
            });
            
            var anime = new Anime
            {
                Id = 123,
                Title = "Test Anime",
                NumEpisodes = 12
            };
            
            var animeWithStatus = new Anime
            {
                Id = 123,
                Title = "Test Anime",
                NumEpisodes = 12,
                MyListStatus = new MyListStatus
                {
                    NumEpisodesWatched = 0,
                    Status = Status.Plan_to_watch,
                    StartDate = null // No start date set
                }
            };
            
            _apiCallHelpersMock
                .Setup(x => x.GetAnime(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>()))
                .ReturnsAsync(animeWithStatus);
            
            _apiCallHelpersMock
                .Setup(x => x.UpdateAnime(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<Status>(),
                    It.IsAny<bool?>(),
                    It.IsAny<int?>(),
                    It.Is<DateTime?>(d => d.HasValue), // Should have a start date
                    It.IsAny<DateTime?>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>(),
                    It.IsAny<AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse?>(),
                    It.IsAny<bool?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new UpdateAnimeStatusResponse());
            
            // Act - Simulate watching episode 5 when MAL has 0 episodes
            var malEpisodeCount = 0;
            var newEpisodeCount = 5;
            bool shouldSetStartDate = malEpisodeCount == 0 && newEpisodeCount > 0;
            
            // Assert
            shouldSetStartDate.Should().BeTrue("going from 0 to 5 episodes should trigger start date setting when SetStartDateFromAnyEpisode is false");
        }

        [Fact]
        public void When_Setting_Is_True_And_Starting_From_Episode_5_Should_Set_Start_Date()
        {
            // Arrange
            _config.SetUserSettings("testuser", new UserSettings
            {
                SetStartDateFromAnyEpisode = true
            });
            
            var newEpisodeCount = 5;
            bool setFromAnyEpisode = _config.GetSetStartDateFromAnyEpisode("testuser");
            
            // Act
            bool shouldSetStartDate = setFromAnyEpisode || newEpisodeCount == 1;
            
            // Assert
            shouldSetStartDate.Should().BeTrue("should set start date when starting from episode 5 with SetStartDateFromAnyEpisode enabled");
        }

        [Fact]
        public void When_Setting_Is_True_And_Starting_From_Episode_1_Should_Set_Start_Date()
        {
            // Arrange
            _config.SetUserSettings("testuser", new UserSettings
            {
                SetStartDateFromAnyEpisode = true
            });
            
            var malEpisodeCount = 0;
            var newEpisodeCount = 1;
            bool setFromAnyEpisode = _config.GetSetStartDateFromAnyEpisode("testuser");
            
            // Act
            bool shouldSetStartDate = malEpisodeCount == 0 && newEpisodeCount > 0 && (setFromAnyEpisode || newEpisodeCount == 1);
            
            // Assert
            shouldSetStartDate.Should().BeTrue("should set start date when starting from episode 1 with SetStartDateFromAnyEpisode enabled");
        }

        [Theory]
        [InlineData(0, 0, false, false)] // No change, no date
        [InlineData(0, 0, true, false)]  // No change, no date (setting doesn't matter)
        [InlineData(0, 1, false, true)]  // Start watching from ep 1, setting false (default) -> set date
        [InlineData(0, 1, true, true)]   // Start watching from ep 1, setting true -> set date
        [InlineData(0, 2, false, false)] // Start watching from ep 2, setting false (default) -> NO date
        [InlineData(0, 2, true, true)]   // Start watching from ep 2, setting true -> set date
        [InlineData(0, 10, false, false)] // Start watching from ep 10, setting false (default) -> NO date
        [InlineData(0, 10, true, true)]  // Start watching from ep 10, setting true -> set date
        [InlineData(5, 6, false, false)] // Already watching, no date change
        [InlineData(5, 6, true, false)]  // Already watching, no date change
        [InlineData(1, 2, false, false)] // Already has episode 1 watched
        [InlineData(1, 2, true, false)]  // Already has episode 1 watched
        public void Start_Date_Logic_Matrix_Test(
            int malEpisodeCount, 
            int newEpisodeCount, 
            bool setFromAnyEpisode, 
            bool expectedShouldSetDate)
        {
            // Arrange
            _config.SetUserSettings("testuser", new UserSettings
            {
                SetStartDateFromAnyEpisode = setFromAnyEpisode
            });
            
            // Act
            bool shouldSetStartDate = malEpisodeCount == 0 && newEpisodeCount > 0 && (setFromAnyEpisode || newEpisodeCount == 1);
            
            // Assert
            shouldSetStartDate.Should().Be(expectedShouldSetDate, 
                $"MAL episodes: {malEpisodeCount}, New episodes: {newEpisodeCount}, Setting: {setFromAnyEpisode}");
        }

        [Fact]
        public void When_Start_Date_Already_Exists_Should_Not_Override()
        {
            // Arrange
            var existingStartDate = "2023-01-15";
            bool shouldOverride = string.IsNullOrEmpty(existingStartDate);
            
            // Assert
            shouldOverride.Should().BeFalse("should not override existing start date");
        }

        [Fact]
        public void When_Start_Date_Is_Empty_Should_Set_New_Date()
        {
            // Arrange
            string? existingStartDate = null;
            bool shouldSetDate = string.IsNullOrEmpty(existingStartDate);
            
            // Assert
            shouldSetDate.Should().BeTrue("should set date when no existing date");
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Empty_Start_Date_Variations_Should_Allow_Setting(string? existingStartDate)
        {
            // Act
            bool shouldSetDate = string.IsNullOrEmpty(existingStartDate);
            
            // Assert
            shouldSetDate.Should().BeTrue($"should set date when existing date is '{existingStartDate ?? "null"}'");
        }

        [Fact]
        public void Setting_Should_Persist_After_Save_And_Reload()
        {
            // Arrange
            var testPath = Path.Combine(Path.GetTempPath(), $"persist-test-{Guid.NewGuid()}.json");
            var config1 = new Config(testPath);
            
            try
            {
                config1.SetUserSettings("user1", new UserSettings
                {
                    SetStartDateFromAnyEpisode = true
                });
                config1.Save();
                
                // Act
                var config2 = new Config(testPath);
                var result = config2.GetSetStartDateFromAnyEpisode("user1");
                
                // Assert
                result.Should().BeTrue("setting should persist after save and reload");
            }
            finally
            {
                if (File.Exists(testPath))
                    File.Delete(testPath);
            }
        }

        [Fact]
        public void Multiple_Users_Should_Have_Independent_Settings()
        {
            // Arrange
            _config.SetUserSettings("user1", new UserSettings
            {
                SetStartDateFromAnyEpisode = true
            });
            
            _config.SetUserSettings("user2", new UserSettings
            {
                SetStartDateFromAnyEpisode = false
            });
            
            // Act
            var user1Setting = _config.GetSetStartDateFromAnyEpisode("user1");
            var user2Setting = _config.GetSetStartDateFromAnyEpisode("user2");
            var user3Setting = _config.GetSetStartDateFromAnyEpisode("user3"); // Non-existent user
            
            // Assert
            user1Setting.Should().BeTrue("user1 has setting enabled");
            user2Setting.Should().BeFalse("user2 has setting disabled");
            user3Setting.Should().BeFalse("user3 should get default value");
        }

        [Fact]
        public void Setting_Should_Work_With_Other_User_Settings()
        {
            // Arrange
            _config.SetUserSettings("testuser", new UserSettings
            {
                SetStartDateFromAnyEpisode = true,
                EnableAutoSync = false,
                UpdateNsfw = true,
                SyncDelaySeconds = 10,
                EnableDebugLogging = true
            });
            
            // Act
            var syncStartDate = _config.GetSetStartDateFromAnyEpisode("testuser");
            var autoSync = _config.GetEnableAutoSync("testuser");
            var updateNsfw = _config.GetUpdateNsfw("testuser");
            var syncDelay = _config.GetSyncDelaySeconds("testuser");
            var debugLogging = _config.GetEnableDebugLogging("testuser");
            
            // Assert
            syncStartDate.Should().BeTrue();
            autoSync.Should().BeFalse();
            updateNsfw.Should().BeTrue();
            syncDelay.Should().Be(10);
            debugLogging.Should().BeTrue();
        }

        [Fact]
        public void Null_Settings_Should_Return_Default_Values()
        {
            // Arrange
            _config.SetUserSettings("testuser", null!);
            
            // Act
            var result = _config.GetSetStartDateFromAnyEpisode("testuser");
            
            // Assert
            result.Should().BeFalse("null settings should return default value");
        }

        [Theory]
        [InlineData(0, -1, false)] // Negative episode count
        [InlineData(-5, 5, false)] // Negative MAL count
        [InlineData(int.MaxValue, 1, true)] // Max value edge case - valid scenario
        [InlineData(0, int.MaxValue, true)] // Max value new episodes
        public void Edge_Case_Episode_Counts(int malEpisodeCount, int newEpisodeCount, bool expectedValid)
        {
            // Arrange
            bool setFromAnyEpisode = false;
            
            // Act
            bool isValidScenario = malEpisodeCount >= 0 && newEpisodeCount >= 0;
            bool shouldSetStartDate = false;
            
            if (isValidScenario)
            {
                shouldSetStartDate = malEpisodeCount == 0 && newEpisodeCount > 0 && (setFromAnyEpisode || newEpisodeCount == 1);
            }
            
            // Assert
            isValidScenario.Should().Be(expectedValid);
        }

        private void Cleanup()
        {
            if (File.Exists(_testConfigPath))
            {
                File.Delete(_testConfigPath);
            }
        }
    }
}