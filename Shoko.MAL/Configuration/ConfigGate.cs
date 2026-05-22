namespace Shoko.AniSync.Configuration
{
    // Serializes load-mutate-save cycles on the plugin config so concurrent watch
    // events, token refreshes, and settings saves don't clobber each other.
    public static class ConfigGate
    {
        public static readonly object Lock = new();
    }
}
