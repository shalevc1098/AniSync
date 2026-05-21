using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.User;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.AniSync.Api;
using Microsoft.Extensions.Caching.Memory;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Models.Mal;
using Shoko.MAL.Models;
using System.Globalization;
using System.Linq;
using Shoko.AniSync.Configuration;
using System.Text.RegularExpressions;

namespace Shoko.AniSync
{
    public class ShokoAniSyncPlugin : IHostedService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _memoryCache;
        private readonly IMetadataService _metadataService;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ShokoAniSyncPlugin> _logger;
        private readonly IUserDataService _userDataService;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ConfigurationProvider<Config> _configProvider;
        public static SyncHistoryManager SyncHistory { get; private set; } = null!;

        // Shoko will inject the IMetadataService for you
        public ShokoAniSyncPlugin(IApplicationPaths applicationPaths, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IMemoryCache memoryCache, IMetadataService metadataService, IUserDataService userDataService, ConfigurationProvider<Config> configProvider)
        {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<ShokoAniSyncPlugin>();
            _memoryCache = memoryCache;
            _metadataService = metadataService;
            _userDataService = userDataService;
            _applicationPaths = applicationPaths;
            _configProvider = configProvider;

            // Initialize sync history manager
            SyncHistory = new SyncHistoryManager(Path.Combine(applicationPaths.PluginsPath, "AniSync"), loggerFactory);
        }

        /// <summary>
        /// Gets episode thumbnail URL if available, falls back to MAL anime image
        /// </summary>
        private string? GetEpisodeThumbnailUrl(IShokoEpisode? episode, Anime? anime)
        {
            if (episode != null)
            {
                return $"/api/v3/Episode/{episode.AnidbEpisodeID}/Images/Thumbnail";
            }

            return anime?.MainPicture?.Medium ?? anime?.MainPicture?.Large;
        }

        private static DateTime? GetSeriesAirDate(object? series)
        {
            if (series == null) return null;
            var value = series.GetType().GetProperty("AirDate")?.GetValue(series);
            if (value == null) return null;

            switch (value)
            {
                case DateTime dt: return dt;
                case DateOnly d: return d.ToDateTime(TimeOnly.MinValue);
            }

            var t = value.GetType();
            if (t.GetProperty("Year")?.GetValue(value) is int year && year > 0)
            {
                int month = (t.GetProperty("Month")?.GetValue(value) as int?) ?? 1;
                int day = (t.GetProperty("Day")?.GetValue(value) as int?) ?? 1;
                if (month < 1) month = 1;
                if (day < 1) day = 1;
                try { return new DateTime(year, month, day); }
                catch { return new DateTime(year, 1, 1); }
            }

            if (t.GetMethod("ToDateOnly", Type.EmptyTypes)?.Invoke(value, null) is DateOnly od)
                return od.ToDateTime(TimeOnly.MinValue);

            return null;
        }

        private bool CompareStrings(string first, string second)
        {
            bool match = String.Compare(first, second, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols) == 0;
            return match;
        }

        private bool TitleCheck(Anime anime, IShokoEpisode episode)
        {
            return episode.Series?.Titles?.Any(title =>
            {
                bool titleMatch = CompareStrings(anime.Title ?? "", title.Value) ||
                   (anime.AlternativeTitles?.En != null && CompareStrings(anime.AlternativeTitles.En, title.Value)) ||
                   (anime.AlternativeTitles?.Ja != null && CompareStrings(anime.AlternativeTitles.Ja, title.Value)) ||
                   (anime.AlternativeTitles?.Synonyms != null && anime.AlternativeTitles.Synonyms.Any(synonym => CompareStrings(synonym, title.Value)));
                if (!titleMatch)
                {
                    titleMatch = ContainsExtended(anime.Title ?? "", title.Value) ||
                   (anime.AlternativeTitles?.En != null && ContainsExtended(anime.AlternativeTitles.En, title.Value)) ||
                   (anime.AlternativeTitles?.Ja != null && ContainsExtended(anime.AlternativeTitles.Ja, title.Value)) ||
                   (anime.AlternativeTitles?.Synonyms != null && anime.AlternativeTitles.Synonyms.Any(synonym => ContainsExtended(synonym, title.Value)));
                }
                return titleMatch;
            }) ?? false;
        }

