using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AniSync.Configuration
{
    public enum ApiName
    {
        [Display(Name = "MyAnimeList")]
        Mal,
        [Display(Name = "AniList")]
        AniList,
        [Display(Name = "Kitsu")]
        Kitsu,
        [Display(Name = "Annict")]
        Annict,
        [Display(Name = "Shikimori")]
        Shikimori,
        [Display(Name = "Simkl")]
        Simkl
    }

    public class UserApiAuth
    {
        /// <summary>
        /// Username of the authenticated user.
        /// </summary>
        [JsonProperty("username")]
        public string Username { get; set; } = string.Empty;
        
        /// <summary>
        /// Access token of the authenticated instance.
        /// </summary>
        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = string.Empty;
        
        /// <summary>
        /// Refresh token of the authenticated instance.
        /// </summary>
        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;
        
        /// <summary>
        /// The Shoko username associated with this MAL account.
        /// Not serialized to JSON since it's the key in the dictionary.
        /// </summary>
        [JsonIgnore]
        public string ShokoUsername { get; set; } = string.Empty;
        
        /// <summary>
        /// When the access token expires (Unix timestamp)
        /// </summary>
        [JsonProperty("expires_at")]
        public long? ExpiresAt { get; set; }

        [JsonProperty("update_nsfw")]
        public bool? UpdateNsfw { get; set; }
        
        [JsonProperty("enable_auto_sync")]
        public bool? EnableAutoSync { get; set; }
        
        [JsonProperty("sync_only_completed")]
        public bool? SyncOnlyCompleted { get; set; }
        
        [JsonProperty("enable_rewatch_detection")]
        public bool? EnableRewatchDetection { get; set; }
        
        [JsonProperty("allow_rollback")]
        public bool? AllowRollback { get; set; }
        
        [JsonProperty("title_match_threshold")]
        public double? TitleMatchThreshold { get; set; }
        
        [JsonProperty("use_fuzzy_matching")]
        public bool? UseFuzzyMatching { get; set; }
        
        [JsonProperty("sync_delay_seconds")]
        public int? SyncDelaySeconds { get; set; }
        
        [JsonProperty("enable_debug_logging")]
        public bool? EnableDebugLogging { get; set; }
    }
}
