using System;
using System.Collections.Generic;

namespace Shoko.AniSync.Models.ViewModels
{
    public class HistoryViewModel
    {
        public bool IsAuthenticated { get; set; }
        public string Username { get; set; } = string.Empty;
        public List<SyncHistoryEntry> SyncHistory { get; set; } = new List<SyncHistoryEntry>();
        public int TotalSyncs { get; set; }
        public int FailedSyncs { get; set; }
        public string SuccessRate { get; set; } = string.Empty;
        public string LastSync { get; set; } = string.Empty;
    }
    
    public class SyncHistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public string AnimeName { get; set; } = string.Empty;
        public int Episode { get; set; }
        public string Action { get; set; } = string.Empty; // watched, unwatched, completed, rewatch
        public bool Success { get; set; }
        public string? Details { get; set; }
        public string? ErrorMessage { get; set; }
    }
}