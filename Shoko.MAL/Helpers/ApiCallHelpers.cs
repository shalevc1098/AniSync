using Shoko.AniSync.Api;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.Mal;
using Shoko.Abstractions.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Helpers
{
    public class ApiCallHelpers : IApiCallHelpers
    {
        private readonly MalApiCalls? _malApiCalls;
        private readonly AniListApiCalls? _aniListApiCalls;

        public ApiCallHelpers(MalApiCalls? malApiCalls = null, AniListApiCalls? aniListApiCalls = null)
        {
            _malApiCalls = malApiCalls;
            _aniListApiCalls = aniListApiCalls;
        }

        public async Task<List<Anime>?> SearchAnime(string query, bool updateNsfw = false, string? shokoUsername = null)
        {
            if (_malApiCalls != null)
                return await _malApiCalls.SearchAnime(query, new[] { "id", "title", "alternative_titles", "num_episodes", "status", "start_date" }, updateNsfw, shokoUsername);
            if (_aniListApiCalls != null)
                return await _aniListApiCalls.SearchAnime(query, null, updateNsfw, shokoUsername);

            return new List<Anime>();
        }

        public async Task<Anime?> GetAnime(int id, string? alternativeId = null, bool getRelated = false, string? shokoUsername = null)
        {
            if (_malApiCalls != null)
                return await _malApiCalls.GetAnime(id, new[] { "title", "alternative_titles", "start_date", "related_anime", "my_list_status{num_times_rewatched,start_date,finish_date}", "num_episodes" }, shokoUsername);
            if (_aniListApiCalls != null)
                return await _aniListApiCalls.GetAnime(id, null, shokoUsername);

            return null;
        }

        public async Task<UpdateAnimeStatusResponse?> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status,
            bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, int? score = null, string? alternativeId = null, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse? ids = null, bool? isShow = null, string? shokoUsername = null)
        {
            if (_malApiCalls != null)
                return await _malApiCalls.UpdateAnimeStatus(animeId, numberOfWatchedEpisodes, status, isRewatching, numberOfTimesRewatched, startDate, endDate, score, shokoUsername);
            if (_aniListApiCalls != null)
                return await _aniListApiCalls.UpdateAnimeStatus(animeId, numberOfWatchedEpisodes, status, isRewatching, numberOfTimesRewatched, startDate, endDate, score, shokoUsername);

            return null;
        }

        public async Task<MalApiCalls.User?> GetUser(string? shokoUsername = null)
        {
            if (_malApiCalls != null)
                return await _malApiCalls.GetUserInformation(shokoUsername);
            if (_aniListApiCalls != null)
                return await _aniListApiCalls.GetUserInformation(shokoUsername);

            return null;
        }

        public async Task<List<Anime>> GetAnimeList(Status status, int? userId = null, string? shokoUsername = null)
        {
            if (_malApiCalls != null)
            {
                var malAnimeList = await _malApiCalls.GetUserAnimeList(status, shokoUsername: shokoUsername);
                return malAnimeList?.Select(animeList => animeList.Anime).Where(anime => anime != null).Cast<Anime>().ToList() ?? new List<Anime>();
            }

            return new List<Anime>();
        }
    }
}
