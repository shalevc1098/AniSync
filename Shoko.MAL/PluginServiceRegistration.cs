using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Shoko.AniSync.Controllers;
using Shoko.Plugin.Abstractions;
using System;
using System.Reflection;

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
            
            // Add session support for remembering authenticated users
            services.AddDistributedMemoryCache();
            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromDays(30); // Session persists for 30 days
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.Name = ".Shoko.MAL.Session";
            });
            
            // Note: Shoko's internal services like IUserDataService should be automatically available
            // through the DI container if they're registered in Shoko Server

            // Add MVC with Views support
            var assembly = typeof(AniSyncController).Assembly;
            var mvcBuilder = services.AddControllersWithViews();
            
            mvcBuilder.AddApplicationPart(assembly);

            services.AddHostedService<ShokoMalPlugin>();
        }
    }
}
