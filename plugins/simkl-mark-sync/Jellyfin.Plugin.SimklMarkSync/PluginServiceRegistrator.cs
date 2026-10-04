using System;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// Registers the plugin's services.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<ISimklCredentials, SimklCredentials>();
        serviceCollection.AddSingleton<ISimklClient, SimklClient>();
        serviceCollection.AddSingleton<MarkSyncProcessor>();
        serviceCollection.AddHostedService<MarkSyncService>();
    }
}
