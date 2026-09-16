using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
namespace Jellyfin.Plugin.EnglishSdh;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = false;
    public string[] UserIds { get; set; } = [];
}

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = new("36dab5c5-2396-4b09-9c63-a46c56288c68");
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "English SDH Preference";
    public override string Description => "Prefer English SDH subtitles for opted-in users; never change audio.";
    public override Guid Id => PluginId;
    public IEnumerable<PluginPageInfo> GetPages() =>
        [new() { Name = "EnglishSdh", EmbeddedResourcePath = "Jellyfin.Plugin.EnglishSdh.config.html" }];
}
