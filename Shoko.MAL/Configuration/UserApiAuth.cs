using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Configuration
{
    public enum ApiName
    {
        [Display(Name = "MyAnimeList")]
        Mal,
        [Display(Name = "AniList")]
        AniList,
        [Display(Name = "Kitsu")]
        Kitsu,
        [Display(Name = "Annict")]
        Annict,
        [Display(Name = "Shikimori")]
        Shikimori,
        [Display(Name = "Simkl")]
        Simkl
    }

    public class UserApiAuth
    {
        /// <summary>
        /// Name of the API provider.
        /// </summary>
        [JsonProperty("name")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ApiName Name { get; set; }
        /// <summary>
        /// Access token of the authenticated instance.
        /// </summary>
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }
        /// <summary>
        /// Refresh token of the authenticated instance.
        /// </summary>
        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }
    }
}
