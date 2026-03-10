using Microsoft.Extensions.Logging;
using Shoko.AniSync.Configuration;
using Shoko.Abstractions.Plugin;
using System;
using System.Collections.Generic;

namespace Shoko.AniSync
{
    public class Plugin : IPlugin
    {
        public Guid ID => new("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
        public string Name => "AniSync";
        public string Description => "Syncs watch state from Shoko to MyAnimeList and other providers";
        public string EmbeddedThumbnailResourceName => string.Empty;
        public static Plugin? Instance { get; private set; }

        public Config Config { get; private set; } = null!;
        public string PluginDirectory { get; private set; } = string.Empty;

        public Plugin() {}

        public Plugin(IApplicationPaths applicationPaths, ILoggerFactory loggerFactory)
        {
            Instance = this;
            PluginDirectory = Path.Combine(applicationPaths.PluginsPath, Name);
            Config = new Config(Path.Combine(PluginDirectory, "config.json"));

            loggerFactory.CreateLogger<Plugin>().LogInformation("Plugin loaded");
        }
    }
}
