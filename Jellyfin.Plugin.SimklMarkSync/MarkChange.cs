using System;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// One manual played or unplayed mark, captured when Jellyfin saved it.
/// </summary>
/// <param name="UserId">The Jellyfin user who marked the item.</param>
/// <param name="ItemId">The Jellyfin item that was marked.</param>
/// <param name="Played">Whether the item is now played.</param>
/// <param name="WatchedAtUtc">When the item was last played, in UTC.</param>
public abstract record MarkChange(Guid UserId, Guid ItemId, bool Played, DateTime WatchedAtUtc);
