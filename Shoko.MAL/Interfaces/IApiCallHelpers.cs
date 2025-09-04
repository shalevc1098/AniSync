using Shoko.AniSync.Api;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Models.Mal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Interfaces
{
    public interface IApiCallHelpers
    {
        Task<List<Anime>> SearchAnime(string query);
        Task<Anime> GetAnime(int id, string alternativeId = null, bool getRelated = false);
        Task<UpdateAnimeStatusResponse> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status,
            bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string alternativeId = null, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids = null, bool? isShow = null);

        Task<MalApiCalls.User> GetUser();
        Task<List<Anime>> GetAnimeList(Status status, int? userId = null);
    }
}