        private bool ContainsExtended(string first, string second)
        {
            var sanitizedFirst = StringFormatter.RemoveSpecialCharacters(first);
            var sanitizedSecond = StringFormatter.RemoveSpecialCharacters(second);
            if (string.IsNullOrEmpty(sanitizedFirst) || string.IsNullOrEmpty(sanitizedSecond))
                return false;
            return sanitizedFirst.Contains(sanitizedSecond, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<Anime?> GetOva(IApiCallHelpers apiCallHelpers, int animeId, IReadOnlyList<ITitle> episodeNames, string? alternativeId = null, string? shokoUsername = null)
        {
            Anime? anime = await apiCallHelpers.GetAnime(animeId, getRelated: true, alternativeId: alternativeId, shokoUsername: shokoUsername);
            if (anime == null)
            {
                _logger.LogError("Could not get anime for ID {AnimeId}", animeId);
                return null;
            }

            var listOfRelatedAnime = anime.RelatedAnime?.Where(relation => relation.RelationType is AnimeRelationType.Side_Story or AnimeRelationType.Alternative_Version or AnimeRelationType.Alternative_Setting) ?? new List<RelatedAnime>();
            foreach (RelatedAnime relatedAnime in listOfRelatedAnime)
            {
                if (relatedAnime.Anime?.Id is not > 0) continue;
                var detailedRelatedAnime = await apiCallHelpers.GetAnime(relatedAnime.Anime.Id, alternativeId: relatedAnime.Anime.AlternativeId, shokoUsername: shokoUsername);
                if (detailedRelatedAnime is { Title: { } })
                {
                    bool titleMatch = episodeNames.Any(episodeName =>
                    {
                        bool match = ContainsExtended(detailedRelatedAnime.Title, episodeName.Value) ||
                        (detailedRelatedAnime.AlternativeTitles?.En != null && ContainsExtended(detailedRelatedAnime.AlternativeTitles.En, episodeName.Value)) ||
                        (detailedRelatedAnime.AlternativeTitles?.Ja != null && ContainsExtended(detailedRelatedAnime.AlternativeTitles.Ja, episodeName.Value));
                        return match;
                    });
                    if (titleMatch)
                    {
                        // rough match
                        return detailedRelatedAnime;
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

        private async Task<Anime?> FetchIdFromProvider(IApiCallHelpers apiCallHelpers, IShokoEpisode episode, Config config, string? shokoUsername = null)
        {
            // Cache key includes username since NSFW settings are per-user
            var cacheKey = $"mal_search_{episode.Series?.AnidbAnimeID ?? 0}_{shokoUsername ?? "default"}";

            var updateNsfw = config.GetUpdateNsfw(shokoUsername);
            var uniqueAnimes = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

                var searchTasks = episode.Series?.Titles?.Select(t => apiCallHelpers.SearchAnime(t.Value, updateNsfw, shokoUsername)).ToArray() ?? new Task<List<Anime>?>[0];
                var results = await Task.WhenAll(searchTasks);
                var allAnimes = results.SelectMany(list => list ?? new List<Anime>());
                return allAnimes.DistinctBy(a => a.Id).ToList();
            });

            DateTime? seriesAirDate = GetSeriesAirDate(episode.Series);
            var candidates = (uniqueAnimes ?? new List<Anime>())
                            .Where(a => a != null && TitleCheck(a, episode))
                            .Select(a => new {
                                Anime = a,
                                MalDate = ParseFullDate(a.StartDate ?? ""),
                                DiffDays = seriesAirDate.HasValue && ParseFullDate(a.StartDate ?? "").HasValue
                                    ? Math.Abs((ParseFullDate(a.StartDate ?? "")!.Value - seriesAirDate.Value).TotalDays)
                                    : double.MaxValue
                            })
                            .Where(x => x.DiffDays < 30)
                            .OrderBy(x => x.DiffDays)
                            .ToList();

            if (candidates.Count > 1)
            {
                _logger.LogDebug("Found {Count} candidates for {Title}, selected best match with {Days} days difference",
                    candidates.Count, episode.Series?.Titles?.First()?.Value ?? "Unknown", candidates.First().DiffDays.ToString("0"));
            }

            Anime? anime = candidates.FirstOrDefault()?.Anime;
            if (anime == null)
            {
                _logger.LogError("Could not find anime for {Title}", episode.Series?.Titles?.First()?.Value ?? "Unknown");
                return null;
            }

            return anime;
        }

        private async Task<Anime?> GetIdFromOfflineDb(IApiCallHelpers apiCallHelpers, IShokoEpisode episode, Config config, string? shokoUsername = null)
        {
            var offlineDbIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(), episode.Series?.AnidbAnimeID ?? 0, AnimeOfflineDatabaseHelpers.Source.Anidb);
            if (offlineDbIds == null || offlineDbIds.Mal == null)
            {
                _logger.LogWarning("Could not get offline database IDs for AniDB ID {AnidbId}, fetching from provider...", episode.Series?.AnidbAnimeID ?? 0);
                return await FetchIdFromProvider(apiCallHelpers, episode, config, shokoUsername);
            }
            var malId = offlineDbIds.Mal;
            var anime = await apiCallHelpers.GetAnime(malId.Value, shokoUsername: shokoUsername);
            if (anime == null)
            {
                _logger.LogError("Could not get anime for MAL ID {MalId}, fetching from provider...", malId);
                return await FetchIdFromProvider(apiCallHelpers, episode, config, shokoUsername);
            }
            return anime;
        }


        private async void OnEpisodeWatchedAsync(object? sender, EpisodeUserDataSavedEventArgs e)
        {
            try
            {
                var config = _configProvider.Load();

                // Get the user who triggered this event
                var shokoUser = e.User;
                if (shokoUser != null)
                {
                    _logger.LogInformation("Episode event for Shoko user: {Username}",
                        shokoUser.Username);
                }
                else
                {
                    _logger.LogWarning("No user information available in episode event");
                }

                // Check if auto-sync is enabled for this user
                if (!config.GetEnableAutoSync(shokoUser?.Username))
                {
                    _logger.LogDebug("Auto-sync is disabled for user {Username}, skipping", shokoUser?.Username);
                    return;
                }

                // Skip import events
                if (e.Reason.HasFlag(EpisodeUserDataSaveReason.Import))
                    return;

                // Skip events that clearly don't affect watch state
                const EpisodeUserDataSaveReason nonWatchReasons =
                    EpisodeUserDataSaveReason.IsFavorite |
                    EpisodeUserDataSaveReason.UserTags |
                    EpisodeUserDataSaveReason.UserRating;
                if (e.Reason != EpisodeUserDataSaveReason.None && (e.Reason & ~nonWatchReasons) == 0)
                {
                    _logger.LogDebug("Skipping sync for non-watch event: {Reason}", e.Reason);
                    return;
                }

                // Get the correct MAL account for this user
                UserApiAuth? userAuth = null;
                if (shokoUser != null)
                {
                    userAuth = config.GetAuthForShokoUser(shokoUser.Username!);
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

                // Proactive token expiry check - attempt refresh before making API calls
                if (userAuth.ExpiresAt.HasValue && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > userAuth.ExpiresAt.Value)
                {
                    _logger.LogWarning("MAL token expired for user {User}, attempting refresh...", shokoUser.Username);
                    try
                    {
                        var auth = new ApiAuthentication(ApiName.Mal, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache);
                        await auth.RefreshAccessToken(shokoUser.Username!);
                        config = _configProvider.Load();
                        userAuth = config.GetAuthForShokoUser(shokoUser.Username!);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Token refresh failed for user {User}. Please re-authenticate via /anisync", shokoUser.Username);
                        return;
                    }
                }

                IApiCallHelpers apiCallHelpers;
                switch (config.SelectedProvider)
                {
                    case Configuration.ApiName.Mal:
                        // Use the user-specific MAL API with their credentials
                        var malApiCalls = new MalApiCalls(_httpClientFactory, _loggerFactory, _memoryCache, new Delayer(), _configProvider, _applicationPaths);
                        apiCallHelpers = new ApiCallHelpers(malApiCalls: malApiCalls);
                        break;
                    default:
                        _logger.LogWarning("No provider configured, skipping sync");
                        return;
                }

                var maxEpisode = e.Episode;
                if (maxEpisode?.Series == null)
                {
                    _logger.LogWarning("No episode or series in event");
                    return;
                }

                bool isWatched = e.UserData.IsWatched;
                int shokoEpisodeNumber = maxEpisode.EpisodeNumber;
                int playbackCount = e.UserData.PlaybackCount;

                _logger.LogInformation("Episode {EpisodeNumber} event: Reason={Reason}, PlaybackCount={Count}, IsWatched={Watched} for {Title}",
                    shokoEpisodeNumber, e.Reason, playbackCount, isWatched, maxEpisode.Series?.Title ?? "Unknown");

                var anime = await GetIdFromOfflineDb(apiCallHelpers, maxEpisode, config, shokoUser?.Username);
                if (anime != null)
                {
                    _logger.LogInformation("Found Anime: {Title} ({Id})", anime.Title, anime.Id);

                    // Check SyncOnlyCompleted - if enabled, skip non-completion syncs
                    bool syncOnlyCompleted = config.GetSyncOnlyCompleted(shokoUser?.Username);
                    if (syncOnlyCompleted && isWatched && anime.NumEpisodes > 0 && shokoEpisodeNumber < anime.NumEpisodes)
                    {
                        _logger.LogDebug("SyncOnlyCompleted is enabled and episode {Episode}/{Total} is not the last - skipping sync for {Title}",
                            shokoEpisodeNumber, anime.NumEpisodes, anime.Title);
                        return;
                    }

                    // Fetch the anime from user's list to get current status
                    var animeWithStatus = await apiCallHelpers.GetAnime(anime.Id, alternativeId: anime.AlternativeId, getRelated: false, shokoUsername: shokoUser?.Username);

                    if (animeWithStatus?.MyListStatus != null)
                    {
                        var currentStatus = animeWithStatus.MyListStatus.Status;
                        var malEpisodeCount = animeWithStatus.MyListStatus.NumEpisodesWatched;
                        var totalEpisodes = animeWithStatus.NumEpisodes;
                        var isRewatching = animeWithStatus.MyListStatus.IsRewatching;
                        var currentRewatchCount = animeWithStatus.MyListStatus.RewatchCount ?? 0;

                        _logger.LogInformation("Current MAL Status - Episodes: {MalEpisodes}/{Total}, Status: {Status}, Rewatching: {Rewatching}",
                            malEpisodeCount, totalEpisodes, currentStatus, isRewatching);

                        var decision = RewatchSyncDecision.Decide(
                            isWatched,
                            shokoEpisodeNumber,
                            playbackCount,
                            malEpisodeCount,
                            totalEpisodes,
                            currentStatus,
                            isRewatching,
                            currentRewatchCount,
                            config.GetEnableRewatchDetection(shokoUser?.Username),
                            config.GetAllowRollback(shokoUser?.Username));

                        bool shouldUpdate = decision.ShouldUpdate;
                        int newEpisodeCount = decision.NewEpisodeCount;
                        Status? newStatus = decision.NewStatus;
                        bool? setRewatching = decision.SetRewatching;
                        int? numberOfTimesRewatched = decision.NumberOfTimesRewatched;

                        if (!shouldUpdate)
                        {
                            _logger.LogInformation("No MAL update needed for {Title} (episode {Episode}, MAL has {MalEpisode}, watched={Watched})",
                                anime.Title, shokoEpisodeNumber, malEpisodeCount, isWatched);
                        }
                        else if (setRewatching == true)
                        {
                            _logger.LogInformation("Detected rewatch start for {Title} - setting is_rewatching, progress {Episode}", anime.Title, newEpisodeCount);
                        }
                        else if (numberOfTimesRewatched != null)
                        {
                            _logger.LogInformation("Completed rewatch #{Count} for {Title}", numberOfTimesRewatched, anime.Title);
                        }

                        // Perform the update if needed
                        if (shouldUpdate)
                        {
                            _logger.LogInformation("Updating MAL status for {Title}: Episodes: {Episodes}, Status: {Status}, Rewatching: {Rewatching}",
                                anime.Title ?? "Unknown", newEpisodeCount, newStatus?.ToString() ?? "unchanged", setRewatching?.ToString() ?? "unchanged");

                            // Call UpdateAnime with the appropriate status
                            UpdateAnimeStatusResponse? updateResult = null;

                            // Determine which status to use: new status if changed, otherwise keep current
                            var statusToUse = newStatus ?? currentStatus;

                            // Add date tracking
                            DateTime? startDate = null;
                            DateTime? endDate = null;

                            // Set start date based on user preference
                            bool setFromAnyEpisode = config.GetSetStartDateFromAnyEpisode(shokoUser?.Username);

                            if (malEpisodeCount == 0 && newEpisodeCount > 0)
                            {
                                bool shouldSetStartDate = setFromAnyEpisode || newEpisodeCount == 1;

                                if (shouldSetStartDate)
                                {
                                    var existingStartDate = animeWithStatus?.MyListStatus?.StartDate;
                                    if (string.IsNullOrEmpty(existingStartDate))
                                    {
                                        startDate = DateTime.Now.Date;
                                        _logger.LogInformation("Setting start date to {Date} for {Title} (started at episode {Episode})",
                                            startDate.Value.ToString("yyyy-MM-dd"), anime.Title, newEpisodeCount);
                                    }
                                    else
                                    {
                                        _logger.LogInformation("Keeping existing start date {Date} for {Title}", existingStartDate, anime.Title);
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation("Not setting start date for {Title} - started watching from episode {Episode} (SetStartDateFromAnyEpisode is disabled)",
                                        anime.Title, newEpisodeCount);
                                }
                            }

                            // Clear dates when completely unwatching (going to 0 episodes)
                            if (newEpisodeCount == 0 && malEpisodeCount > 0)
                            {
                                startDate = DateTime.MinValue;
                                endDate = DateTime.MinValue;
                                _logger.LogInformation("Clearing start and end dates for {Title} (unwatched completely)", anime.Title);
                            }

                            // When starting a rewatch, preserve the original dates
                            if (setRewatching == true && currentStatus == Status.Completed)
                            {
                                _logger.LogInformation("Starting rewatch for {Title}, preserving original dates", anime.Title);
                            }

                            // Set end date if completing the series for the first time (not during rewatch)
                            if (newStatus == Status.Completed && currentStatus != Status.Completed)
                            {
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
                                endDate = DateTime.MinValue;
                                _logger.LogInformation("Clearing end date for {Title} (rolled back from completed)", anime.Title);
                            }

                            updateResult = await apiCallHelpers.UpdateAnime(
                                anime.Id,
                                newEpisodeCount,
                                statusToUse,
                                isRewatching: setRewatching,
                                numberOfTimesRewatched: numberOfTimesRewatched,
                                startDate: startDate,
                                endDate: endDate,
                                alternativeId: anime.AlternativeId,
                                shokoUsername: shokoUser?.Username
                            );

                            if (updateResult != null)
                            {
                                _logger.LogInformation("Successfully updated MAL status for {Title}", anime.Title);

                                // Log to sync history
                                SyncAction syncAction = SyncAction.Updated;
                                string? details = null;

                                if (newStatus == Status.Completed)
                                {
                                    syncAction = SyncAction.Completed;
                                    if (endDate.HasValue && endDate.Value != DateTime.MinValue)
                                        details = "Set end date";
                                }
                                else if (setRewatching == true)
                                {
                                    syncAction = SyncAction.Rewatching;
                                    details = "Started rewatch";
                                }
                                else if (isRewatching)
                                {
                                    syncAction = SyncAction.Rewatching;
                                    details = $"Rewatch progress: episode {newEpisodeCount}";
                                }
                                else if (newEpisodeCount == 0)
                                {
                                    syncAction = SyncAction.Unwatched;
                                    details = null;
                                }
                                else if (newEpisodeCount < malEpisodeCount)
                                {
                                    syncAction = SyncAction.RolledBack;
                                    details = $"From episode {malEpisodeCount} to {newEpisodeCount}";
                                }
                                else
                                {
                                    syncAction = SyncAction.Watched;
                                    if (startDate.HasValue && startDate.Value != DateTime.MinValue)
                                        details = "Set start date";
                                }

                                SyncHistory?.LogSync(
                                    shokoUser?.Username ?? "Unknown User",
                                    anime.Id,
                                    anime.Title ?? "Unknown",
                                    shokoEpisodeNumber,
                                    SyncActionHelper.GetActionText(syncAction),
                                    true,
                                    statusToUse,
                                    config.SelectedProvider.ToString(),
                                    GetEpisodeThumbnailUrl(maxEpisode, anime),
                                    userAuth?.Username ?? "Unknown MAL User"
                                );
                            }
                            else
                            {
                                _logger.LogError("Failed to update MAL status for {Title}", anime.Title);
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

                            var updateResult = await apiCallHelpers.UpdateAnime(
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
                                SyncAction syncAction = newStatus == Status.Completed ? SyncAction.Completed : SyncAction.AddedToList;
                                string? details = newStatus == Status.Completed
                                    ? "First time completion"
                                    : null;

                                SyncHistory?.LogSync(
                                    shokoUser?.Username ?? "Unknown User",
                                    anime.Id,
                                    anime.Title ?? "Unknown",
                                    shokoEpisodeNumber,
                                    SyncActionHelper.GetActionText(syncAction),
                                    true,
                                    newStatus,
                                    config.SelectedProvider.ToString(),
                                    GetEpisodeThumbnailUrl(maxEpisode, anime),
                                    userAuth?.Username ?? "Unknown MAL User"
                                );
                            }
                            else
                            {
                                _logger.LogError("Failed to add {Title} to MAL list", anime.Title);
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
                    _logger.LogWarning("Could not find MAL anime for {Title}", maxEpisode.Series?.Title ?? "Unknown");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing episode watch event");
            }
        }

        Task IHostedService.StartAsync(CancellationToken cancellationToken)
        {
            _userDataService.EpisodeUserDataSaved += OnEpisodeWatchedAsync;
            _logger.LogInformation("AniSync plugin started - listening for episode watch events");
            return Task.CompletedTask;
        }

        Task IHostedService.StopAsync(CancellationToken cancellationToken)
        {
            _userDataService.EpisodeUserDataSaved -= OnEpisodeWatchedAsync;
            return Task.CompletedTask;
        }
    }
}
