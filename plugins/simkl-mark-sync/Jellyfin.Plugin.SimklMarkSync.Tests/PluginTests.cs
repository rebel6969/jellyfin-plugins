using System;
using System.IO;
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
        Assert.Same(plugin, Plugin.Instance);
    }

    /// <summary>
    /// The settings page is listed and its HTML is embedded under the listed name, with the rewatch switch on it.
    /// </summary>
    [Fact]
    public void ServesTheSettingsPage()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.PluginsPath).Returns("/config/data/plugins");
        paths.Setup(p => p.PluginConfigurationsPath).Returns("/config/data/plugins/configurations");
        var plugin = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());

        var page = Assert.Single(plugin.GetPages());
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        Assert.Contains("TrackRewatches", html, StringComparison.Ordinal);
        Assert.Contains(Plugin.PluginId.ToString(), html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The settings the sending path reads follow what was saved, off until switched on.
    /// </summary>
    [Fact]
    public void ReadsTheSavedRewatchSetting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "simkl-mark-sync-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new Mock<IApplicationPaths>();
            paths.Setup(p => p.PluginsPath).Returns(folder);
            paths.Setup(p => p.PluginConfigurationsPath).Returns(folder);
            var plugin = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());
            var settings = new PluginSettings();
            plugin.UpdateConfiguration(new Configuration.PluginConfiguration());
            Assert.False(settings.TrackRewatches);

            plugin.UpdateConfiguration(new Configuration.PluginConfiguration { TrackRewatches = true });

            Assert.True(settings.TrackRewatches);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
