using Microsoft.Extensions.DependencyInjection;
using Shoko.AniSync.Controllers;
using Shoko.Plugin.Abstractions;

namespace Shoko.AniSync
{
    public class PluginServiceRegistration : IPluginServiceRegistration
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
        {
            services.AddHttpClient();
            services.AddHttpContextAccessor();
            services.AddMemoryCache();

            var mvc = services.AddControllers();
            mvc.AddApplicationPart(typeof(AniSyncController).Assembly).AddControllersAsServices();

            services.AddHostedService<ShokoMalPlugin>();
        }
    }
}
