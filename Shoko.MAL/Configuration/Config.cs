using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Shoko.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Configuration
{
    public class Config
    {
        private readonly string _filePath;

        [JsonProperty("update_nsfw")]
        public bool UpdateNsfw { get; set; } = false;

        [JsonProperty("selected_provider")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ApiName SelectedProvider { get; set; } = ApiName.Mal;

        [JsonProperty("auths")]
        public Dictionary<string, Dictionary<ApiName, UserApiAuth>> Auths { get; set; } = new Dictionary<string, Dictionary<ApiName, UserApiAuth>>();
        
        
        // New settings for the web UI
        [JsonProperty("enable_auto_sync")]
        public bool EnableAutoSync { get; set; } = true;
        
        [JsonProperty("sync_only_completed")]
        public bool SyncOnlyCompleted { get; set; } = true;
        
        [JsonProperty("enable_rewatch_detection")]
        public bool EnableRewatchDetection { get; set; } = true;
        
        [JsonProperty("allow_rollback")]
        public bool AllowRollback { get; set; } = false;
        
        [JsonProperty("title_match_threshold")]
        public double TitleMatchThreshold { get; set; } = 0.8;
        
        [JsonProperty("use_fuzzy_matching")]
        public bool UseFuzzyMatching { get; set; } = true;
        
        [JsonProperty("sync_delay_seconds")]
        public int SyncDelaySeconds { get; set; } = 5;
        
        [JsonProperty("enable_debug_logging")]
        public bool EnableDebugLogging { get; set; } = false;
        
        
        // Get all authenticated users across all Shoko users
        public List<UserApiAuth> GetAuthenticatedUsers()
        {
            var allAuths = new List<UserApiAuth>();
            if (Auths != null)
            {
                foreach (var userAuths in Auths.Values)
                {
                    allAuths.AddRange(userAuths.Values);
                }
            }
            return allAuths;
        }
        
        // Get auth for a specific Shoko user and provider
        public UserApiAuth GetAuthForShokoUser(string shokoUsername, ApiName? provider = null)
        {
            if (string.IsNullOrEmpty(shokoUsername) || Auths == null) return null;
            
            var providerToUse = provider ?? SelectedProvider;
            
            if (Auths.TryGetValue(shokoUsername, out var userAuths) && userAuths != null)
            {
                if (userAuths.TryGetValue(providerToUse, out var auth))
                {
                    return auth;
                }
            }
            return null;
        }
        
        // Add or update auth for a Shoko user
        public void SetAuthForShokoUser(string shokoUsername, ApiName provider, UserApiAuth auth)
        {
            if (string.IsNullOrEmpty(shokoUsername) || auth == null) return;
            
            auth.ShokoUsername = shokoUsername;
            
            // Initialize dictionary if null
            if (Auths == null)
                Auths = new Dictionary<string, Dictionary<ApiName, UserApiAuth>>();
            
            // Initialize user's auth dictionary if doesn't exist
            if (!Auths.ContainsKey(shokoUsername))
                Auths[shokoUsername] = new Dictionary<ApiName, UserApiAuth>();
            
            // Set the auth for this provider
            Auths[shokoUsername][provider] = auth;
            
            Save();
        }
        

        public Config(string filePath)
        {
            _filePath = filePath;
            EnsureDirectoryExists();

            if (!File.Exists(_filePath))
            {
                Save();
            } else
            {
                var json = File.ReadAllText(_filePath);
                JsonConvert.PopulateObject(json, this);
            }
        }

        public void Save()
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(_filePath, json);
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
    }
}
