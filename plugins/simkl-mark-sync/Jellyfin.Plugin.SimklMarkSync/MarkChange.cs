using System;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// One played or unplayed mark: a manual mark captured when Jellyfin saved it, or a playback Jellyfin counted as
/// finished.
/// </summary>
/// <param name="UserId">The Jellyfin user who marked the item.</param>
/// <param name="ItemId">The Jellyfin item that was marked.</param>
/// <param name="Played">Whether the item is now played.</param>
/// <param name="WatchedAtUtc">When the item was last played, in UTC.</param>
public abstract record MarkChange(Guid UserId, Guid ItemId, bool Played, DateTime WatchedAtUtc)
{
    /// <summary>
    /// Gets a value indicating whether the mark comes from a playback Jellyfin counted as finished rather than from a
    /// manual mark. Only such a mark may be recorded as a rewatch.
    /// </summary>
    public bool FromPlayback { get; init; }
}
