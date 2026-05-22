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
    /// History entry for per-user storage
    /// </summary>
    public class HistoryEntry
    {
        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;

        [JsonPropertyName("action")]
        public int Action { get; set; }

        [JsonPropertyName("anime_id")]
        public int? AnimeId { get; set; }

        [JsonPropertyName("anime_title")]
        public string AnimeTitle { get; set; } = string.Empty;
        
        [JsonPropertyName("anime_image")]
        public string? AnimeImage { get; set; }

        [JsonPropertyName("episode_number")]
        public int? EpisodeNumber { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

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
            History.Insert(0, entry);
            TotalSyncs++;
            if (!entry.Success)
            {
                FailedSyncs++;
            }
            LastSync = entry.Timestamp;

            if (History.Count > 1000)
            {
                History = History.Take(1000).ToList();
            }
        }
    }

    /// <summary>
    /// Sync history statistics model
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