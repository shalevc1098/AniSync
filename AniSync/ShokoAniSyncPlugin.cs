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
using AniSync.Api;
using Microsoft.Extensions.Caching.Memory;
using AniSync.Interfaces;
using AniSync.Helpers;
using AniSync.Models.Mal;
using AniSync.Models;
using System.Globalization;
using System.Linq;
using AniSync.Configuration;
using System.Text.RegularExpressions;

namespace AniSync
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

            SyncHistory = new SyncHistoryManager(Path.Combine(applicationPaths.PluginsPath, "AniSync"), loggerFactory);
        }

        /// <summary>
        /// Gets episode thumbnail URL if available, falls back to MAL anime image
        /// </summary>
        private string? GetEpisodeThumbnailUrl(IShokoEpisode? episode, Anime? anime)
        {
            var image = episode?.GetPreferredImageForType(ImageEntityType.Backdrop)
                ?? episode?.GetImages(imageType: ImageEntityType.Backdrop)?.FirstOrDefault()
                ?? episode?.Series?.GetPreferredImageForType(ImageEntityType.Primary)
                ?? episode?.Series?.GetImages(imageType: ImageEntityType.Primary)?.FirstOrDefault();
            if (image == null)
                return null;

#pragma warning disable CS0618
            return $"/api/v3/Image/{image.Source}/{image.Type}/{image.LocalID}";
#pragma warning restore CS0618
        }

        private static DateTime? GetSeriesAirDate(ISeries? series)
        {
            if (series?.AirDate is not { } airDate || airDate.Year <= 0) return null;

            int month = airDate.Month is int m && m >= 1 ? m : 1;
            int day = airDate.Day is int d && d >= 1 ? d : 1;
            try { return new DateTime(airDate.Year, month, day); }
            catch { return new DateTime(airDate.Year, 1, 1); }
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

        private async Task<Anime?> FetchIdFromProvider(IApiCallHelpers apiCallHelpers, ApiName provider, IShokoEpisode episode, Config config, string? shokoUsername = null)
        {
            var cacheKey = $"search_{provider}_{episode.Series?.AnidbAnimeID ?? 0}_{shokoUsername ?? "default"}";

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
                            .Select(a => {
                                var malDate = ParseFullDate(a.StartDate ?? "");
                                return new {
                                    Anime = a,
                                    MalDate = malDate,
                                    DiffDays = seriesAirDate.HasValue && malDate.HasValue
                                        ? Math.Abs((malDate.Value - seriesAirDate.Value).TotalDays)
                                        : double.MaxValue
                                };
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

        private async Task<Anime?> GetIdFromOfflineDb(IApiCallHelpers apiCallHelpers, ApiName provider, IShokoEpisode episode, Config config, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? offlineDbIds, string? shokoUsername = null)
        {
            var providerId = provider == ApiName.AniList ? offlineDbIds?.Anilist : offlineDbIds?.Mal;
            if (offlineDbIds == null || providerId == null)
            {
                _logger.LogWarning("Could not get {Provider} ID from offline database for AniDB ID {AnidbId}, fetching from provider...", provider, episode.Series?.AnidbAnimeID ?? 0);
                return await FetchIdFromProvider(apiCallHelpers, provider, episode, config, shokoUsername);
            }
            var anime = await apiCallHelpers.GetAnime(providerId.Value, shokoUsername: shokoUsername);
            if (anime == null)
            {
                _logger.LogError("Could not get anime for {Provider} ID {ProviderId}, fetching from provider...", provider, providerId);
                return await FetchIdFromProvider(apiCallHelpers, provider, episode, config, shokoUsername);
            }
            return anime;
        }


        private async void OnEpisodeWatchedAsync(object? sender, EpisodeUserDataSavedEventArgs e)
        {
            try
            {
                var config = _configProvider.Load();

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

                if (!config.GetEnableAutoSync(shokoUser?.Username))
                {
                    _logger.LogDebug("Auto-sync is disabled for user {Username}, skipping", shokoUser?.Username);
                    return;
                }

                if (e.Reason.HasFlag(EpisodeUserDataSaveReason.Import))
                    return;

                const EpisodeUserDataSaveReason nonWatchReasons =
                    EpisodeUserDataSaveReason.IsFavorite |
                    EpisodeUserDataSaveReason.UserTags |
                    EpisodeUserDataSaveReason.UserRating;
                if (e.Reason != EpisodeUserDataSaveReason.None && (e.Reason & ~nonWatchReasons) == 0)
                {
                    _logger.LogDebug("Skipping sync for non-watch event: {Reason}", e.Reason);
                    return;
                }

                if (shokoUser == null)
                {
                    _logger.LogWarning("No user information available in episode event, skipping sync");
                    return;
                }

                var maxEpisode = e.Episode;
                if (maxEpisode?.Series == null)
                {
                    _logger.LogWarning("No episode or series in event");
                    return;
                }

                bool isWatched = e.UserData.LastPlayedAt.HasValue;
                int shokoEpisodeNumber = maxEpisode.EpisodeNumber;
                int playbackCount = e.UserData.PlaybackCount;

                _logger.LogInformation("Episode {EpisodeNumber} event: Reason={Reason}, PlaybackCount={Count}, IsWatched={Watched} for {Title}",
                    shokoEpisodeNumber, e.Reason, playbackCount, isWatched, maxEpisode.Series?.Title ?? "Unknown");

                var dedupeKey = $"watch_{shokoUser.Username}_{maxEpisode.AnidbEpisodeID}_{isWatched}_{playbackCount}";
                if (_memoryCache.TryGetValue(dedupeKey, out _))
                {
                    _logger.LogDebug("Skipping duplicate watch event for {Title} ep {Episode}",
                        maxEpisode.Series?.Title ?? "Unknown", shokoEpisodeNumber);
                    return;
                }
                _memoryCache.Set(dedupeKey, true, TimeSpan.FromSeconds(30));

                var providers = config.GetConnectedProviders(shokoUser.Username!);
                if (providers.Count == 0)
                {
                    _logger.LogWarning("No provider account configured for Shoko user {Username}, skipping sync", shokoUser.Username);
                    return;
                }

                var eventId = Guid.NewGuid().ToString();

                var anidbId = maxEpisode.Series?.AnidbAnimeID ?? 0;
                var offlineKey = $"offlinedb_{anidbId}";
                if (!_memoryCache.TryGetValue(offlineKey, out AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? offlineDbIds))
                {
                    offlineDbIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(
                        _httpClientFactory.CreateClient(), anidbId, AnimeOfflineDatabaseHelpers.Source.Anidb);
                    _memoryCache.Set(offlineKey, offlineDbIds, TimeSpan.FromHours(1));
                }

                foreach (var syncProvider in providers)
                {
                    try
                    {
                        await SyncEpisodeToProviderAsync(syncProvider, shokoUser, maxEpisode, isWatched, shokoEpisodeNumber, playbackCount, eventId, offlineDbIds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Sync to {Provider} failed for user {User}", syncProvider, shokoUser.Username);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing episode watch event");
            }
        }

        private async Task SyncEpisodeToProviderAsync(ApiName provider, IUser shokoUser, IShokoEpisode maxEpisode, bool isWatched, int shokoEpisodeNumber, int playbackCount, string eventId, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? offlineDbIds)
        {
            var config = _configProvider.Load();

            UserApiAuth? userAuth = config.GetAuthForShokoUser(shokoUser.Username!, provider);
            if (userAuth == null)
            {
                _logger.LogDebug("No {Provider} account for Shoko user {Username}, skipping that provider", provider, shokoUser.Username);
                return;
            }
            _logger.LogInformation("Using {Provider} account {Account} for Shoko user {ShokoUser}",
                provider, userAuth.Username, shokoUser.Username);

            if (userAuth.ExpiresAt.HasValue && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > userAuth.ExpiresAt.Value)
            {
                _logger.LogWarning("{Provider} token expired for user {User}, attempting refresh...", provider, shokoUser.Username);
                try
                {
                    var auth = new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache);
                    await auth.RefreshAccessToken(shokoUser.Username!);
                    config = _configProvider.Load();
                    userAuth = config.GetAuthForShokoUser(shokoUser.Username!, provider);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Token refresh failed for user {User}. Please re-authenticate via /anisync", shokoUser.Username);
                    return;
                }
            }

            IApiCallHelpers? apiCallHelpers = ProviderApiFactory.Create(provider, _httpClientFactory, _loggerFactory, _memoryCache, _configProvider, _applicationPaths);
            if (apiCallHelpers == null)
            {
                _logger.LogWarning("Provider {Provider} is not supported for sync, skipping", provider);
                return;
            }

            var anime = await GetIdFromOfflineDb(apiCallHelpers, provider, maxEpisode, config, offlineDbIds, shokoUser?.Username);
                if (anime != null)
                {
                    _logger.LogInformation("Found Anime: {Title} ({Id})", anime.Title, anime.Id);

                    bool syncOnlyCompleted = config.GetSyncOnlyCompleted(shokoUser?.Username);
                    if (syncOnlyCompleted && isWatched && anime.NumEpisodes > 0 && shokoEpisodeNumber < anime.NumEpisodes)
                    {
                        _logger.LogDebug("SyncOnlyCompleted is enabled and episode {Episode}/{Total} is not the last - skipping sync for {Title}",
                            shokoEpisodeNumber, anime.NumEpisodes, anime.Title);
                        return;
                    }

                    var animeWithStatus = await apiCallHelpers.GetAnime(anime.Id, alternativeId: anime.AlternativeId, getRelated: false, shokoUsername: shokoUser?.Username);

                    if (animeWithStatus?.MyListStatus != null)
                    {
                        var currentStatus = animeWithStatus.MyListStatus.Status;
                        var malEpisodeCount = animeWithStatus.MyListStatus.NumEpisodesWatched;
                        var totalEpisodes = animeWithStatus.NumEpisodes;
                        var isRewatching = animeWithStatus.MyListStatus.IsRewatching;
                        var currentRewatchCount = animeWithStatus.MyListStatus.RewatchCount ?? 0;

                        _logger.LogInformation("Current {Provider} Status - Episodes: {MalEpisodes}/{Total}, Status: {Status}, Rewatching: {Rewatching}",
                            provider, malEpisodeCount, totalEpisodes, currentStatus, isRewatching);

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
                            _logger.LogInformation("No {Provider} update needed for {Title} (episode {Episode}, list has {MalEpisode}, watched={Watched})",
                                provider, anime.Title, shokoEpisodeNumber, malEpisodeCount, isWatched);
                        }
                        else if (setRewatching == true)
                        {
                            _logger.LogInformation("Detected rewatch start for {Title} - setting is_rewatching, progress {Episode}", anime.Title, newEpisodeCount);
                        }
                        else if (numberOfTimesRewatched != null)
                        {
                            _logger.LogInformation("Completed rewatch #{Count} for {Title}", numberOfTimesRewatched, anime.Title);
                        }

                        if (shouldUpdate)
                        {
                            _logger.LogInformation("Updating {Provider} status for {Title}: Episodes: {Episodes}, Status: {Status}, Rewatching: {Rewatching}",
                                provider, anime.Title ?? "Unknown", newEpisodeCount, newStatus?.ToString() ?? "unchanged", setRewatching?.ToString() ?? "unchanged");

                            UpdateAnimeStatusResponse? updateResult = null;

                            var statusToUse = newStatus ?? currentStatus;

                            DateTime? startDate = null;
                            DateTime? endDate = null;

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

                            if (newEpisodeCount == 0 && malEpisodeCount > 0)
                            {
                                startDate = DateTime.MinValue;
                                endDate = DateTime.MinValue;
                                _logger.LogInformation("Clearing start and end dates for {Title} (unwatched completely)", anime.Title);
                            }

                            if (setRewatching == true && currentStatus == Status.Completed)
                            {
                                _logger.LogInformation("Starting rewatch for {Title}, preserving original dates", anime.Title);
                            }

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
                                _logger.LogInformation("Successfully updated {Provider} status for {Title}", provider, anime.Title);

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

                                if (SyncHistory != null)
                                    await SyncHistory.LogSyncAsync(
                                        shokoUser?.Username ?? "Unknown User",
                                        anime.Id,
                                        anime.Title ?? "Unknown",
                                        shokoEpisodeNumber,
                                        SyncActionHelper.GetActionText(syncAction),
                                        true,
                                        statusToUse,
                                        provider.ToString(),
                                        GetEpisodeThumbnailUrl(maxEpisode, anime),
                                        userAuth?.Username ?? "Unknown User",
                                        eventId: eventId
                                    );
                            }
                            else
                            {
                                _logger.LogError("Failed to update {Provider} status for {Title}", provider, anime.Title);
                            }
                        }
                    }
                    else
                    {
                        if (isWatched)
                        {
                            _logger.LogInformation("Adding {Title} to {Provider} list with episode {Episode}", anime.Title, provider, shokoEpisodeNumber);

                            var newStatus = (anime.NumEpisodes > 0 && shokoEpisodeNumber >= anime.NumEpisodes)
                                ? Status.Completed
                                : Status.Watching;

                            DateTime? startDate = DateTime.Now.Date;
                            DateTime? endDate = null;

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
                                _logger.LogInformation("Successfully added {Title} to {Provider} list", anime.Title, provider);

                                SyncAction syncAction = newStatus == Status.Completed ? SyncAction.Completed : SyncAction.AddedToList;
                                string? details = newStatus == Status.Completed
                                    ? "First time completion"
                                    : null;

                                if (SyncHistory != null)
                                    await SyncHistory.LogSyncAsync(
                                        shokoUser?.Username ?? "Unknown User",
                                        anime.Id,
                                        anime.Title ?? "Unknown",
                                        shokoEpisodeNumber,
                                        SyncActionHelper.GetActionText(syncAction),
                                        true,
                                        newStatus,
                                        provider.ToString(),
                                        GetEpisodeThumbnailUrl(maxEpisode, anime),
                                        userAuth?.Username ?? "Unknown User",
                                        eventId: eventId
                                    );
                            }
                            else
                            {
                                _logger.LogError("Failed to add {Title} to {Provider} list", anime.Title, provider);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("Anime not in {Provider} list and episode marked unwatched - skipping", provider);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Could not find anime for {Title}", maxEpisode.Series?.Title ?? "Unknown");
                }
        }

        private async void OnSeriesUserDataSavedAsync(object? sender, SeriesUserDataSavedEventArgs e)
        {
            try
            {
                if (!e.Reason.HasFlag(SeriesUserDataSaveReason.UserRating)) return;
                if (e.IsImport) return;
                var shokoUser = e.User;
                var series = e.Series;
                if (shokoUser == null || series == null) return;
                if (!e.UserData.HasUserRating) return;

                int score = (int)Math.Clamp(Math.Round((double)e.UserData.UserRating), 0, 10);
                _logger.LogInformation("Series rating event: {Title} = {Score}/10 for user {User}",
                    series.Title ?? "Unknown", score, shokoUser.Username);

                var config = _configProvider.Load();
                var providers = config.GetConnectedProviders(shokoUser.Username!);
                if (providers.Count == 0)
                {
                    _logger.LogDebug("No provider accounts for Shoko user {User}, skipping rating sync", shokoUser.Username);
                    return;
                }

                var anidbId = series.AnidbAnimeID;
                var offlineKey = $"offlinedb_{anidbId}";
                if (!_memoryCache.TryGetValue(offlineKey, out AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? offlineDbIds))
                {
                    offlineDbIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(
                        _httpClientFactory.CreateClient(), anidbId, AnimeOfflineDatabaseHelpers.Source.Anidb);
                    _memoryCache.Set(offlineKey, offlineDbIds, TimeSpan.FromHours(1));
                }

                var eventId = Guid.NewGuid().ToString();
                foreach (var syncProvider in providers)
                {
                    try
                    {
                        await SyncRatingToProviderAsync(syncProvider, shokoUser, series, score, offlineDbIds, eventId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Rating sync to {Provider} failed for user {User}", syncProvider, shokoUser.Username);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing series rating event");
            }
        }

        private async Task SyncRatingToProviderAsync(ApiName provider, IUser shokoUser, IShokoSeries series, int score, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? offlineDbIds, string eventId)
        {
            var config = _configProvider.Load();
            UserApiAuth? userAuth = config.GetAuthForShokoUser(shokoUser.Username!, provider);
            if (userAuth == null)
            {
                _logger.LogDebug("No {Provider} account for Shoko user {Username}, skipping rating sync", provider, shokoUser.Username);
                return;
            }

            IApiCallHelpers? apiCallHelpers = ProviderApiFactory.Create(provider, _httpClientFactory, _loggerFactory, _memoryCache, _configProvider, _applicationPaths);
            if (apiCallHelpers == null)
            {
                _logger.LogWarning("Provider {Provider} is not supported, skipping rating sync", provider);
                return;
            }

            var providerId = provider == ApiName.AniList ? offlineDbIds?.Anilist : offlineDbIds?.Mal;
            if (providerId == null)
            {
                _logger.LogWarning("No {Provider} ID for series {Title}, skipping rating sync", provider, series.Title ?? "Unknown");
                return;
            }

            var anime = await apiCallHelpers.GetAnime(providerId.Value, shokoUsername: shokoUser.Username);
            if (anime == null)
            {
                _logger.LogWarning("Could not load {Provider} anime {Id} for rating sync", provider, providerId);
                return;
            }

            if (anime.MyListStatus == null)
            {
                _logger.LogDebug("{Title} is not on the {Provider} list, skipping rating sync", series.Title ?? "Unknown", provider);
                return;
            }
            int currentProgress = anime.MyListStatus.NumEpisodesWatched;
            Status currentStatus = anime.MyListStatus.Status;
            var resp = await apiCallHelpers.UpdateAnime(anime.Id, currentProgress, currentStatus, score: score, shokoUsername: shokoUser.Username);
            if (resp != null)
            {
                _logger.LogInformation("Synced rating {Score}/10 for {Title} to {Provider}", score, series.Title ?? "Unknown", provider);
                await SyncHistory.LogSyncAsync(shokoUser.Username!, anime.Id, anime.Title ?? series.Title ?? "Unknown", 0, "Rated", true, resp.Status, provider.ToString(), null, userAuth.Username, eventId);
            }
            else
            {
                _logger.LogWarning("Rating update returned null for {Title} on {Provider}", series.Title ?? "Unknown", provider);
            }
        }

        Task IHostedService.StartAsync(CancellationToken cancellationToken)
        {
            _userDataService.EpisodeUserDataSaved += OnEpisodeWatchedAsync;
            _userDataService.SeriesUserDataSaved += OnSeriesUserDataSavedAsync;
            _logger.LogInformation("AniSync plugin started - listening for episode watch and series rating events");
            return Task.CompletedTask;
        }

        Task IHostedService.StopAsync(CancellationToken cancellationToken)
        {
            _userDataService.EpisodeUserDataSaved -= OnEpisodeWatchedAsync;
            _userDataService.SeriesUserDataSaved -= OnSeriesUserDataSavedAsync;
            return Task.CompletedTask;
        }
    }
}
