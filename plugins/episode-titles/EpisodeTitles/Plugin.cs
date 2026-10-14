using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
namespace Jellyfin.Plugin.EpisodeTitles;

// Nothing to configure; Jellyfin sets a plugin's Version only through the generic BasePlugin<T> constructor.
public sealed class PluginConfiguration : BasePluginConfiguration
{
}

public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    public static readonly Guid PluginId = new("c63652fb-8ecd-4791-928b-d769b21b8b08");
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
    }

    public override string Name => "Episode Titles";
    public override string Description =>
        "Replaces placeholder episode names (\"Episode 3\", \"TBA\", the file name) with the first real title among the " +
        "library's metadata providers, in their configured order, and re-checks such episodes until a title arrives.";
    public override Guid Id => PluginId;
}
