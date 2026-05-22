using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using Shoko.AniSync.Api;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Interfaces;

namespace Shoko.AniSync.Helpers
{
    /// <summary>
    /// Builds the right <see cref="IApiCallHelpers"/> backend for a given provider.
    /// </summary>
    public static class ProviderApiFactory
    {
        public static IApiCallHelpers? Create(
            ApiName provider,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            IMemoryCache memoryCache,
            ConfigurationProvider<Config> configProvider,
            IApplicationPaths applicationPaths)
        {
            switch (provider)
            {
                case ApiName.Mal:
                    return new ApiCallHelpers(malApiCalls: new MalApiCalls(httpClientFactory, loggerFactory, memoryCache, new Delayer(), configProvider, applicationPaths));
                case ApiName.AniList:
                    return new ApiCallHelpers(aniListApiCalls: new AniListApiCalls(httpClientFactory, loggerFactory, memoryCache, new Delayer(), configProvider, applicationPaths));
                default:
                    return null;
            }
        }

        public static bool TryParseProvider(string? name, out ApiName provider)
        {
            return System.Enum.TryParse(name, ignoreCase: true, out provider) && System.Enum.IsDefined(typeof(ApiName), provider);
        }
    }
}
