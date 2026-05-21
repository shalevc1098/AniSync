using Shoko.Abstractions.Plugin;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shoko.AniSync.Configuration
{
    public class GlobalSettings
    {
        public string? MalClientId { get; set; }
        public string? MalClientSecret { get; set; }

        // HMAC key used to sign OAuth `state` so the callback can trust the embedded
        // Shoko username. Generated once and persisted; never sent to the client.
        public string? StateSigningKey { get; set; }

        private const string FileName = "global-settings.json";

        /// <summary>
        /// Returns the persisted OAuth state-signing key, generating and saving one on first use.
        /// </summary>
        public static byte[] GetOrCreateStateSigningKey(IApplicationPaths applicationPaths)
        {
            var gs = Load(applicationPaths) ?? new GlobalSettings();
            if (string.IsNullOrEmpty(gs.StateSigningKey))
            {
                gs.StateSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                gs.Save(applicationPaths);
            }
            return Convert.FromBase64String(gs.StateSigningKey);
        }

        public static GlobalSettings? Load(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.PluginsPath, "AniSync");
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GlobalSettings>(json);
        }

        public void Save(IApplicationPaths applicationPaths)
        {
            var dir = Path.Combine(applicationPaths.PluginsPath, "AniSync");

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
