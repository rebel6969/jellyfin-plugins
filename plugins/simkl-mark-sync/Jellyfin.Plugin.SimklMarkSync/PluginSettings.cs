namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// Reads the settings saved on the plugin's configuration page, so a change applies to the next batch without a
/// restart.
/// </summary>
public sealed class PluginSettings : IMarkSyncSettings
{
    /// <inheritdoc />
    public bool TrackRewatches => Plugin.Instance?.Configuration.TrackRewatches ?? false;
}
