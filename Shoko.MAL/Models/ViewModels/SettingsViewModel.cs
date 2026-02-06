namespace Shoko.AniSync.Models.ViewModels
{
    public class SettingsViewModel
    {
        public bool IsAuthenticated { get; set; }
        public string Username { get; set; } = string.Empty;
        
        // Sync settings
        public bool UpdateNsfw { get; set; } = false;
        public bool EnableAutoSync { get; set; } = true;
        public bool SyncOnlyCompleted { get; set; } = true;
        public bool SetStartDateFromAnyEpisode { get; set; } = false;
        public bool EnableRewatchDetection { get; set; } = true;
        public bool AllowRollback { get; set; } = false;
        
        // Title matching
        public double TitleMatchThreshold { get; set; } = 0.8;
        public bool UseFuzzyMatching { get; set; } = true;
        
        // Advanced
        public int SyncDelaySeconds { get; set; } = 5;
        public bool EnableDebugLogging { get; set; } = false;
        
        // Flags to indicate if using user-specific or global settings
        public bool HasUserSettings { get; set; } = false;
    }
}