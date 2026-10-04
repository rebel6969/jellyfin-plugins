using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Jellyfin.Plugin.EnglishSdh;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton(sp => new PlaybackHandler(
            sp.GetRequiredService<ISessionManager>(), sp.GetRequiredService<IMediaSourceManager>(),
            sp.GetRequiredService<ILogger<PlaybackHandler>>(), sp.GetRequiredService<IHostApplicationLifetime>(),
            () => Plugin.Instance?.Configuration));
        services.AddSingleton<IEventConsumer<PlaybackStartEventArgs>>(sp => sp.GetRequiredService<PlaybackHandler>());
        services.AddSingleton<IEventConsumer<PlaybackStopEventArgs>>(sp => sp.GetRequiredService<PlaybackHandler>());
    }
}
