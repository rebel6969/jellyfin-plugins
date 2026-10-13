using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SimklMarkSync.Configuration;

/// <summary>
/// The plugin's own settings.
/// </summary>
/// <remarks>
/// The Simkl login and the per-user "movies" and "shows" switches are read from the official Simkl plugin.
/// </remarks>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether a finished playback of something already watched is recorded on Simkl
    /// as a rewatch. Simkl keeps rewatches only for PRO and VIP accounts; on a free account the playback is sent as a
    /// plain watch. Off by default, as Simkl asks of apps.
    /// </summary>
    public bool TrackRewatches { get; set; }
}
