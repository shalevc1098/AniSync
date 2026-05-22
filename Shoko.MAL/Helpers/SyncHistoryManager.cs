using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.AniSync.Models;
using Shoko.AniSync.Models.Mal;
using Shoko.MAL.Models;

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
                    return history.Take(limit.Value).ToList();
                }

                return history.ToList();
            }
            finally
            {
                _fileLock.Release();
            }
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
                            var syncAction = (SyncAction)entry.Action;
                            var actionText = SyncActionHelper.GetActionText(syncAction);
                            if (stats.SyncsByAction.ContainsKey(actionText))
                                stats.SyncsByAction[actionText]++;
                            else
                                stats.SyncsByAction[actionText] = 1;
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
                        var syncAction = (SyncAction)entry.Action;
                        var actionText = SyncActionHelper.GetActionText(syncAction);
                        if (stats.SyncsByAction.ContainsKey(actionText))
                            stats.SyncsByAction[actionText]++;
                        else
                            stats.SyncsByAction[actionText] = 1;
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
                if (!_userHistories.TryGetValue(username, out var userHistory))
                    return null;

                // Snapshot under the lock - callers enumerate this after the lock is released,
                // and a concurrent watch event mutates the live History list.
                return new UserHistory
                {
                    History = userHistory.History.ToList(),
                    LastSync = userHistory.LastSync,
                    TotalSyncs = userHistory.TotalSyncs,
                    FailedSyncs = userHistory.FailedSyncs
                };
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
                // Write to temp file first, then rename atomically to prevent corruption on crash
                var tempPath = _historyFilePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json);
                File.Move(tempPath, _historyFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save sync history");
            }
        }

        /// <summary>
        /// LogSync method that uses the new format
        /// </summary>
        public Task LogSyncAsync(string username, int? animeId, string animeTitle, int episodeNumber,
            string action, bool success, Status status, string providerName = "MAL", string? animeImage = null, string? providerUsername = null)
        {
            var entry = new HistoryEntry
            {
                Timestamp = DateTime.Now,
                Action = (int)SyncActionHelper.ParseAction(action),
                AnimeId = animeId,
                AnimeTitle = animeTitle,
                AnimeImage = animeImage,
                EpisodeNumber = episodeNumber,
                Status = (int)status,
                Success = success,
                Provider = new ProviderInfo
                {
                    Name = providerName,
                    Username = providerUsername
                }
            };
            return AddEntryAsync(username, entry);
        }

        public void LogSync(string username, int? animeId, string animeTitle, int episodeNumber,
            string action, bool success, Status status, string providerName = "MAL", string? animeImage = null, string? providerUsername = null)
        {
            // Fire and forget - don't wait for the save
            _ = Task.Run(async () =>
            {
                try
                {
                    await LogSyncAsync(username, animeId, animeTitle, episodeNumber, action, success, status, providerName, animeImage, providerUsername);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save sync history entry for {AnimeTitle}", animeTitle);
                }
            });
        }

    }
}