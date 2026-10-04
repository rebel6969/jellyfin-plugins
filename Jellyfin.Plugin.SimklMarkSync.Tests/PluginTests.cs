using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// The plugin class as Jellyfin's PluginManager creates and reads it.
/// </summary>
public sealed class PluginTests
{
    /// <summary>
    /// Created the way PluginManager.CreatePluginInstance does it, the instance carries every value the loader reads next.
    /// </summary>
    [Fact]
    public void CarriesWhatJellyfinsLoaderReadsAfterCreatingIt()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginsPath).Returns("/config/data/plugins");
        paths.Setup(p => p.PluginConfigurationsPath).Returns("/config/data/plugins/configurations");
        var services = new ServiceCollection()
            .AddSingleton(paths.Object)
            .AddSingleton(Mock.Of<IXmlSerializer>());
        using var provider = services.BuildServiceProvider();

        var plugin = ActivatorUtilities.CreateInstance<Plugin>(provider);

        Assert.NotNull(plugin.Version);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version!.ToString(), plugin.Version.ToString());
        Assert.Equal(typeof(Plugin).Assembly.Location, plugin.AssemblyFilePath);
        Assert.Equal(Plugin.PluginId, plugin.Id);
        Assert.Equal("Simkl Mark Sync", plugin.Name);
        Assert.False(string.IsNullOrEmpty(plugin.Description));
    }
}
