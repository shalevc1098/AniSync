using System;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;

public class PluginServiceRegistration : IPluginServiceRegistration
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddHostedService<ShokoMalPlugin>();
    }
}
