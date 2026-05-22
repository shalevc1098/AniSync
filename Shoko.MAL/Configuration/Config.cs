using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;

namespace Shoko.AniSync.Configuration
{
    [Display(Name = "AniSync Configuration")]
    public class Config : INewtonsoftJsonConfiguration, IConfigurationWithMigrations
    {
        [JsonProperty("users")]
        public ConcurrentDictionary<string, UserConfig> Users { get; set; } = new();

        [Display(Name = "MAL Client ID")]
        [JsonProperty("malClientId")]
        public string? MalClientId { get; set; }

        [Display(Name = "MAL Client Secret")]
        [JsonProperty("malClientSecret")]
        public string? MalClientSecret { get; set; }

        [Display(Name = "AniList Client ID")]
        [JsonProperty("aniListClientId")]
        public string? AniListClientId { get; set; }

        [Display(Name = "AniList Client Secret")]
        [JsonProperty("aniListClientSecret")]
        public string? AniListClientSecret { get; set; }

        [JsonProperty("stateSigningKey")]
        public string? StateSigningKey { get; set; }

        public static string ApplyMigrations(string config, IApplicationPaths applicationPaths)
        {
            config = MigrateLegacyUsers(config, applicationPaths);
            config = MigrateGlobalSettings(config, applicationPaths);
            return config;
        }

        private static string MigrateLegacyUsers(string config, IApplicationPaths applicationPaths)
        {
            var oldPath = Path.Combine(applicationPaths.PluginsPath, "AniSync", "config.json");

            var isEmptyOrDefault = string.IsNullOrWhiteSpace(config)
                || config.Contains("\"users\":{}")
                || config.Contains("\"users\": {}");

            if (File.Exists(oldPath) && isEmptyOrDefault)
            {
                var oldData = File.ReadAllText(oldPath);
                if (!string.IsNullOrWhiteSpace(oldData))
                {
                    return $"{{\"users\":{oldData}}}";
                }
            }

            if (!string.IsNullOrWhiteSpace(config) && !config.Contains("\"users\"") && config.Contains("\"providers\""))
            {
                return $"{{\"users\":{config}}}";
            }

            return config;
        }

        private static string MigrateGlobalSettings(string config, IApplicationPaths applicationPaths)
        {
            var gsPath = Path.Combine(applicationPaths.PluginsPath, "AniSync", "global-settings.json");
            if (!File.Exists(gsPath)) return config;

            try
            {
                var root = string.IsNullOrWhiteSpace(config) ? new JObject() : JObject.Parse(config);
                if (root.ContainsKey("malClientId") || root.ContainsKey("aniListClientId") || root.ContainsKey("stateSigningKey"))
                    return config;

                var gs = JObject.Parse(File.ReadAllText(gsPath));
                void Copy(string from, string to)
                {
                    var v = gs[from];
                    if (v != null && v.Type != JTokenType.Null) root[to] = v;
                }
                Copy("MalClientId", "malClientId");
                Copy("MalClientSecret", "malClientSecret");
                Copy("AniListClientId", "aniListClientId");
                Copy("AniListClientSecret", "aniListClientSecret");
                Copy("StateSigningKey", "stateSigningKey");
                return root.ToString(Formatting.None);
            }
            catch
            {
                return config;
            }
        }

        // Get all authenticated users from the config
        public List<UserApiAuth> GetAuthenticatedUsers()
        {
            var allAuths = new List<UserApiAuth>();

            foreach (var kvp in Users)
            {
                var username = kvp.Key;
                var userConfig = kvp.Value;

                if (userConfig?.Providers != null)
                {
                    foreach (var provider in userConfig.Providers)
                    {
                        allAuths.Add(new UserApiAuth
                        {
                            Username = provider.Value.Username,
                            AccessToken = provider.Value.AccessToken,
                            RefreshToken = provider.Value.RefreshToken,
                            ExpiresAt = provider.Value.ExpiresAt,
                            ShokoUsername = username
                        });
                    }
                }
            }

            return allAuths;
        }

