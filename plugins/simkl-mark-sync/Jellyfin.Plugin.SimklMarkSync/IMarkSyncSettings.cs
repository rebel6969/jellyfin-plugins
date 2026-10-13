namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// The plugin settings the sending path reads, at the moment it sends.
/// </summary>
public interface IMarkSyncSettings
{
    /// <summary>
    /// Gets a value indicating whether finished playbacks may be recorded as rewatches.
    /// </summary>
    bool TrackRewatches { get; }
}
