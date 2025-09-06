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
        private List<SyncHistoryEntry> _history = new List<SyncHistoryEntry>();
        private readonly int _maxHistoryEntries = 1000; // Keep last 1000 entries

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
                if (File.Exists(_historyFilePath))
                {
                    var json = File.ReadAllText(_historyFilePath);
                    _history = JsonSerializer.Deserialize<List<SyncHistoryEntry>>(json) ?? new List<SyncHistoryEntry>();
                    _logger.LogInformation("Loaded {Count} sync history entries", _history.Count);
                }
                else
                {
                    _history = new List<SyncHistoryEntry>();
                    _logger.LogInformation("Created new sync history");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load sync history, starting with empty history");
                _history = new List<SyncHistoryEntry>();
            }
        }

        public async Task AddEntryAsync(SyncHistoryEntry entry)
        {
            await _fileLock.WaitAsync();
            try
            {
                _history.Insert(0, entry); // Add to beginning (most recent first)
                
                // Trim history if it exceeds max entries
                if (_history.Count > _maxHistoryEntries)
                {
                    _history = _history.Take(_maxHistoryEntries).ToList();
                }

                await SaveHistoryAsync();
                _logger.LogDebug("Added sync history entry for {AnimeName}", entry.AnimeName);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task<List<SyncHistoryEntry>> GetHistoryAsync(int? limit = null, string? shokoUsername = null)
        {
            await _fileLock.WaitAsync();
            try
            {
                var query = _history.AsEnumerable();
                
                if (!string.IsNullOrEmpty(shokoUsername))
                {
                    query = query.Where(h => h.ShokoUsername == shokoUsername);
                }

                if (limit.HasValue)
                {
                    query = query.Take(limit.Value);
                }

                return query.ToList();
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
                var query = _history.AsEnumerable();
                
                if (!string.IsNullOrEmpty(shokoUsername))
                {
                    query = query.Where(h => h.ShokoUsername == shokoUsername);
                }

                var historyList = query.ToList();
                
                var stats = new SyncHistoryStats
                {
                    TotalSyncs = historyList.Count,
                    SuccessfulSyncs = historyList.Count(h => h.Success),
                    FailedSyncs = historyList.Count(h => !h.Success),
                    LastSyncTime = historyList.FirstOrDefault()?.Timestamp
                };

                // Group by user
                foreach (var userGroup in historyList.GroupBy(h => h.ShokoUsername ?? "Unknown"))
                {
                    stats.SyncsByUser[userGroup.Key] = userGroup.Count();
                }

                // Group by action
                foreach (var actionGroup in historyList.GroupBy(h => h.Action ?? "Unknown"))
                {
                    stats.SyncsByAction[actionGroup.Key] = actionGroup.Count();
                }

                return stats;
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
                    _history.Clear();
                    _logger.LogInformation("Cleared all sync history");
                }
                else
                {
                    _history = _history.Where(h => h.ShokoUsername != shokoUsername).ToList();
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
                var json = JsonSerializer.Serialize(_history, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                await File.WriteAllTextAsync(_historyFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save sync history");
            }
        }

        public void LogSync(string animeName, int episodeNumber, int totalEpisodes, string action, 
            bool success, string shokoUsername, string malUsername, string? errorMessage = null, string? details = null)
        {
            var entry = new SyncHistoryEntry
            {
                AnimeName = animeName,
                EpisodeNumber = episodeNumber,
                TotalEpisodes = totalEpisodes,
                Action = action,
                Success = success,
                ShokoUsername = shokoUsername,
                MalUsername = malUsername,
                ErrorMessage = errorMessage,
                Details = details
            };

            // Fire and forget - don't wait for the save
            Task.Run(async () => await AddEntryAsync(entry));
        }
    }
}