        // Get user config object for a specific user
        private UserConfig? GetUserConfig(string? username)
        {
            if (string.IsNullOrEmpty(username))
            {
                return null;
            }
            if (Users.TryGetValue(username, out var userConfig))
            {
                return userConfig;
            }
            return null;
        }

        // Set user config object for a specific user
        private void SetUserConfig(string username, UserConfig config)
        {
            Users[username] = config;
        }

        // The providers this Shoko user has connected (parsed from the per-user Providers keys).
        public List<ApiName> GetConnectedProviders(string? shokoUsername)
        {
            var result = new List<ApiName>();
            var userConfig = GetUserConfig(shokoUsername);
            if (userConfig?.Providers == null) return result;
            foreach (var key in userConfig.Providers.Keys)
            {
                if (System.Enum.TryParse<ApiName>(key, true, out var p) && System.Enum.IsDefined(typeof(ApiName), p))
                    result.Add(p);
            }
            return result;
        }

        // Get auth for a specific Shoko user and provider
        public UserApiAuth? GetAuthForShokoUser(string? shokoUsername, ApiName? provider = null)
        {
            if (string.IsNullOrEmpty(shokoUsername)) return null;

            var userConfig = GetUserConfig(shokoUsername);
            if (userConfig?.Providers != null && userConfig.Providers.Count > 0)
            {
                ProviderAuth? providerAuth;
                if (provider != null)
                    userConfig.Providers.TryGetValue(provider.ToString()!, out providerAuth);
                else
                    providerAuth = userConfig.Providers.Values.FirstOrDefault();

                if (providerAuth != null)
                {
                    return new UserApiAuth
                    {
                        Username = providerAuth.Username,
                        AccessToken = providerAuth.AccessToken,
                        RefreshToken = providerAuth.RefreshToken,
                        ExpiresAt = providerAuth.ExpiresAt,
                        ShokoUsername = shokoUsername
                    };
                }
            }

            return null;
        }

        // Add or update auth for a Shoko user
        public void SetAuthForShokoUser(string shokoUsername, ApiName provider, UserApiAuth auth)
        {
            if (string.IsNullOrEmpty(shokoUsername) || auth == null) return;

            var userConfig = GetUserConfig(shokoUsername) ?? new UserConfig();

            if (userConfig.Providers == null)
                userConfig.Providers = new Dictionary<string, ProviderAuth>();

            userConfig.Providers[provider.ToString()] = new ProviderAuth
            {
                Username = auth.Username,
                AccessToken = auth.AccessToken,
                RefreshToken = auth.RefreshToken,
                ExpiresAt = auth.ExpiresAt
            };

            SetUserConfig(shokoUsername, userConfig);
        }

        // Set user settings for a Shoko user
        public void SetUserSettings(string shokoUsername, UserSettings settings)
        {
            if (string.IsNullOrEmpty(shokoUsername)) return;

            var userConfig = GetUserConfig(shokoUsername) ?? new UserConfig();
            userConfig.Settings = settings ?? new UserSettings();

            SetUserConfig(shokoUsername, userConfig);
        }

        // Helper methods to get user-specific settings

        public bool GetUpdateNsfw(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.UpdateNsfw ?? false;
        }

        public bool GetEnableAutoSync(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.EnableAutoSync ?? true;
        }

        public bool GetSyncOnlyCompleted(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.SyncOnlyCompleted ?? true;
        }

        public bool GetEnableRewatchDetection(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.EnableRewatchDetection ?? true;
        }

        public bool GetAllowRollback(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.AllowRollback ?? false;
        }

        public double GetTitleMatchThreshold(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.TitleMatchThreshold ?? 0.8;
        }

        public bool GetUseFuzzyMatching(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.UseFuzzyMatching ?? true;
        }

        public int GetSyncDelaySeconds(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.SyncDelaySeconds ?? 5;
        }

        public bool GetEnableDebugLogging(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.EnableDebugLogging ?? false;
        }

        public bool GetSetStartDateFromAnyEpisode(string? shokoUsername)
        {
            var userConfig = GetUserConfig(shokoUsername);
            return userConfig?.Settings?.SetStartDateFromAnyEpisode ?? false;
        }
    }
}
