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
        /// Username of the authenticated user.
        /// </summary>
        [JsonProperty("username")]
        public string Username { get; set; }
        
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
        
        /// <summary>
        /// The Shoko username associated with this MAL account.
        /// Not serialized to JSON since it's the key in the dictionary.
        /// </summary>
        [JsonIgnore]
        public string ShokoUsername { get; set; }
    }
}
