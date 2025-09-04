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

        private async Task<Anime> GetOva(int animeId, IReadOnlyList<AnimeTitle> episodeNames, string? alternativeId = null)
        {
            Anime anime = await ApiCallHelpers.GetAnime(animeId, getRelated: true, alternativeId: alternativeId);
            if (anime == null)
            {
                _logger.LogError($"Could not get anime for ID {animeId}");
                return null;
            }

            if (anime != null)
            {
                var listOfRelatedAnime = anime.RelatedAnime.Where(relation => relation.RelationType is AnimeRelationType.Side_Story or AnimeRelationType.Alternative_Version or AnimeRelationType.Alternative_Setting);
                foreach (RelatedAnime relatedAnime in listOfRelatedAnime)
                {
                    var detailedRelatedAnime = await ApiCallHelpers.GetAnime(relatedAnime.Anime.Id, alternativeId: relatedAnime.Anime.AlternativeId);
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

        private async Task<Anime?> FetchIdFromProvider(IShokoEpisode episode)
        {
            // Cache key for search results
            var cacheKey = $"mal_search_{episode.Series.AnidbAnimeID}";
            
            var uniqueAnimes = await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                
                var searchTasks = episode.Series.Titles.Select(t => ApiCallHelpers.SearchAnime(t.Title)).ToArray();
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
                _logger.LogDebug($"Found {candidates.Count} candidates for {episode.Series.Titles.First().Title}, selected best match with {candidates.First().DiffDays:0} days difference");
            }
            
            Anime? anime = candidates.FirstOrDefault()?.Anime;
            if (anime == null)
            {
                _logger.LogError($"Could not find anime for {episode.Series.Titles.First().Title}");
                return null;
            }
            
            // Only fetch full details if we don't have them cached and actually need related anime
            // For now, return the basic anime object as it contains most needed fields
            return anime;
        }

        private async Task<Anime?> GetIdFromOfflineDb(IShokoEpisode episode)
        {
            var offlineDbIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(), episode.Series.AnidbAnimeID, AnimeOfflineDatabaseHelpers.Source.Anidb);
            if (offlineDbIds == null || offlineDbIds.Mal == null)
            {
                _logger.LogWarning($"Could not get offline database IDs for episode {episode.Series.AnidbAnimeID}, fetching from provider...");
                return await FetchIdFromProvider(episode);
            }
            var malId = offlineDbIds.Mal;
            var anime = await ApiCallHelpers.GetAnime(malId.Value);
            if (anime == null)
            {
                _logger.LogError($"Could not get anime for MAL ID {malId}, fetching from provider...");
                return await FetchIdFromProvider(episode);
            }
            return anime;
        }

        private async void OnEpisodeWatchedAsync(object? sender, VideoUserDataSavedEventArgs e)
        {
            try
            {
                switch (Plugin.Instance.Config.SelectedProvider)
                {
                    case Configuration.ApiName.Mal:
                        ApiCallHelpers = new ApiCallHelpers(malApiCalls: new MalApiCalls(_httpClientFactory, _loggerFactory, _memoryCache, new Delayer()));
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
                    _logger.LogWarning($"No episodes found for {e.Video.MediaInfo.Title}");
                    return;
                }

                var anime = await GetIdFromOfflineDb(maxEpisode);
                if (anime != null)
                {
                    _logger.LogInformation($"Found Anime: {anime.Title} ({anime.Id})");
                    _logger.LogDebug($"Anime status: {anime.Title} ({anime.Id}) - Status: {anime.MyListStatus?.Status ?? "Not in list"}, Episodes watched: {anime.MyListStatus?.NumEpisodesWatched ?? 0}");
                }
                else
                {
                    _logger.LogWarning($"Could not find MAL anime for {maxEpisode.Series.PreferredTitle}");
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
