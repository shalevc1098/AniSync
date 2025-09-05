using System;
using System.Collections.Generic;

namespace Shoko.AniSync.Models
{
    public class SyncHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string AnimeName { get; set; }
        public int? AnimeId { get; set; }
        public string Action { get; set; } // "Watched", "Unwatched", "Completed", "Rewatching", etc.
        public int EpisodeNumber { get; set; }
        public int TotalEpisodes { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string ShokoUsername { get; set; }
        public string MalUsername { get; set; }
        public string Provider { get; set; } = "MAL";
        public string Details { get; set; } // Additional details like "Set start date", "Set end date", etc.
    }

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