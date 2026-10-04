using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Moq;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// A loaded plugin instance whose assembly holds the <see cref="Jellyfin.Plugin.Simkl.API.SimklApi"/> fixture.
/// </summary>
public sealed class FakeSimklPlugin : BasePlugin<BasePluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FakeSimklPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    public FakeSimklPlugin(IApplicationPaths applicationPaths)
        : base(applicationPaths, Mock.Of<IXmlSerializer>())
    {
    }

    /// <inheritdoc />
    public override string Name => "Simkl";
}
