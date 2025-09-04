using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Configuration
{
    public class Config
    {
        private readonly string _filePath;

        [JsonProperty("plan_to_watch_only")]
        public bool PlanToWatchOnly { get; set; } = true;

        [JsonProperty("rewatch_completed")]
        public bool RewatchCompleted { get; set; } = true;
        [JsonProperty("update_nsfw")]
        public bool UpdateNsfw { get; set; } = false;

        [JsonProperty("selected_provider")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ApiName SelectedProvider { get; set; } = ApiName.Mal;

        [JsonProperty("auths")]
        public UserApiAuth[] UserApiAuth { get; set; } = [];

        public Config(string filePath)
        {
            _filePath = filePath;
            EnsureDirectoryExists();

            if (!File.Exists(_filePath))
            {
                Save();
            } else
            {
                var json = File.ReadAllText(_filePath);
                JsonConvert.PopulateObject(json, this);
            }
        }

        public void Save()
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(_filePath, json);
        }

        private void EnsureDirectoryExists()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
