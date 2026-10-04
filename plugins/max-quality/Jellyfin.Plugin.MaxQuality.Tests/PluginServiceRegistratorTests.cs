using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MaxQuality.Tests;

/// <summary>
/// The plugin's registrations, resolved the way Jellyfin's host resolves them.
/// </summary>
public sealed class PluginServiceRegistratorTests
{
    /// <summary>
    /// The filter is added to MVC's global filters, so it runs for Jellyfin's own controllers.
    /// </summary>
    [Fact]
    public void AddsTheFilterToEveryMvcAction()
    {
        using var provider = BuildProvider();

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;

        Assert.Contains(filters, f => f is ServiceFilterAttribute s && s.ServiceType == typeof(MaxQualityActionFilter));
        Assert.NotNull(provider.GetRequiredService<MaxQualityActionFilter>());
    }

    /// <summary>
    /// The playback report listener is registered as a hosted service, so the host starts it.
    /// </summary>
    [Fact]
    public void RegistersThePlaybackListenerAsAHostedService()
    {
        using var provider = BuildProvider();

        Assert.Single(provider.GetServices<IHostedService>().OfType<PlaybackActivityListener>());
    }

    /// <summary>
    /// The filter and the listener share one tracker, so reports extend the rounds the filter decides on.
    /// </summary>
    [Fact]
    public void FilterAndListenerShareOneTracker()
    {
        using var provider = BuildProvider();

        Assert.Same(provider.GetRequiredService<PlaybackRoundTracker>(), provider.GetRequiredService<PlaybackRoundTracker>());
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(Mock.Of<ILibraryManager>());
        services.AddSingleton(Mock.Of<ISessionManager>());
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        return services.BuildServiceProvider(validateScopes: true);
    }
}
