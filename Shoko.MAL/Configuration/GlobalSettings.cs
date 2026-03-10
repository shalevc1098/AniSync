using System.IO;
using System.Text.Json;

namespace Shoko.AniSync.Configuration
{
    public class GlobalSettings
    {
        public string? MalClientId { get; set; }
        public string? MalClientSecret { get; set; }

        private const string FileName = "global-settings.json";

        public static GlobalSettings? Load()
        {
            var dir = Plugin.Instance?.PluginDirectory;
            if (string.IsNullOrEmpty(dir)) return null;

            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GlobalSettings>(json);
        }

        public void Save()
        {
            var dir = Plugin.Instance?.PluginDirectory;
            if (string.IsNullOrEmpty(dir)) return;

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, FileName);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
    }

    public class GlobalSettingsRequest
    {
        public string? MalClientId { get; set; }
        public string? MalClientSecret { get; set; }
    }
}
