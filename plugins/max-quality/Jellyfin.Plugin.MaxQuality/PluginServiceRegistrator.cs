using System;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MaxQuality;

/// <summary>
/// Registers the plugin's services and its global MVC action filter.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        serviceCollection.AddSingleton(_ => new PlaybackRoundTracker(TimeProvider.System));
        serviceCollection.AddSingleton<MaxQualityActionFilter>();
        serviceCollection.AddHostedService<PlaybackActivityListener>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<MaxQualityActionFilter>());
    }
}
