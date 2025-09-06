using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.AniSync.Models;

namespace Shoko.AniSync.Helpers
{
    public class SyncHistoryManager
    {
        private readonly string _historyFilePath;
        private readonly ILogger<SyncHistoryManager> _logger;
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private Dictionary<string, UserHistory> _userHistories = new Dictionary<string, UserHistory>();

        public SyncHistoryManager(string pluginPath, ILoggerFactory loggerFactory)
        {
            _historyFilePath = Path.Combine(pluginPath, "sync-history.json");
            _logger = loggerFactory.CreateLogger<SyncHistoryManager>();
            LoadHistory();
        }

        private void LoadHistory()
        {
            try
            {
                // Try to load new format first
                if (File.Exists(_historyFilePath))
                {
                    var json = File.ReadAllText(_historyFilePath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };
                    _userHistories = JsonSerializer.Deserialize<Dictionary<string, UserHistory>>(json, options) ?? new Dictionary<string, UserHistory>();
                    _logger.LogInformation("Loaded history for {UserCount} users", _userHistories.Count);
                }
                else
                {
                    _userHistories = new Dictionary<string, UserHistory>();
                    _logger.LogInformation("Created new sync history storage");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load sync history, starting with empty history");
                _userHistories = new Dictionary<string, UserHistory>();
            }
        }

        public async Task AddEntryAsync(SyncHistoryEntry entry)
        {
            await _fileLock.WaitAsync();
            try
            {
                var username = entry.ShokoUsername ?? "Unknown";
                
                // Ensure user history exists
                if (!_userHistories.ContainsKey(username))
                {
                    _userHistories[username] = new UserHistory();
                }

                // Convert legacy entry to new format
                var historyEntry = new HistoryEntry
                {
                    Timestamp = entry.Timestamp,
                    Action = entry.Action,
                    AnimeId = entry.AnimeId,
                    AnimeTitle = entry.AnimeName,
                    AnimeImage = entry.AnimeImage,
                    EpisodesSynced = entry.EpisodeNumber,
                    Status = DetermineStatus(entry.Action, entry.EpisodeNumber, entry.TotalEpisodes),
                    Success = entry.Success,
                    Message = entry.Success ? 
                        $"Successfully synced episode {entry.EpisodeNumber}" + (string.IsNullOrEmpty(entry.Details) ? "" : $" - {entry.Details}") :
                        entry.ErrorMessage ?? "Sync failed",
                    Details = entry.Details,
                    Provider = new ProviderInfo 
                    { 
                        Name = entry.Provider,
                        Username = entry.ProviderUsername ?? entry.MalUsername
                    }
                };

                _userHistories[username].AddEntry(historyEntry);
                await SaveHistoryAsync();
                _logger.LogDebug("Added sync history entry for {AnimeName} (user: {Username})", entry.AnimeName, username);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Add a new history entry directly (new format)
        /// </summary>
        public async Task AddEntryAsync(string username, HistoryEntry entry)
        {
            await _fileLock.WaitAsync();
            try
            {
                // Ensure user history exists
                if (!_userHistories.ContainsKey(username))
                {
                    _userHistories[username] = new UserHistory();
                }

                _userHistories[username].AddEntry(entry);
                await SaveHistoryAsync();
                _logger.LogDebug("Added sync history entry for {AnimeTitle} (user: {Username})", entry.AnimeTitle, username);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private string DetermineStatus(string action, int episodeNumber, int totalEpisodes)
        {
            return action.ToLowerInvariant() switch
            {
                "completed" => "completed",
                "rewatching" => "rewatching",
                "dropped" => "dropped", 
                "paused" => "on_hold",
                "plan to watch" => "plan_to_watch",
                _ => episodeNumber >= totalEpisodes ? "completed" : "watching"
            };
        }

        public async Task<List<SyncHistoryEntry>> GetHistoryAsync(int? limit = null, string? shokoUsername = null)
        {
            await _fileLock.WaitAsync();
            try
            {
                var legacyEntries = new List<SyncHistoryEntry>();

                if (string.IsNullOrEmpty(shokoUsername))
                {
                    // Return entries for all users
                    foreach (var kvp in _userHistories)
                    {
                        var username = kvp.Key;
                        var userHistory = kvp.Value;
                        
                        foreach (var entry in userHistory.History)
                        {
                            legacyEntries.Add(ConvertToLegacyEntry(entry, username));
                        }
                    }
                }
                else if (_userHistories.ContainsKey(shokoUsername))
                {
                    // Return entries for specific user
                    foreach (var entry in _userHistories[shokoUsername].History)
                    {
                        legacyEntries.Add(ConvertToLegacyEntry(entry, shokoUsername));
                    }
                }

                // Sort by timestamp (most recent first)
                legacyEntries = legacyEntries.OrderByDescending(e => e.Timestamp).ToList();

                if (limit.HasValue)
                {
                    legacyEntries = legacyEntries.Take(limit.Value).ToList();
                }

                return legacyEntries;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Get history in new format for a specific user
        /// </summary>
        public async Task<List<HistoryEntry>> GetUserHistoryAsync(string username, int? limit = null)
        {
            await _fileLock.WaitAsync();
            try
            {
                if (!_userHistories.ContainsKey(username))
                {
                    return new List<HistoryEntry>();
                }

                var history = _userHistories[username].History;
                
                if (limit.HasValue)
                {
                    history = history.Take(limit.Value).ToList();
                }

                return history;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private SyncHistoryEntry ConvertToLegacyEntry(HistoryEntry entry, string username)
        {
            return new SyncHistoryEntry
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = entry.Timestamp,
                AnimeName = entry.AnimeTitle,
                AnimeId = entry.AnimeId,
                Action = entry.Action,
                EpisodeNumber = entry.EpisodesSynced,
                TotalEpisodes = 0, // Not stored in new format
                Success = entry.Success,
                ErrorMessage = entry.Success ? null : entry.Message,
                ShokoUsername = username,
                MalUsername = username, // Assuming same for now
                Provider = entry.Provider?.Name ?? "MAL",
                ProviderUsername = entry.Provider?.Username,
                Details = entry.Details
            };
        }

        public async Task<SyncHistoryStats> GetStatsAsync(string? shokoUsername = null)
        {
            await _fileLock.WaitAsync();
            try
            {
                var stats = new SyncHistoryStats();

                if (string.IsNullOrEmpty(shokoUsername))
                {
                    // Calculate stats for all users
                    foreach (var kvp in _userHistories)
                    {
                        var username = kvp.Key;
                        var userHistory = kvp.Value;
                        
                        stats.TotalSyncs += userHistory.TotalSyncs;
                        stats.SuccessfulSyncs += userHistory.SuccessfulSyncs;
                        stats.FailedSyncs += userHistory.FailedSyncs;
                        
                        if (userHistory.LastSync.HasValue)
                        {
                            if (!stats.LastSyncTime.HasValue || userHistory.LastSync.Value > stats.LastSyncTime.Value)
                            {
                                stats.LastSyncTime = userHistory.LastSync.Value;
                            }
                        }

                        stats.SyncsByUser[username] = userHistory.TotalSyncs;

                        // Group by action
                        foreach (var entry in userHistory.History)
                        {
                            var action = entry.Action ?? "Unknown";
                            if (stats.SyncsByAction.ContainsKey(action))
                                stats.SyncsByAction[action]++;
                            else
                                stats.SyncsByAction[action] = 1;
                        }
                    }
                }
                else if (_userHistories.ContainsKey(shokoUsername))
                {
                    // Calculate stats for specific user
                    var userHistory = _userHistories[shokoUsername];
                    
                    stats.TotalSyncs = userHistory.TotalSyncs;
                    stats.SuccessfulSyncs = userHistory.SuccessfulSyncs;
                    stats.FailedSyncs = userHistory.FailedSyncs;
                    stats.LastSyncTime = userHistory.LastSync;
                    stats.SyncsByUser[shokoUsername] = userHistory.TotalSyncs;

                    // Group by action
                    foreach (var entry in userHistory.History)
                    {
                        var action = entry.Action ?? "Unknown";
                        if (stats.SyncsByAction.ContainsKey(action))
                            stats.SyncsByAction[action]++;
                        else
                            stats.SyncsByAction[action] = 1;
                    }
                }

                return stats;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Get user-specific statistics in new format
        /// </summary>
        public async Task<UserHistory?> GetUserStatsAsync(string username)
        {
            await _fileLock.WaitAsync();
            try
            {
                return _userHistories.TryGetValue(username, out var userHistory) ? userHistory : null;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task ClearHistoryAsync(string? shokoUsername = null)
        {
            await _fileLock.WaitAsync();
            try
            {
                if (string.IsNullOrEmpty(shokoUsername))
                {
                    _userHistories.Clear();
                    _logger.LogInformation("Cleared all sync history");
                }
                else if (_userHistories.ContainsKey(shokoUsername))
                {
                    _userHistories.Remove(shokoUsername);
                    _logger.LogInformation("Cleared sync history for user {Username}", shokoUsername);
                }

                await SaveHistoryAsync();
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task SaveHistoryAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_userHistories, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(_historyFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save sync history");
            }
        }

        public void LogSync(string animeName, int episodeNumber, int totalEpisodes, string action, 
            bool success, string shokoUsername, string malUsername, string? errorMessage = null, string? details = null, int? animeId = null, string? animeImage = null)
        {
            var entry = new SyncHistoryEntry
            {
                AnimeName = animeName,
                AnimeId = animeId, // Now properly setting the anime ID
                AnimeImage = animeImage,
                EpisodeNumber = episodeNumber,
                TotalEpisodes = totalEpisodes,
                Action = action,
                Success = success,
                ShokoUsername = shokoUsername,
                MalUsername = malUsername,
                ProviderUsername = malUsername,
                ErrorMessage = errorMessage,
                Details = details
            };

            // Fire and forget - don't wait for the save
            Task.Run(async () => await AddEntryAsync(entry));
        }

        /// <summary>
        /// New simplified LogSync method that directly uses the new format
        /// </summary>
        public void LogSyncDirect(string username, int? animeId, string animeTitle, int episodesSynced, 
            string action, bool success, string? message = null, string? details = null, string? animeImage = null, string? providerUsername = null)
        {
            var entry = new HistoryEntry
            {
                Timestamp = DateTime.Now,
                Action = action,
                AnimeId = animeId,
                AnimeTitle = animeTitle,
                AnimeImage = animeImage,
                EpisodesSynced = episodesSynced,
                Status = DetermineStatus(action, episodesSynced, 0), // We don't have total episodes in this context
                Success = success,
                Message = message ?? (success ? $"Successfully synced episode {episodesSynced}" : "Sync failed"),
                Details = details,
                Provider = new ProviderInfo 
                { 
                    Name = "MAL",
                    Username = providerUsername
                }
            };

            // Fire and forget - don't wait for the save
            Task.Run(async () => await AddEntryAsync(username, entry));
        }
    }
}