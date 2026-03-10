using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Shoko.Abstractions.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Configuration
{
    // Root config is now just a dictionary of users
    public class Config : Dictionary<string, UserConfig>
    {
        [JsonIgnore]
        private readonly string _filePath;

        [JsonIgnore]
        private readonly object _configLock = new object();

        // Global default provider (for backward compatibility)
        [JsonIgnore]
        public ApiName SelectedProvider { get; set; } = ApiName.Mal;
        
        // Get all authenticated users from the new config structure  
        public List<UserApiAuth> GetAuthenticatedUsers()
        {
            var allAuths = new List<UserApiAuth>();
            
            foreach (var kvp in this)
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
            if (this.TryGetValue(username, out var userConfig))
            {
                return userConfig;
            }
            return null;
        }

        // Set user config object for a specific user
        private void SetUserConfig(string username, UserConfig config)
        {
            this[username] = config;
        }

        // Get auth for a specific Shoko user and provider
        public UserApiAuth? GetAuthForShokoUser(string? shokoUsername, ApiName? provider = null)
        {
            if (string.IsNullOrEmpty(shokoUsername)) return null;

            var userConfig = GetUserConfig(shokoUsername);
            if (userConfig?.Providers != null)
            {
                // Use the user's selected provider if no specific provider is requested
                var providerName = provider?.ToString() ?? userConfig.SelectedProvider ?? "Mal";
                
                if (userConfig.Providers.TryGetValue(providerName, out var providerAuth))
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

            lock (_configLock)
            {
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
                Save();
            }
        }
        
        // Set user settings for a Shoko user
        public void SetUserSettings(string shokoUsername, UserSettings settings)
        {
            if (string.IsNullOrEmpty(shokoUsername)) return;

            lock (_configLock)
            {
                var userConfig = GetUserConfig(shokoUsername) ?? new UserConfig();
                userConfig.Settings = settings ?? new UserSettings();

                SetUserConfig(shokoUsername, userConfig);
                Save();
            }
        }
        

        public Config(string filePath) : base()
        {
            _filePath = filePath;
            EnsureDirectoryExists();

            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, UserConfig>>(json);
                    if (data != null)
                    {
                        foreach (var kvp in data)
                        {
                            this[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Back up corrupt file and start fresh so plugin can still load
                    var backupPath = _filePath + ".corrupt";
                    try { File.Copy(_filePath, backupPath, overwrite: true); } catch { }
                    System.Diagnostics.Debug.WriteLine($"Config file corrupt, backed up to {backupPath}: {ex.Message}");
                }
            }
            else
            {
                // Create empty config file
                Save();
            }
        }

        public void Save()
        {
            lock (_configLock)
            {
                // Create a clean dictionary for serialization (without the _filePath)
                var dataToSave = new Dictionary<string, UserConfig>(this);
                var json = JsonConvert.SerializeObject(dataToSave, Formatting.Indented);
                // Write to temp file first, then rename atomically to prevent corruption on crash
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
        }

        private void EnsureDirectoryExists()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        
        // Static helper methods for easy access
        public static Config GetConfig(IApplicationPaths applicationPaths)
        {
            var configPath = Path.Combine(applicationPaths.PluginsPath, "AniSync", "config.json");
            return new Config(configPath);
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
