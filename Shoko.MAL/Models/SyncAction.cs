using System.ComponentModel;
using Shoko.AniSync.Models.Mal;

namespace Shoko.MAL.Models
{
    /// <summary>
    /// Represents the action taken during a sync operation
    /// </summary>
    public enum SyncAction
    {
        [Description("Watched")]
        Watched = 1,
        
        [Description("Unwatched")]
        Unwatched = 2,
        
        [Description("Completed")]
        Completed = 3,
        
        [Description("Rewatching")]
        Rewatching = 4,
        
        [Description("Rolled back")]
        RolledBack = 5,
        
        [Description("Added to list")]
        AddedToList = 6,
        
        [Description("Updated")]
        Updated = 7,
        
        [Description("Failed")]
        Failed = 8
    }
    
    /// <summary>
    /// Helper class for SyncAction messages
    /// </summary>
    public static class SyncActionHelper
    {
        /// <summary>
        /// Get the display action text for a SyncAction
        /// </summary>
        public static string GetActionText(SyncAction action)
        {
            return action switch
            {
                SyncAction.Watched => "Watched",
                SyncAction.Unwatched => "Unwatched",
                SyncAction.Completed => "Completed",
                SyncAction.Rewatching => "Rewatching",
                SyncAction.RolledBack => "Rolled back",
                SyncAction.AddedToList => "Added to list",
                SyncAction.Updated => "Updated",
                SyncAction.Failed => "Failed",
                _ => "Unknown"
            };
        }
        
        /// <summary>
        /// Get the success message for a SyncAction
        /// </summary>
        public static string GetSuccessMessage(SyncAction action)
        {
            return action switch
            {
                SyncAction.Unwatched => "Successfully cleared progress",
                SyncAction.Failed => "Sync failed",
                _ => "Successfully synced"
            };
        }
        
        /// <summary>
        /// Parse action string to SyncAction enum
        /// </summary>
        public static SyncAction ParseAction(string actionString)
        {
            return actionString?.ToLowerInvariant() switch
            {
                "watched" => SyncAction.Watched,
                "unwatched" => SyncAction.Unwatched,
                "completed" => SyncAction.Completed,
                "rewatching" => SyncAction.Rewatching,
                "rolled back" => SyncAction.RolledBack,
                "add to list" or "added to list" => SyncAction.AddedToList,
                "updated" => SyncAction.Updated,
                "failed" => SyncAction.Failed,
                _ => SyncAction.Updated
            };
        }
        
        /// <summary>
        /// Get the details text for a SyncAction
        /// </summary>
        public static string? GetDetailsText(SyncAction action, bool isFirstTime = false)
        {
            return action switch
            {
                SyncAction.Watched when isFirstTime => "Set start date",
                SyncAction.Completed when isFirstTime => "First time completion",
                SyncAction.Completed => "Set end date",
                SyncAction.Rewatching => "Started rewatch",
                _ => null
            };
        }
        
        /// <summary>
        /// Get the MAL status string for a Status enum
        /// </summary>
        public static string GetStatusString(Status status)
        {
            return status switch
            {
                Status.Watching => "watching",
                Status.Completed => "completed",
                Status.On_hold => "on_hold",
                Status.Dropped => "dropped",
                Status.Plan_to_watch => "plan_to_watch",
                _ => "watching"
            };
        }
    }
}