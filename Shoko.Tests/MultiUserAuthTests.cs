using FluentAssertions;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Models;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Shoko.Tests
{
    public class MultiUserAuthTests : IDisposable
    {
        private readonly string _testConfigPath;
        private readonly Config _config;

        public MultiUserAuthTests()
        {
            _testConfigPath = Path.Combine(Path.GetTempPath(), $"test-multi-user-{Guid.NewGuid()}.json");
            _config = new Config(_testConfigPath);
        }

        [Fact]
        public void SetAuthForShokoUser_ShouldStoreAuthCorrectly()
        {
            // Arrange
            var shokoUsername = "shalev";
            var auth = new UserApiAuth
            {
                Username = "mal_user123",
                AccessToken = "access_token_123",
                RefreshToken = "refresh_token_123",
                ShokoUsername = shokoUsername
            };

            // Act
            _config.SetAuthForShokoUser(shokoUsername, ApiName.Mal, auth);

            // Assert
            _config.Should().ContainKey(shokoUsername);
            _config[shokoUsername].Providers.Should().ContainKey("Mal");
            var savedAuth = _config.GetAuthForShokoUser(shokoUsername, ApiName.Mal);
            savedAuth.Should().NotBeNull();
            savedAuth!.Username.Should().Be(auth.Username);
            savedAuth!.AccessToken.Should().Be(auth.AccessToken);
            savedAuth!.ShokoUsername.Should().Be(shokoUsername);
        }

        [Fact]
        public void GetAuthForShokoUser_WithValidUser_ShouldReturnAuth()
        {
            // Arrange
            var shokoUsername = "testuser";
            var auth = new UserApiAuth
            {
                Username = "mal_user",
                AccessToken = "token123"
            };
            _config.SetAuthForShokoUser(shokoUsername, ApiName.Mal, auth);

            // Act
            var result = _config.GetAuthForShokoUser(shokoUsername, ApiName.Mal);

            // Assert
            result.Should().NotBeNull();
            result!.Username.Should().Be("mal_user");
            result!.AccessToken.Should().Be("token123");
        }

        [Fact]
        public void GetAuthForShokoUser_WithInvalidUser_ShouldReturnNull()
        {
            // Act
            var result = _config.GetAuthForShokoUser("nonexistent", ApiName.Mal);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public void MultipleUsersWithDifferentProviders_ShouldWorkCorrectly()
        {
            // Arrange
            var user1 = "shalev";
            var user2 = "family";
            
            var malAuth1 = new UserApiAuth { Username = "shalev_mal", AccessToken = "mal_token1" };
            var malAuth2 = new UserApiAuth { Username = "family_mal", AccessToken = "mal_token2" };
            var anilistAuth1 = new UserApiAuth { Username = "shalev_anilist", AccessToken = "anilist_token1" };

            // Act
            _config.SetAuthForShokoUser(user1, ApiName.Mal, malAuth1);
            _config.SetAuthForShokoUser(user1, ApiName.AniList, anilistAuth1);
            _config.SetAuthForShokoUser(user2, ApiName.Mal, malAuth2);

            // Assert
            _config.Should().HaveCount(2);
            _config[user1].Providers.Should().HaveCount(2);
            _config[user2].Providers.Should().HaveCount(1);
            
            _config.GetAuthForShokoUser(user1, ApiName.Mal)!.Username.Should().Be("shalev_mal");
            _config.GetAuthForShokoUser(user1, ApiName.AniList)!.Username.Should().Be("shalev_anilist");
            _config.GetAuthForShokoUser(user2, ApiName.Mal)!.Username.Should().Be("family_mal");
            _config.GetAuthForShokoUser(user2, ApiName.AniList).Should().BeNull();
        }

        [Fact]
        public void GetAuthenticatedUsers_ShouldReturnAllUsers()
        {
            // Arrange
            _config.SetAuthForShokoUser("user1", ApiName.Mal, 
                new UserApiAuth { Username = "mal1", AccessToken = "token1" });
            _config.SetAuthForShokoUser("user1", ApiName.AniList, 
                new UserApiAuth { Username = "anilist1", AccessToken = "token2" });
            _config.SetAuthForShokoUser("user2", ApiName.Mal, 
                new UserApiAuth { Username = "mal2", AccessToken = "token3" });

            // Act
            var allUsers = _config.GetAuthenticatedUsers();

            // Assert
            allUsers.Should().HaveCount(3);
            allUsers.Should().Contain(u => u.Username == "mal1");
            allUsers.Should().Contain(u => u.Username == "anilist1");
            allUsers.Should().Contain(u => u.Username == "mal2");
        }

        [Fact]
        public void UpdateExistingAuth_ShouldOverwriteCorrectly()
        {
            // Arrange
            var shokoUsername = "testuser";
            var oldAuth = new UserApiAuth
            {
                Username = "old_mal_user",
                AccessToken = "old_token",
                RefreshToken = "old_refresh"
            };
            _config.SetAuthForShokoUser(shokoUsername, ApiName.Mal, oldAuth);

            var newAuth = new UserApiAuth
            {
                Username = "new_mal_user",
                AccessToken = "new_token",
                RefreshToken = "new_refresh"
            };

            // Act
            _config.SetAuthForShokoUser(shokoUsername, ApiName.Mal, newAuth);

            // Assert
            var result = _config.GetAuthForShokoUser(shokoUsername, ApiName.Mal);
            result.Should().NotBeNull();
            result!.Username.Should().Be("new_mal_user");
            result!.AccessToken.Should().Be("new_token");
            result!.RefreshToken.Should().Be("new_refresh");
        }

        [Fact]
        public void SaveAndLoad_ShouldPersistMultiUserAuth()
        {
            // Arrange
            _config.SetAuthForShokoUser("user1", ApiName.Mal, 
                new UserApiAuth { Username = "mal_user1", AccessToken = "token1", RefreshToken = "refresh1" });
            _config.SetAuthForShokoUser("user2", ApiName.Mal, 
                new UserApiAuth { Username = "mal_user2", AccessToken = "token2", RefreshToken = "refresh2" });
            
            // Act
            _config.Save();
            var loadedConfig = new Config(_testConfigPath);

            // Assert
            loadedConfig.Should().HaveCount(2);
            loadedConfig.GetAuthForShokoUser("user1", ApiName.Mal)!.Username.Should().Be("mal_user1");
            loadedConfig.GetAuthForShokoUser("user2", ApiName.Mal)!.Username.Should().Be("mal_user2");
        }

        [Fact]
        public void GetAuthWithDefaultProvider_ShouldUseSelectedProvider()
        {
            // Arrange
            _config.SelectedProvider = ApiName.Mal;
            _config.SetAuthForShokoUser("user1", ApiName.Mal, 
                new UserApiAuth { Username = "mal_user", AccessToken = "mal_token" });
            _config.SetAuthForShokoUser("user1", ApiName.AniList, 
                new UserApiAuth { Username = "anilist_user", AccessToken = "anilist_token" });

            // Act
            var result = _config.GetAuthForShokoUser("user1"); // No provider specified

            // Assert
            result.Should().NotBeNull();
            result!.Username.Should().Be("mal_user");
        }

        [Fact]
        public void EmptyShokoUsername_ShouldReturnNull()
        {
            // Act
            var result1 = _config.GetAuthForShokoUser("", ApiName.Mal);
            var result2 = _config.GetAuthForShokoUser(null, ApiName.Mal);

            // Assert
            result1.Should().BeNull();
            result2.Should().BeNull();
        }

        public void Dispose()
        {
            if (File.Exists(_testConfigPath))
            {
                File.Delete(_testConfigPath);
            }
        }
    }
}