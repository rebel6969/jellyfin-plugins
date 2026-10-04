using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SimklMarkSync.Configuration;

/// <summary>
/// The plugin has no settings of its own; Jellyfin's plugin base class requires a configuration type.
/// </summary>
/// <remarks>
/// The Simkl login and the per-user "movies" and "shows" switches are read from the official Simkl plugin.
/// </remarks>
public sealed class PluginConfiguration : BasePluginConfiguration
{
}
