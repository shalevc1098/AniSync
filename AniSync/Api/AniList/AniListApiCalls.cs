using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using AniSync.Configuration;
using AniSync.Interfaces;
using AniSync.Models.Mal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AniSync.Api
{
    /// <summary>
    /// AniList (GraphQL) provider. Maps AniList's MediaList model to the plugin's canonical
    /// MAL-shaped models so the rest of the plugin stays provider-neutral.
    /// </summary>
    public class AniListApiCalls
    {
        private readonly ILogger<AniListApiCalls> _logger;
        private readonly AuthApiCall _authApiCall;
        private const string GraphQlUrl = "https://graphql.anilist.co";

        public AniListApiCalls(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IMemoryCache memoryCache, IAsyncDelayer delayer, ConfigurationProvider<Config> configProvider, IApplicationPaths applicationPaths)
        {
            _logger = loggerFactory.CreateLogger<AniListApiCalls>();
            _authApiCall = new AuthApiCall(httpClientFactory, loggerFactory, memoryCache, delayer, configProvider, applicationPaths);
        }

        public async Task<MalApiCalls.User?> GetUserInformation(string? shokoUsername = null)
        {
            const string query = "query { Viewer { id name avatar { large } } }";
            using var doc = await PostGraphQl(query, null, shokoUsername);
            if (doc == null) return null;
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("Viewer", out var viewer) || viewer.ValueKind != JsonValueKind.Object)
                return null;
            return new MalApiCalls.User
            {
                Id = ReadInt(viewer, "id") ?? 0,
                Name = viewer.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty,
                Picture = viewer.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.Object && av.TryGetProperty("large", out var lg) ? lg.GetString() : null
            };
        }

        public async Task<List<Anime>> SearchAnime(string? query, string[]? fields = null, bool updateNsfw = false, string? shokoUsername = null)
        {
            var result = new List<Anime>();
            if (string.IsNullOrWhiteSpace(query)) return result;

            const string gql = "query ($q: String) { Page(perPage: 10) { media(search: $q, type: ANIME) { id episodes title { romaji english } startDate { year month day } } } }";
            using var doc = await PostGraphQl(gql, new Dictionary<string, object?> { ["q"] = query }, shokoUsername);
            if (doc == null) return result;
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("Page", out var page) || !page.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var m in media.EnumerateArray())
            {
                result.Add(new Anime
                {
                    Id = ReadInt(m, "id") ?? 0,
                    Title = ReadTitle(m),
                    NumEpisodes = ReadInt(m, "episodes") ?? 0,
                    StartDate = ReadFuzzyDate(m, "startDate")
                });
            }
            return result;
        }

        public async Task<Anime?> GetAnime(int animeId, string[]? fields = null, string? shokoUsername = null)
        {
            const string gql = "query ($id: Int) { Media(id: $id, type: ANIME) { id episodes title { romaji english } startDate { year month day } mediaListEntry { status progress repeat startedAt { year month day } completedAt { year month day } } } }";
            using var doc = await PostGraphQl(gql, new Dictionary<string, object?> { ["id"] = animeId }, shokoUsername);
            if (doc == null) return null;
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("Media", out var m) || m.ValueKind != JsonValueKind.Object)
                return null;

            var anime = new Anime
            {
                Id = ReadInt(m, "id") ?? animeId,
                Title = ReadTitle(m),
                NumEpisodes = ReadInt(m, "episodes") ?? 0,
                StartDate = ReadFuzzyDate(m, "startDate")
            };

            if (m.TryGetProperty("mediaListEntry", out var entry) && entry.ValueKind == JsonValueKind.Object)
            {
                var aniStatus = entry.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                var (canonical, isRewatching) = MapFromAniListStatus(aniStatus);
                anime.MyListStatus = new MyListStatus
                {
                    Status = canonical,
                    IsRewatching = isRewatching,
                    NumEpisodesWatched = ReadInt(entry, "progress") ?? 0,
                    RewatchCount = ReadInt(entry, "repeat") ?? 0,
                    StartDate = ReadFuzzyDate(entry, "startedAt"),
                    FinishDate = ReadFuzzyDate(entry, "completedAt")
                };
            }
            return anime;
        }

        public async Task<UpdateAnimeStatusResponse?> UpdateAnimeStatus(int animeId, int numberOfWatchedEpisodes, Status? status = null, bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, int? score = null, string? shokoUsername = null)
        {
            var variables = new Dictionary<string, object?>
            {
                ["mediaId"] = animeId,
                ["progress"] = numberOfWatchedEpisodes
            };

            if (isRewatching == true)
                variables["status"] = "REPEATING";
            else if (status != null)
                variables["status"] = MapToAniListStatus(status.Value);

            if (numberOfTimesRewatched != null)
                variables["repeat"] = numberOfTimesRewatched.Value;

            if (startDate != null)
                variables["startedAt"] = ToFuzzyDateInput(startDate.Value);
            if (endDate != null)
                variables["completedAt"] = ToFuzzyDateInput(endDate.Value);

            if (score != null)
                variables["score"] = (double)score.Value;

            const string gql = "mutation ($mediaId: Int, $progress: Int, $status: MediaListStatus, $repeat: Int, $startedAt: FuzzyDateInput, $completedAt: FuzzyDateInput, $score: Float) { SaveMediaListEntry(mediaId: $mediaId, progress: $progress, status: $status, repeat: $repeat, startedAt: $startedAt, completedAt: $completedAt, score: $score) { id status progress repeat score } }";
            using var doc = await PostGraphQl(gql, variables, shokoUsername);
            if (doc == null) return null;
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("SaveMediaListEntry", out var entry) || entry.ValueKind != JsonValueKind.Object)
                return null;

            var (canonical, _) = MapFromAniListStatus(entry.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null);
            return new UpdateAnimeStatusResponse
            {
                Status = canonical,
                NumEpisodesWatched = ReadInt(entry, "progress") ?? numberOfWatchedEpisodes,
                NumTimesRewatched = ReadInt(entry, "repeat") ?? 0
            };
        }

        public Task<List<UserAnimeListData>> GetUserAnimeList(Status status, string? shokoUsername = null)
            => Task.FromResult(new List<UserAnimeListData>());

        // --- mapping helpers ---

        public static string MapToAniListStatus(Status status) => status switch
        {
            Status.Watching => "CURRENT",
            Status.Completed => "COMPLETED",
            Status.Plan_to_watch => "PLANNING",
            Status.On_hold => "PAUSED",
            Status.Dropped => "DROPPED",
            _ => "CURRENT"
        };

        public static (Status status, bool isRewatching) MapFromAniListStatus(string? aniListStatus) => aniListStatus switch
        {
            "CURRENT" => (Status.Watching, false),
            "COMPLETED" => (Status.Completed, false),
            "PLANNING" => (Status.Plan_to_watch, false),
            "PAUSED" => (Status.On_hold, false),
            "DROPPED" => (Status.Dropped, false),
            "REPEATING" => (Status.Completed, true),
            _ => (Status.Watching, false)
        };

        private static object ToFuzzyDateInput(DateTime date)
        {
            if (date == DateTime.MinValue)
                return new { year = (int?)null, month = (int?)null, day = (int?)null };
            return new { year = (int?)date.Year, month = (int?)date.Month, day = (int?)date.Day };
        }

        private static string? ReadTitle(JsonElement media)
        {
            if (media.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                if (t.TryGetProperty("romaji", out var r) && r.ValueKind == JsonValueKind.String) return r.GetString();
                if (t.TryGetProperty("english", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
            }
            return null;
        }

        private static int? ReadInt(JsonElement parent, string prop)
            => parent.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var iv) ? iv : (int?)null;

        private static string? ReadFuzzyDate(JsonElement parent, string prop)
        {
            if (!parent.TryGetProperty(prop, out var d) || d.ValueKind != JsonValueKind.Object) return null;
            int? year = ReadInt(d, "year");
            if (year == null || year == 0) return null;
            int month = ReadInt(d, "month") is int mv && mv > 0 ? mv : 1;
            int day = ReadInt(d, "day") is int dv && dv > 0 ? dv : 1;
            return $"{year:D4}-{month:D2}-{day:D2}";
        }

        private async Task<JsonDocument?> PostGraphQl(string query, Dictionary<string, object?>? variables, string? shokoUsername)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { query, variables = variables ?? new Dictionary<string, object?>() });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var apiCall = await _authApiCall.AuthenticatedApiCall(ApiName.AniList, AuthApiCall.CallType.POST, GraphQlUrl, stringContent: content, shokoUsername: shokoUsername);
                if (apiCall == null) return null;
                using var streamReader = new StreamReader(await apiCall.Content.ReadAsStreamAsync());
                var text = await streamReader.ReadToEndAsync();
                var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    _logger.LogWarning("AniList GraphQL returned errors: {Errors}", errors.ToString());
                }
                return doc;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AniList GraphQL request failed");
                return null;
            }
        }
    }
}
