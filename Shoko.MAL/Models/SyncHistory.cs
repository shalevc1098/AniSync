using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Shoko.AniSync.Models
{
    /// <summary>
    /// Provider information for sync history
    /// </summary>
    public class ProviderInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "MAL";

        [JsonPropertyName("username")]
        public string? Username { get; set; }
    }

    /// <summary>
    /// Legacy sync history entry model for backward compatibility
    /// </summary>
    public class SyncHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string AnimeName { get; set; } = string.Empty;
        public int? AnimeId { get; set; }
        public string Action { get; set; } = string.Empty; // "Watched", "Unwatched", "Completed", "Rewatching", etc.
        public int EpisodeNumber { get; set; }
        public int TotalEpisodes { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string ShokoUsername { get; set; } = string.Empty;
        public string MalUsername { get; set; } = string.Empty;
        public string Provider { get; set; } = "MAL";
        public string? ProviderUsername { get; set; }
        public string? Details { get; set; } // Additional details like "Set start date", "Set end date", etc.
        public string? AnimeImage { get; set; }
    }

    /// <summary>
    /// New simplified history entry for per-user storage
    /// </summary>
    public class HistoryEntry
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("anime_id")]
        public int? AnimeId { get; set; }

        [JsonPropertyName("anime_title")]
        public string AnimeTitle { get; set; } = string.Empty;
        
        [JsonPropertyName("anime_image")]
        public string? AnimeImage { get; set; }

        [JsonPropertyName("episodes_synced")]
        public int EpisodesSynced { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public string? Details { get; set; }

        [JsonPropertyName("provider")]
        public ProviderInfo Provider { get; set; } = new ProviderInfo();
    }

    /// <summary>
    /// Per-user history data with statistics
    /// </summary>
    public class UserHistory
    {
        [JsonPropertyName("history")]
        public List<HistoryEntry> History { get; set; } = new List<HistoryEntry>();

        [JsonPropertyName("last_sync")]
        public DateTime? LastSync { get; set; }

        [JsonPropertyName("total_syncs")]
        public int TotalSyncs { get; set; }

        [JsonPropertyName("failed_syncs")]
        public int FailedSyncs { get; set; }

        [JsonIgnore]
        public int SuccessfulSyncs => TotalSyncs - FailedSyncs;

        [JsonIgnore]
        public double SuccessRate => TotalSyncs > 0 ? (SuccessfulSyncs * 100.0 / TotalSyncs) : 0;

        /// <summary>
        /// Add a new entry and update statistics
        /// </summary>
        public void AddEntry(HistoryEntry entry)
        {
            History.Insert(0, entry); // Most recent first
            TotalSyncs++;
            if (!entry.Success)
            {
                FailedSyncs++;
            }
            LastSync = entry.Timestamp;

            // Trim history to keep only recent entries
            if (History.Count > 1000)
            {
                History = History.Take(1000).ToList();
            }
        }
    }

    /// <summary>
    /// Legacy sync history stats model for backward compatibility
    /// </summary>
    public class SyncHistoryStats
    {
        public int TotalSyncs { get; set; }
        public int SuccessfulSyncs { get; set; }
        public int FailedSyncs { get; set; }
        public double SuccessRate => TotalSyncs > 0 ? (SuccessfulSyncs * 100.0 / TotalSyncs) : 0;
        public DateTime? LastSyncTime { get; set; }
        public Dictionary<string, int> SyncsByUser { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> SyncsByAction { get; set; } = new Dictionary<string, int>();
    }
}