using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Newtonsoft.Json;
using Shoko.AniSync.Configuration;
using Xunit;

namespace Shoko.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _testConfigPath;
    private readonly Config _config;

    public ConfigTests()
    {
        _testConfigPath = Path.Combine(Path.GetTempPath(), $"test-config-{Guid.NewGuid()}.json");
        _config = new Config(_testConfigPath);
    }

    public void Dispose()
    {
        if (File.Exists(_testConfigPath))
        {
            File.Delete(_testConfigPath);
        }
    }

    [Fact]
    public void Constructor_Should_Create_Empty_Config_When_File_Does_Not_Exist()
    {
        // Arrange & Act - done in constructor

        // Assert
        _config.Should().NotBeNull();
        _config.Auths.Should().NotBeNull();
        _config.Auths.Should().BeEmpty();
        File.Exists(_testConfigPath).Should().BeTrue();
    }

    [Fact]
    public void SetAuthForShokoUser_Should_Add_Auth_For_New_User()
    {
        // Arrange
        var auth = new UserApiAuth
        {
            Username = "mal_user1",
            AccessToken = "token123",
            RefreshToken = "refresh123"
        };

        // Act
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, auth);

        // Assert
        _config.Auths.Should().ContainKey("shalev");
        _config.Auths["shalev"].Should().ContainKey(ApiName.Mal);
        _config.Auths["shalev"][ApiName.Mal].Username.Should().Be("mal_user1");
        _config.Auths["shalev"][ApiName.Mal].AccessToken.Should().Be("token123");
    }

    [Fact]
    public void SetAuthForShokoUser_Should_Replace_Existing_Auth_For_Same_Provider()
    {
        // Arrange
        var auth1 = new UserApiAuth
        {
            Username = "mal_user1",
            AccessToken = "token123",
            RefreshToken = "refresh123"
        };
        var auth2 = new UserApiAuth
        {
            Username = "mal_user2",
            AccessToken = "token456",
            RefreshToken = "refresh456"
        };

        // Act
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, auth1);
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, auth2);

        // Assert
        _config.Auths["shalev"][ApiName.Mal].Username.Should().Be("mal_user2");
        _config.Auths["shalev"][ApiName.Mal].AccessToken.Should().Be("token456");
    }

    [Fact]
    public void SetAuthForShokoUser_Should_Support_Multiple_Providers_Per_User()
    {
        // Arrange
        var malAuth = new UserApiAuth
        {
            Username = "mal_user",
            AccessToken = "mal_token",
            RefreshToken = "mal_refresh"
        };
        var anilistAuth = new UserApiAuth
        {
            Username = "anilist_user",
            AccessToken = "anilist_token",
            RefreshToken = "anilist_refresh"
        };

        // Act
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, malAuth);
        _config.SetAuthForShokoUser("shalev", ApiName.AniList, anilistAuth);

        // Assert
        _config.Auths["shalev"].Should().HaveCount(2);
        _config.Auths["shalev"][ApiName.Mal].Username.Should().Be("mal_user");
        _config.Auths["shalev"][ApiName.AniList].Username.Should().Be("anilist_user");
    }

    [Fact]
    public void GetAuthForShokoUser_Should_Return_Auth_For_Selected_Provider()
    {
        // Arrange
        var auth = new UserApiAuth
        {
            Username = "mal_user",
            AccessToken = "token123",
            RefreshToken = "refresh123"
        };
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, auth);
        _config.SelectedProvider = ApiName.Mal;

        // Act
        var result = _config.GetAuthForShokoUser("shalev");

        // Assert
        result.Should().NotBeNull();
        result.Username.Should().Be("mal_user");
        result.AccessToken.Should().Be("token123");
    }

    [Fact]
    public void GetAuthForShokoUser_Should_Return_Null_When_User_Not_Found()
    {
        // Act
        var result = _config.GetAuthForShokoUser("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetAuthForShokoUser_Should_Return_Specific_Provider_When_Specified()
    {
        // Arrange
        var malAuth = new UserApiAuth
        {
            Username = "mal_user",
            AccessToken = "mal_token"
        };
        var anilistAuth = new UserApiAuth
        {
            Username = "anilist_user",
            AccessToken = "anilist_token"
        };
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, malAuth);
        _config.SetAuthForShokoUser("shalev", ApiName.AniList, anilistAuth);
        _config.SelectedProvider = ApiName.Mal; // Default is MAL

        // Act
        var result = _config.GetAuthForShokoUser("shalev", ApiName.AniList);

        // Assert
        result.Should().NotBeNull();
        result.Username.Should().Be("anilist_user");
    }

    [Fact]
    public void GetAuthenticatedUsers_Should_Return_All_Users_Across_All_Shoko_Users()
    {
        // Arrange
        var auth1 = new UserApiAuth { Username = "mal_user1", AccessToken = "token1" };
        var auth2 = new UserApiAuth { Username = "mal_user2", AccessToken = "token2" };
        var auth3 = new UserApiAuth { Username = "anilist_user", AccessToken = "token3" };

        _config.SetAuthForShokoUser("shalev", ApiName.Mal, auth1);
        _config.SetAuthForShokoUser("family", ApiName.Mal, auth2);
        _config.SetAuthForShokoUser("family", ApiName.AniList, auth3);

        // Act
        var users = _config.GetAuthenticatedUsers();

        // Assert
        users.Should().HaveCount(3);
        users.Should().Contain(u => u.Username == "mal_user1");
        users.Should().Contain(u => u.Username == "mal_user2");
        users.Should().Contain(u => u.Username == "anilist_user");
    }




    [Fact]
    public void Save_And_Load_Should_Preserve_Nested_Dictionary_Structure()
    {
        // Arrange
        var auth1 = new UserApiAuth { Username = "mal_user", AccessToken = "token1", RefreshToken = "refresh1" };
        var auth2 = new UserApiAuth { Username = "anilist_user", AccessToken = "token2", RefreshToken = "refresh2" };

        _config.SetAuthForShokoUser("shalev", ApiName.Mal, auth1);
        _config.SetAuthForShokoUser("shalev", ApiName.AniList, auth2);
        _config.UpdateNsfw = true;

        // Act
        _config.Save();
        var loadedConfig = new Config(_testConfigPath);

        // Assert
        loadedConfig.Auths.Should().HaveCount(1);
        loadedConfig.Auths["shalev"].Should().HaveCount(2);
        loadedConfig.Auths["shalev"][ApiName.Mal].Username.Should().Be("mal_user");
        loadedConfig.Auths["shalev"][ApiName.AniList].Username.Should().Be("anilist_user");
        loadedConfig.UpdateNsfw.Should().BeTrue();
    }

    [Fact]
    public void Json_Structure_Should_Match_Expected_Format()
    {
        // Arrange
        var auth = new UserApiAuth
        {
            Username = "mal_user",
            AccessToken = "token123",
            RefreshToken = "refresh123"
        };
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, auth);

        // Act
        _config.Save();
        var json = File.ReadAllText(_testConfigPath);
        dynamic jsonObj = JsonConvert.DeserializeObject(json);

        // Assert
        ((string)jsonObj.auths.shalev.Mal.username).Should().Be("mal_user");
        ((string)jsonObj.auths.shalev.Mal.access_token).Should().Be("token123");
        ((string)jsonObj.auths.shalev.Mal.refresh_token).Should().Be("refresh123");
        // ShokoUsername should NOT be in the JSON
        ((object)jsonObj.auths.shalev.Mal.shoko_username).Should().BeNull();
    }
}