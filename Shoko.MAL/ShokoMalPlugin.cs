using Shoko.Plugin.Abstractions.Events;
using Shoko.Plugin.Abstractions.Services;
using Shoko.Plugin.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.Abstractions.DataModels;
using Shoko.AniSync.Api;
using Microsoft.Extensions.Caching.Memory;
using Shoko.AniSync.Interfaces;
using Shoko.Plugin.Abstractions.DataModels.Shoko;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Models.Mal;
using System.Globalization;
using System.Xml.Linq;
using System.Linq;
using Shoko.AniSync.Configuration;
using System.Text.RegularExpressions;
using Shoko.Plugin.Abstractions.Enums;

namespace Shoko.AniSync
{
    public class ShokoMalPlugin : IHostedService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly IMetadataService _metadataService;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ShokoMalPlugin> _logger;
        private readonly IUserDataService _userDataService;
        private readonly IApplicationPaths _applicationPaths;
        public static SyncHistoryManager SyncHistory { get; private set; }

        internal IApiCallHelpers ApiCallHelpers;

        // Shoko will inject the IMetadataService for you
        public ShokoMalPlugin(IApplicationPaths applicationPaths, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IMemoryCache memoryCache, IMetadataService metadataService, IUserDataService userDataService)
        {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ShokoMalPlugin>();
            _memoryCache = memoryCache;
            _metadataService = metadataService;
            _userDataService = userDataService;
            _applicationPaths = applicationPaths;
            
            // Initialize sync history manager
            SyncHistory = new SyncHistoryManager(applicationPaths.PluginsPath, loggerFactory);
        }

        private bool CompareStrings(string first, string second)
        {
            bool match = String.Compare(first, second, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols) == 0;
            return match;
        }

        private bool TitleCheck(Anime anime, IShokoEpisode episode)
        {
            return episode.Series.Titles.Any(title =>
            {
                bool titleMatch = CompareStrings(anime.Title, title.Title) ||
                   (anime.AlternativeTitles.En != null && CompareStrings(anime.AlternativeTitles.En, title.Title)) ||
                   (anime.AlternativeTitles.Ja != null && CompareStrings(anime.AlternativeTitles.Ja, title.Title)) ||
                   (anime.AlternativeTitles.Synonyms != null && anime.AlternativeTitles.Synonyms.Any(synonym => CompareStrings(synonym, title.Title)));
                if (!titleMatch)
                {
                    titleMatch = ContainsExtended(anime.Title, title.Title) ||
                   (anime.AlternativeTitles.En != null && ContainsExtended(anime.AlternativeTitles.En, title.Title)) ||
                   (anime.AlternativeTitles.Ja != null && ContainsExtended(anime.AlternativeTitles.Ja, title.Title)) ||
                   (anime.AlternativeTitles.Synonyms != null && anime.AlternativeTitles.Synonyms.Any(synonym => ContainsExtended(synonym, title.Title)));
                }
                return titleMatch;
            });
        }

        private bool ContainsExtended(string first, string second)
        {
            return StringFormatter.RemoveSpecialCharacters(first).Contains(StringFormatter.RemoveSpecialCharacters(second), StringComparison.OrdinalIgnoreCase);
        }

        private async Task<Anime> GetOva(int animeId, IReadOnlyList<AnimeTitle> episodeNames, string? alternativeId = null, string shokoUsername = null)
        {
            Anime anime = await ApiCallHelpers.GetAnime(animeId, getRelated: true, alternativeId: alternativeId, shokoUsername: shokoUsername);
            if (anime == null)
            {
                _logger.LogError("Could not get anime for ID {AnimeId}", animeId);
                return null;
            }

            if (anime != null)
            {
                var listOfRelatedAnime = anime.RelatedAnime.Where(relation => relation.RelationType is AnimeRelationType.Side_Story or AnimeRelationType.Alternative_Version or AnimeRelationType.Alternative_Setting);
                foreach (RelatedAnime relatedAnime in listOfRelatedAnime)
                {
                    var detailedRelatedAnime = await ApiCallHelpers.GetAnime(relatedAnime.Anime.Id, alternativeId: relatedAnime.Anime.AlternativeId, shokoUsername: shokoUsername);
                    if (detailedRelatedAnime is { Title: { }, AlternativeTitles: { En: { } } })
                    {
                        bool titleMatch = episodeNames.Any(episodeName =>
                        {
                            bool match = ContainsExtended(detailedRelatedAnime.Title, episodeName.Title) ||
                            (detailedRelatedAnime.AlternativeTitles.En != null && ContainsExtended(detailedRelatedAnime.AlternativeTitles.En, episodeName.Title)) ||
                            (detailedRelatedAnime.AlternativeTitles.Ja != null && ContainsExtended(detailedRelatedAnime.AlternativeTitles.Ja, episodeName.Title));
                            return match;
                        });
                        if (titleMatch)
                        {
                            // rough match
                            return detailedRelatedAnime;
                        }
                    }
                }
            }

            return null;
        }

