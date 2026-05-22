using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;

namespace AniSync
{
    public class PluginServiceRegistration : IPluginServiceRegistration
    {
        /// <inheritdoc />
        public static void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
        {
            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.AddHostedService<ShokoAniSyncPlugin>();
        }
    }

    public class PluginApplicationRegistration : IPluginApplicationRegistration
    {
        public static void RegisterServices(IApplicationBuilder application, IApplicationPaths applicationPaths) { }
    }
}
