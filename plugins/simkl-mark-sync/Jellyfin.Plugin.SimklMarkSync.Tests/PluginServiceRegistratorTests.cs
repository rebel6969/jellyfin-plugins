using System;
using System.Linq;
using System.Net.Http;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// The plugin's registrations, resolved the way Jellyfin's host resolves them.
/// </summary>
public sealed class PluginServiceRegistratorTests
{
    /// <summary>
    /// The listener is registered as a hosted service, so the host starts it.
    /// </summary>
    [Fact]
    public void RegistersTheMarkListenerAsAHostedService()
    {
        using var provider = BuildProvider();

        Assert.Single(provider.GetServices<IHostedService>().OfType<MarkSyncService>());
    }

    /// <summary>
    /// The Simkl services resolve to one instance each, so the client's POST spacing covers every call.
    /// </summary>
    [Fact]
    public void ResolvesOneInstanceOfEachSimklService()
    {
        using var provider = BuildProvider();

        Assert.IsType<SimklClient>(provider.GetRequiredService<ISimklClient>());
        Assert.Same(provider.GetRequiredService<ISimklClient>(), provider.GetRequiredService<ISimklClient>());
        Assert.IsType<SimklCredentials>(provider.GetRequiredService<ISimklCredentials>());
        Assert.Same(provider.GetRequiredService<ISimklCredentials>(), provider.GetRequiredService<ISimklCredentials>());
    }

    /// <summary>
    /// Without a clock from the host, the system clock is used.
    /// </summary>
    [Fact]
    public void UsesTheSystemClock()
    {
        using var provider = BuildProvider();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    /// <summary>
    /// A clock the host already registered is kept.
    /// </summary>
    [Fact]
    public void KeepsAClockTheHostRegistered()
    {
        var clock = new FakeTimeProvider();

        using var provider = BuildProvider(services => services.AddSingleton<TimeProvider>(clock));

        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection>? hostRegistrations = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginConfigurationsPath).Returns("/config/data/plugins/configurations");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(paths.Object);
        services.AddSingleton(Mock.Of<IUserDataManager>());
        services.AddSingleton(Mock.Of<IPluginManager>());
        services.AddSingleton(Mock.Of<IHttpClientFactory>());
        hostRegistrations?.Invoke(services);
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
