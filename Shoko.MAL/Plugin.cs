using Microsoft.Extensions.Logging;
using Shoko.AniSync.Configuration;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.Services;
using System;
using System.Collections.Generic;

namespace Shoko.AniSync
{
    public class Plugin : IPlugin, IPluginSettings
    {
        public string Name => "AniSync";
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

        public void Load() {}
        public void OnSettingsLoaded(IPluginSettings settings) {}
    }
}
