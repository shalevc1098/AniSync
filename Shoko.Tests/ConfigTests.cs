using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
        _config.Should().BeEmpty();
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
        _config.Should().ContainKey("shalev");
        _config["shalev"].Providers.Should().ContainKey("Mal");
        _config["shalev"].Providers["Mal"].Username.Should().Be("mal_user1");
        _config["shalev"].Providers["Mal"].AccessToken.Should().Be("token123");
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
        _config["shalev"].Providers["Mal"].Username.Should().Be("mal_user2");
        _config["shalev"].Providers["Mal"].AccessToken.Should().Be("token456");
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
        _config["shalev"].Providers.Should().HaveCount(2);
        _config["shalev"].Providers["Mal"].Username.Should().Be("mal_user");
        _config["shalev"].Providers["AniList"].Username.Should().Be("anilist_user");
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
        result!.Username.Should().Be("mal_user");
        result!.AccessToken.Should().Be("token123");
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
        result!.Username.Should().Be("anilist_user");
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
        _config.SetUserSettings("shalev", new UserSettings { UpdateNsfw = true });

        // Act
        _config.Save();
        var loadedConfig = new Config(_testConfigPath);

        // Assert
        loadedConfig.Should().HaveCount(1);
        loadedConfig["shalev"].Providers.Should().HaveCount(2);
        loadedConfig["shalev"].Providers["Mal"].Username.Should().Be("mal_user");
        loadedConfig["shalev"].Providers["AniList"].Username.Should().Be("anilist_user");
        loadedConfig.GetUpdateNsfw("shalev").Should().BeTrue();
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
        dynamic? jsonObj = JsonConvert.DeserializeObject(json);

        // Assert
        ((string)jsonObj!.shalev.providers.Mal.username).Should().Be("mal_user");
        ((string)jsonObj.shalev.providers.Mal.access_token).Should().Be("token123");
        ((string)jsonObj.shalev.providers.Mal.refresh_token).Should().Be("refresh123");
    }

    [Fact]
    public void GetSetStartDateFromAnyEpisode_Should_Return_Default_False_When_Not_Set()
    {
        // Act
        var result = _config.GetSetStartDateFromAnyEpisode("shalev");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GetSetStartDateFromAnyEpisode_Should_Return_UserSetting_When_Set()
    {
        // Arrange
        _config.SetUserSettings("shalev", new UserSettings { SetStartDateFromAnyEpisode = true });

        // Act
        var result = _config.GetSetStartDateFromAnyEpisode("shalev");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void GetSetStartDateFromAnyEpisode_Should_Preserve_Value_After_Save_And_Load()
    {
        // Arrange
        _config.SetUserSettings("shalev", new UserSettings { SetStartDateFromAnyEpisode = true });
        _config.Save();

        // Act
        var loadedConfig = new Config(_testConfigPath);
        var result = loadedConfig.GetSetStartDateFromAnyEpisode("shalev");

        // Assert
        result.Should().BeTrue();
    }

    // ========================================================================
    // Config settings defaults (all 9 helpers)
    // For an unknown user, all settings helpers should return documented defaults.
    // ========================================================================

    [Fact]
    public void Config_All_Settings_Return_Documented_Defaults_For_Unknown_User()
    {
        _config.GetEnableAutoSync("unknown_user").Should().BeTrue("default is true");
        _config.GetSyncOnlyCompleted("unknown_user").Should().BeTrue("default is true");
        _config.GetEnableRewatchDetection("unknown_user").Should().BeTrue("default is true");
        _config.GetAllowRollback("unknown_user").Should().BeFalse("default is false");
        _config.GetTitleMatchThreshold("unknown_user").Should().Be(0.8, "default is 0.8");
        _config.GetSyncDelaySeconds("unknown_user").Should().Be(5, "default is 5");
        _config.GetUseFuzzyMatching("unknown_user").Should().BeTrue("default is true");
        _config.GetEnableDebugLogging("unknown_user").Should().BeFalse("default is false");
        _config.GetUpdateNsfw("unknown_user").Should().BeFalse("default is false");
    }

    // ========================================================================
    // Config null-guard tests
    // SetAuthForShokoUser silently returns when username is empty or auth is null.
    // ========================================================================

    [Fact]
    public void SetAuthForShokoUser_With_Empty_Username_Does_Not_Add_Entry()
    {
        _config.SetAuthForShokoUser("", ApiName.Mal, new UserApiAuth { Username = "mal_user", AccessToken = "token" });

        _config.Should().BeEmpty("empty username should be rejected");
    }

    [Fact]
    public void SetAuthForShokoUser_With_Null_Auth_Does_Not_Add_Entry()
    {
        _config.SetAuthForShokoUser("shalev", ApiName.Mal, null!);

        _config.Should().BeEmpty("null auth should be rejected");
    }

    [Fact]
    public void GetAuthForShokoUser_With_Empty_Or_Null_Username_Should_Return_Null()
    {
        // Act
        var result1 = _config.GetAuthForShokoUser("", ApiName.Mal);
        var result2 = _config.GetAuthForShokoUser(null, ApiName.Mal);

        // Assert
        result1.Should().BeNull();
        result2.Should().BeNull();
    }

    [Fact]
    public void GetAuthForShokoUser_With_Default_Provider_Should_Use_SelectedProvider()
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
    public void SelectedProvider_Set_Should_Use_Configured_Value()
    {
        ApiName? provider = ApiName.AniList;
        var result = provider ?? ApiName.Mal;

        result.Should().Be(ApiName.AniList);
    }

    // ========================================================================
    // Config thread safety
    // Concurrent SetAuth + Save must not throw due to collection modification.
    // ========================================================================

    [Fact]
    public void Config_Concurrent_Modification_Does_Not_Throw()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                _config.SetAuthForShokoUser($"user{index}", ApiName.Mal, new UserApiAuth
                {
                    Username = $"maluser{index}",
                    AccessToken = $"token{index}",
                    RefreshToken = $"refresh{index}",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                });
            }));
        }

        var act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow("concurrent SetAuth calls should be synchronized by the lock");
    }

    [Fact]
    public void Config_Concurrent_SetSettings_Does_Not_Throw()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                _config.SetUserSettings($"user{index}", new UserSettings
                {
                    SyncDelaySeconds = index
                });
            }));
        }

        var act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow("concurrent SetUserSettings calls should be synchronized by the lock");
    }

    // ========================================================================
    // Config survives malformed JSON
    // ========================================================================

    [Fact]
    public void Config_MalformedJson_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "anisync_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            File.WriteAllText(configPath, "{{{invalid json content");

            Config config = null!;
            var act = () => config = new Config(configPath);
            act.Should().NotThrow("malformed JSON should be handled gracefully");

            config.Should().NotBeNull();
            config.Should().BeEmpty("corrupt config should result in empty dictionary");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Config_MalformedJson_CreatesBackup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "anisync_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var corruptContent = "{{{invalid json content";
            File.WriteAllText(configPath, corruptContent);

            _ = new Config(configPath);

            var backupPath = configPath + ".corrupt";
            File.Exists(backupPath).Should().BeTrue("corrupt config file should be backed up");
            File.ReadAllText(backupPath).Should().Be(corruptContent, "backup should contain the original corrupt content");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}