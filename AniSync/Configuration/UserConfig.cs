using Newtonsoft.Json;
using System.Collections.Generic;

namespace AniSync.Configuration
{
    public class UserConfig
    {
        [JsonProperty("providers")]
        public Dictionary<string, ProviderAuth> Providers { get; set; } = new Dictionary<string, ProviderAuth>();

        [JsonProperty("settings")]
        public UserSettings Settings { get; set; } = UserSettings.CreateWithDefaults();
    }

    public class ProviderAuth
    {
        [JsonProperty("username")]
        public string Username { get; set; } = string.Empty;

        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonProperty("expires_at")]
        public long? ExpiresAt { get; set; }
    }

    public class UserSettings
    {
        [JsonProperty("update_nsfw")]
        public bool? UpdateNsfw { get; set; } = null;

        [JsonProperty("enable_auto_sync")]
        public bool? EnableAutoSync { get; set; } = null;

        [JsonProperty("sync_only_completed")]
        public bool? SyncOnlyCompleted { get; set; } = null;

        [JsonProperty("set_start_date_from_any_episode")]
        public bool? SetStartDateFromAnyEpisode { get; set; } = null;

        [JsonProperty("enable_rewatch_detection")]
        public bool? EnableRewatchDetection { get; set; } = null;

        [JsonProperty("allow_rollback")]
        public bool? AllowRollback { get; set; } = null;

        [JsonProperty("title_match_threshold")]
        public double? TitleMatchThreshold { get; set; } = null;

        [JsonProperty("use_fuzzy_matching")]
        public bool? UseFuzzyMatching { get; set; } = null;

        [JsonProperty("sync_delay_seconds")]
        public int? SyncDelaySeconds { get; set; } = null;

        [JsonProperty("enable_debug_logging")]
        public bool? EnableDebugLogging { get; set; } = null;
        
        /// <summary>
        /// Creates UserSettings with default values instead of nulls
        /// </summary>
        public static UserSettings CreateWithDefaults()
        {
            return new UserSettings
            {
                UpdateNsfw = false,
                EnableAutoSync = true,
                SyncOnlyCompleted = true,
                SetStartDateFromAnyEpisode = false,
                EnableRewatchDetection = false,
                AllowRollback = false,
                TitleMatchThreshold = 0.8,
                UseFuzzyMatching = true,
                SyncDelaySeconds = 5,
                EnableDebugLogging = false
            };
        }
    }
}