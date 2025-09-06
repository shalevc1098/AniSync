using System;
using System.Collections.Generic;

namespace Shoko.AniSync.Models.ViewModels
{
    public class DashboardViewModel
    {
        public bool IsAuthenticated { get; set; }
        public string Username { get; set; } = string.Empty;
        public int? TotalAnime { get; set; }
        public int? SyncedAnime { get; set; }
        public string LastSyncTime { get; set; } = string.Empty;
        public int? PendingUpdates { get; set; }
        public List<SyncActivity> RecentActivity { get; set; } = new List<SyncActivity>();
    }
    
    public class SyncActivity
    {
        public string Time { get; set; } = string.Empty;
        public string AnimeName { get; set; } = string.Empty;
        public int Episode { get; set; }
        public bool Success { get; set; }
    }
}