        private DateTime? ParseFullDate(string dateString)
        {
            if (string.IsNullOrWhiteSpace(dateString))
                return null;

            if (DateTime.TryParse(dateString, out DateTime result))
                return result;

            return null;
        }

        private async Task<Anime?> FetchIdFromProvider(IShokoEpisode episode, string shokoUsername = null)
        {
            // Cache key for search results
            var cacheKey = $"mal_search_{episode.Series.AnidbAnimeID}";
            
            var uniqueAnimes = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                
                var searchTasks = episode.Series.Titles.Select(t => ApiCallHelpers.SearchAnime(t.Title, shokoUsername)).ToArray();
                var results = await Task.WhenAll(searchTasks);
                var allAnimes = results.SelectMany(list => list);
                return allAnimes.DistinctBy(a => a.Id).ToList();
            });
            
            var candidates = uniqueAnimes
                            .Where(a => TitleCheck(a, episode))
                            .Select(a => new {
                                Anime = a,
                                MalDate = ParseFullDate(a.StartDate),
                                DiffDays = episode.Series.AirDate.HasValue && ParseFullDate(a.StartDate).HasValue
                                    ? Math.Abs((ParseFullDate(a.StartDate).Value - episode.Series.AirDate.Value).TotalDays)
                                    : double.MaxValue
                            })
                            .Where(x => x.DiffDays < 30)
                            .OrderBy(x => x.DiffDays)
                            .ToList();
            
            if (candidates.Count > 1)
            {
                _logger.LogDebug("Found {Count} candidates for {Title}, selected best match with {Days} days difference", 
                    candidates.Count, episode.Series.Titles.First().Title, candidates.First().DiffDays.ToString("0"));
            }
            
            Anime? anime = candidates.FirstOrDefault()?.Anime;
            if (anime == null)
            {
                _logger.LogError("Could not find anime for {Title}", episode.Series.Titles.First().Title);
                return null;
            }
            
            // Only fetch full details if we don't have them cached and actually need related anime
            // For now, return the basic anime object as it contains most needed fields
            return anime;
        }

        private bool DetermineWatchedState(VideoUserDataSavedEventArgs e)
        {
            // Check the reason for the event
            switch (e.Reason)
            {
                case UserDataSaveReason.PlaybackEnd:
                    // Episode was played to the end - definitely watched
                    return true;
                    
                case UserDataSaveReason.UserInteraction:
                    // User manually changed the state
                    // The challenge: We can't directly know if marking watched or unwatched
                    // Strategy: 
                    // 1. If LastPlayedAt is null -> unwatched (never watched or unmarked)
                    // 2. If LastPlayedAt exists and is very recent (< 10 seconds) -> just marked watched
                    // 3. If LastPlayedAt exists but is old -> likely unmarking (the date didn't change)
                    
                    if (!e.UserData.LastPlayedAt.HasValue)
                    {
                        // No watch date = unwatched
                        _logger.LogDebug("UserInteraction: No LastPlayedAt - treating as unwatched");
                        return false;
                    }
                    
                    var timeSinceWatched = DateTime.Now - e.UserData.LastPlayedAt.Value;
                    bool isRecentlyWatched = timeSinceWatched.TotalSeconds < 10;
                    
                    _logger.LogDebug("UserInteraction: LastPlayedAt={LastPlayed}, TimeSince={Seconds}s, IsRecent={IsRecent}",
                        e.UserData.LastPlayedAt.Value, timeSinceWatched.TotalSeconds, isRecentlyWatched);
                    
                    // If the timestamp is very recent, user just marked it as watched
                    // If the timestamp is old, user is likely unmarking (timestamp didn't update)
                    return isRecentlyWatched;
                    
                case UserDataSaveReason.AnidbImport:
                    // Imported from AniDB - has watch date means watched
                    return e.UserData.LastPlayedAt.HasValue;
                    
                case UserDataSaveReason.PlaybackProgress:
                case UserDataSaveReason.PlaybackStart:
                case UserDataSaveReason.PlaybackPause:
                case UserDataSaveReason.PlaybackResume:
                    // These are progress updates, not completion - don't sync
                    // These should be filtered out earlier, but if we get here, check LastPlayedAt
                    return e.UserData.LastPlayedAt.HasValue;
                    
                default:
                    // Default: check if it has a watch date
                    _logger.LogDebug("Unknown reason {Reason}, using LastPlayedAt presence: {HasDate}", 
                        e.Reason, e.UserData.LastPlayedAt.HasValue);
                    return e.UserData.LastPlayedAt.HasValue;
            }
        }

        private async Task<Anime?> GetIdFromOfflineDb(IShokoEpisode episode, string shokoUsername = null)
        {
            var offlineDbIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(), episode.Series.AnidbAnimeID, AnimeOfflineDatabaseHelpers.Source.Anidb);
            if (offlineDbIds == null || offlineDbIds.Mal == null)
            {
                _logger.LogWarning("Could not get offline database IDs for AniDB ID {AnidbId}, fetching from provider...", episode.Series.AnidbAnimeID);
                return await FetchIdFromProvider(episode, shokoUsername);
            }
            var malId = offlineDbIds.Mal;
            var anime = await ApiCallHelpers.GetAnime(malId.Value, shokoUsername: shokoUsername);
            if (anime == null)
            {
                _logger.LogError("Could not get anime for MAL ID {MalId}, fetching from provider...", malId);
                return await FetchIdFromProvider(episode, shokoUsername);
            }
            return anime;
        }


        private async void OnEpisodeWatchedAsync(object? sender, VideoUserDataSavedEventArgs e)
        {
            try
            {
                // Get the user who triggered this event
                var shokoUser = e.User; // This is the IShokoUser who watched the episode!
                if (shokoUser != null)
                {
                    _logger.LogInformation("Episode watched by Shoko user: {Username}", 
                        shokoUser.Username);
                    // User-specific MAL sync will be handled below
                }
                else
                {
                    _logger.LogWarning("No user information available in episode watched event");
                }
                
                // Skip progress events that don't represent completion or manual changes
                if (e.Reason == UserDataSaveReason.PlaybackProgress || 
                    e.Reason == UserDataSaveReason.PlaybackStart ||
                    e.Reason == UserDataSaveReason.PlaybackPause ||
                    e.Reason == UserDataSaveReason.PlaybackResume)
                {
                    _logger.LogDebug("Skipping sync for playback progress event: {Reason}", e.Reason);
                    return;
                }

                // Get the correct MAL account for this user
                UserApiAuth userAuth = null;
                if (shokoUser != null && Plugin.Instance.Config != null)
                {
                    userAuth = Plugin.Instance.Config.GetAuthForShokoUser(shokoUser.Username);
                    if (userAuth == null)
                    {
                        _logger.LogWarning("No MAL account configured for Shoko user {Username}, skipping sync", shokoUser.Username);
                        return;
                    }
                    _logger.LogInformation("Using MAL account {MalUser} for Shoko user {ShokoUser}", 
                        userAuth.Username, shokoUser.Username);
                }
                else
                {
                    _logger.LogWarning("No user information available in episode event, skipping sync");
                    return;
                }
                
                switch (Plugin.Instance.Config.SelectedProvider)
                {
                    case Configuration.ApiName.Mal:
                        // Use the user-specific MAL API with their credentials
                        var malApiCalls = new MalApiCalls(_httpClientFactory, _loggerFactory, _memoryCache, new Delayer());
                        // TODO: Set the user-specific auth token on malApiCalls
                        ApiCallHelpers = new ApiCallHelpers(malApiCalls: malApiCalls);
                        break;
                    default:
                        break;
                }

                IShokoEpisode maxEpisode = null;
                foreach (var episode in e.Video.Episodes)
                {
                    if (episode.Type is not EpisodeType.Episode and not EpisodeType.Special || episode.Series is not { } series) continue;
                    if (maxEpisode == null || episode.EpisodeNumber > maxEpisode.EpisodeNumber)
                    {
                        maxEpisode = episode;
                    }
                }
                if (maxEpisode == null)
                {
                    _logger.LogWarning("No episodes found for {Title}", e.Video.MediaInfo.Title);
                    return;
                }

                // Determine if episode is being marked as watched or unwatched
                bool isWatched = DetermineWatchedState(e);
                int shokoEpisodeNumber = maxEpisode.EpisodeNumber;
                int playbackCount = e.UserData.PlaybackCount;
                
                _logger.LogInformation("Episode {EpisodeNumber} event: Reason={Reason}, PlaybackCount={Count}, IsWatched={Watched} for {Title}", 
                    shokoEpisodeNumber, e.Reason, playbackCount, isWatched, maxEpisode.Series.PreferredTitle);

                var anime = await GetIdFromOfflineDb(maxEpisode, shokoUser?.Username);
                if (anime != null)
                {
                    _logger.LogInformation("Found Anime: {Title} ({Id})", anime.Title, anime.Id);
                    
                    // Fetch the anime from user's list to get current status
                    var animeWithStatus = await ApiCallHelpers.GetAnime(anime.Id, alternativeId: anime.AlternativeId, getRelated: false, shokoUsername: shokoUser?.Username);
                    
                    if (animeWithStatus?.MyListStatus != null)
                    {
                        var currentStatus = animeWithStatus.MyListStatus.Status;
                        var malEpisodeCount = animeWithStatus.MyListStatus.NumEpisodesWatched;
                        var totalEpisodes = animeWithStatus.NumEpisodes;
                        var isRewatching = animeWithStatus.MyListStatus.IsRewatching;
                        // RewatchCount is tracked separately from the sync logic
                        
                        _logger.LogInformation("Current MAL Status - Episodes: {MalEpisodes}/{Total}, Status: {Status}, Rewatching: {Rewatching}", 
                            malEpisodeCount, totalEpisodes, currentStatus, isRewatching);

                        // Determine if we need to update MAL
                        bool shouldUpdate = false;
                        int newEpisodeCount = malEpisodeCount;
                        Status? newStatus = null;
                        bool? setRewatching = null;

                        if (isWatched)
                        {
                            // Episode marked as watched in Shoko
                            if (shokoEpisodeNumber > malEpisodeCount)
                            {
                                // Progress forward - update MAL
                                shouldUpdate = true;
                                newEpisodeCount = shokoEpisodeNumber;
                                
                                // Update status based on progress
                                if (currentStatus != Status.Watching && currentStatus != Status.Completed)
                                {
                                    newStatus = Status.Watching;
                                }
                                
                                // Check if completed
                                if (totalEpisodes > 0 && shokoEpisodeNumber >= totalEpisodes)
                                {
                                    newStatus = Status.Completed;
                                    setRewatching = false;
                                }
                                
                                _logger.LogInformation("Updating MAL: Episode {Episode} (was {OldEpisode})", newEpisodeCount, malEpisodeCount);
                            }
                            else if (shokoEpisodeNumber < malEpisodeCount)
                            {
                                // Going backwards - likely a rewatch
                                if (currentStatus == Status.Completed || malEpisodeCount == totalEpisodes)
                                {
                                    // Started rewatching a completed series
                                    shouldUpdate = true;
                                    setRewatching = true;
                                    _logger.LogInformation("Detected rewatch - setting is_rewatching flag");
                                }
                                else
                                {
                                    _logger.LogInformation("Episode {Episode} already watched in MAL (has {MalEpisode}), skipping update", 
                                        shokoEpisodeNumber, malEpisodeCount);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("Episode {Episode} already synced with MAL, skipping update", shokoEpisodeNumber);
                            }
                        }
                        else
                        {
                            // Episode marked as unwatched in Shoko
                            // Only update MAL if this is the highest episode (to allow rolling back from the end)
                            if (shokoEpisodeNumber == malEpisodeCount && malEpisodeCount > 0)
                            {
                                // User is unmarking the highest watched episode - roll back by one
                                shouldUpdate = true;
                                newEpisodeCount = malEpisodeCount - 1;
                                
                                // Update status if rolling back from completed
                                if (currentStatus == Status.Completed)
                                {
                                    newStatus = Status.Watching;
                                    setRewatching = false;
                                }
                                
                                _logger.LogInformation("Rolling back MAL progress: Episode {OldEpisode} -> {NewEpisode} (unmarked highest episode)", 
                                    malEpisodeCount, newEpisodeCount);
                            }
                            else if (shokoEpisodeNumber < malEpisodeCount)
                            {
                                _logger.LogInformation("Episode {Episode} unmarked but MAL has higher episodes watched ({MalEpisode}) - skipping rollback to avoid gaps", 
                                    shokoEpisodeNumber, malEpisodeCount);
                            }
                            else
                            {
                                _logger.LogInformation("Episode marked as unwatched in Shoko - no MAL update needed");
                            }
                        }

                        // Perform the update if needed
                        if (shouldUpdate)
                        {
                            _logger.LogInformation("Updating MAL status for {Title}: Episodes: {Episodes}, Status: {Status}, Rewatching: {Rewatching}",
                                anime.Title, newEpisodeCount, newStatus?.ToString() ?? "unchanged", setRewatching?.ToString() ?? "unchanged");
                            
                            // Call UpdateAnime with the appropriate status
                            UpdateAnimeStatusResponse updateResult = null;
                            
                            // Determine which status to use: new status if changed, otherwise keep current
                            var statusToUse = newStatus ?? currentStatus;
                            
                            // Add date tracking
                            DateTime? startDate = null;
                            DateTime? endDate = null;
                            
                            // Set start date if this is the first episode being watched (going from 0 to 1+)
                            // AND the anime doesn't already have a start date in MAL
                            if (malEpisodeCount == 0 && newEpisodeCount > 0)
                            {
                                // Check if we already have dates in MAL before overwriting
                                var existingStartDate = animeWithStatus?.MyListStatus?.StartDate;
                                if (string.IsNullOrEmpty(existingStartDate))
                                {
                                    startDate = DateTime.Now.Date;
                                    _logger.LogInformation("Setting start date to {Date} for {Title}", startDate.Value.ToString("yyyy-MM-dd"), anime.Title);
                                }
                                else
                                {
                                    _logger.LogInformation("Keeping existing start date {Date} for {Title}", existingStartDate, anime.Title);
                                }
                            }
                            
                            // Clear dates when completely unwatching (going to 0 episodes)
                            if (newEpisodeCount == 0 && malEpisodeCount > 0)
                            {
                                // Send empty string to clear the dates in MAL
                                startDate = DateTime.MinValue; // This will be handled specially in the API call
                                endDate = DateTime.MinValue;
                                _logger.LogInformation("Clearing start and end dates for {Title} (unwatched completely)", anime.Title);
                            }
                            
                            // When starting a rewatch, preserve the original dates
                            if (setRewatching == true && currentStatus == Status.Completed)
                            {
                                // Don't touch the dates - they should remain as the original watch dates
                                _logger.LogInformation("Starting rewatch for {Title}, preserving original dates", anime.Title);
                            }
                            
                            // Set end date if completing the series for the first time (not during rewatch)
                            if (newStatus == Status.Completed && currentStatus != Status.Completed)
                            {
                                // Only set end date if this is the first completion, not a rewatch completion
                                // If the anime already has an end date, it means it was completed before (this is a rewatch)
                                var existingEndDate = animeWithStatus?.MyListStatus?.FinishDate;
                                if (string.IsNullOrEmpty(existingEndDate))
                                {
                                    endDate = DateTime.Now.Date;
                                    _logger.LogInformation("Setting end date to {Date} for {Title} (first completion)", endDate.Value.ToString("yyyy-MM-dd"), anime.Title);
                                }
                                else
                                {
                                    _logger.LogInformation("Preserving original end date {Date} for {Title} (rewatch completion)", existingEndDate, anime.Title);
                                }
                            }
                            
                            // Clear end date when rolling back from completed
                            if (currentStatus == Status.Completed && newStatus == Status.Watching)
                            {
                                endDate = DateTime.MinValue; // This will be handled specially to clear the date
                                _logger.LogInformation("Clearing end date for {Title} (rolled back from completed)", anime.Title);
                            }
                            
                            updateResult = await ApiCallHelpers.UpdateAnime(
                                anime.Id, 
                                newEpisodeCount, 
                                statusToUse,
                                isRewatching: setRewatching,
                                startDate: startDate,
                                endDate: endDate,
                                alternativeId: anime.AlternativeId,
                                shokoUsername: shokoUser?.Username
                            );
                            
                            if (updateResult != null)
                            {
                                _logger.LogInformation("Successfully updated MAL status for {Title}", anime.Title);
                                
                                // Log to sync history
                                string action = "Updated";
                                string details = null;
                                
                                if (newStatus == Status.Completed)
                                {
                                    action = "Completed";
                                    if (endDate.HasValue && endDate.Value != DateTime.MinValue)
                                        details = "Set end date";
                                }
                                else if (setRewatching == true)
                                {
                                    action = "Rewatching";
                                    details = "Started rewatch";
                                }
                                else if (newEpisodeCount == 0)
                                {
                                    action = "Unwatched";
                                    details = "Cleared all progress";
                                }
                                else if (newEpisodeCount < malEpisodeCount)
                                {
                                    action = "Rolled back";
                                    details = $"From episode {malEpisodeCount} to {newEpisodeCount}";
                                }
                                else
                                {
                                    action = "Watched";
                                    if (startDate.HasValue && startDate.Value != DateTime.MinValue)
                                        details = "Set start date";
                                }
                                
                                SyncHistory?.LogSync(
                                    anime.Title,
                                    newEpisodeCount,
                                    anime.NumEpisodes,
                                    action,
                                    true,
                                    shokoUser?.Username,
                                    userAuth?.Username,
                                    null,
                                    details
                                );
                            }
                            else
                            {
                                _logger.LogError("Failed to update MAL status for {Title}", anime.Title);
                                
                                // Log failure to sync history
                                SyncHistory?.LogSync(
                                    anime.Title,
                                    newEpisodeCount,
                                    anime.NumEpisodes,
                                    isWatched ? "Watched" : "Unwatched",
                                    false,
                                    shokoUser?.Username,
                                    userAuth?.Username,
                                    "Failed to update MAL",
                                    null
                                );
                            }
                        }
                    }
                    else
                    {
                        // Anime not in user's list - add it if watched
                        if (isWatched)
                        {
                            _logger.LogInformation("Adding {Title} to MAL list with episode {Episode}", anime.Title, shokoEpisodeNumber);
                            
                            var newStatus = (anime.NumEpisodes > 0 && shokoEpisodeNumber >= anime.NumEpisodes) 
                                ? Status.Completed 
                                : Status.Watching;
                            
                            // Set dates when adding anime to list
                            DateTime? startDate = DateTime.Now.Date;
                            DateTime? endDate = null;
                            
                            // If completing on first watch, set both start and end date
                            if (newStatus == Status.Completed)
                            {
                                endDate = DateTime.Now.Date;
                                _logger.LogInformation("Setting start and end date to {Date} for {Title} (completed on add)", startDate.Value.ToString("yyyy-MM-dd"), anime.Title);
                            }
                            else
                            {
                                _logger.LogInformation("Setting start date to {Date} for {Title}", startDate.Value.ToString("yyyy-MM-dd"), anime.Title);
                            }
                            
                            var updateResult = await ApiCallHelpers.UpdateAnime(
                                anime.Id, 
                                shokoEpisodeNumber, 
                                newStatus,
                                startDate: startDate,
                                endDate: endDate,
                                alternativeId: anime.AlternativeId,
                                shokoUsername: shokoUser?.Username
                            );
                            
                            if (updateResult != null)
                            {
                                _logger.LogInformation("Successfully added {Title} to MAL list", anime.Title);
                                
                                // Log to sync history
                                string action = newStatus == Status.Completed ? "Completed" : "Added to list";
                                string details = newStatus == Status.Completed 
                                    ? "Added as completed with start and end dates" 
                                    : "Added to watching list with start date";
                                
                                SyncHistory?.LogSync(
                                    anime.Title,
                                    shokoEpisodeNumber,
                                    anime.NumEpisodes,
                                    action,
                                    true,
                                    shokoUser?.Username,
                                    userAuth?.Username,
                                    null,
                                    details
                                );
                            }
                            else
                            {
                                _logger.LogError("Failed to add {Title} to MAL list", anime.Title);
                                
                                // Log failure to sync history
                                SyncHistory?.LogSync(
                                    anime.Title,
                                    shokoEpisodeNumber,
                                    anime.NumEpisodes,
                                    "Add to list",
                                    false,
                                    shokoUser?.Username,
                                    userAuth?.Username,
                                    "Failed to add to MAL",
                                    null
                                );
                            }
                        }
                        else
                        {
                            _logger.LogInformation("Anime not in MAL list and episode marked unwatched - skipping");
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Could not find MAL anime for {Title}", maxEpisode.Series.PreferredTitle);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing episode watch event");
            }
        }

        Task IHostedService.StartAsync(CancellationToken cancellationToken)
        {
            _userDataService.VideoUserDataSaved += OnEpisodeWatchedAsync;
            return Task.CompletedTask;
        }

        Task IHostedService.StopAsync(CancellationToken cancellationToken)
        {
            _userDataService.VideoUserDataSaved -= OnEpisodeWatchedAsync;
            return Task.CompletedTask;
        }
    }
}
