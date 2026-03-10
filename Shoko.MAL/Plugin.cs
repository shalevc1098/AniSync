using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Utilities;
using System;

namespace Shoko.AniSync
{
    public class Plugin : IPlugin
    {
        public Guid ID => UuidUtility.GetV5(GetType().FullName!);
        public string Name => "AniSync";
        public string Description => "Syncs watch state from Shoko to MyAnimeList and other providers";
        public string EmbeddedThumbnailResourceName => string.Empty;
    }
}
