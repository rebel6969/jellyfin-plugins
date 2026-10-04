using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// A movie marked played or unplayed.
/// </summary>
/// <param name="UserId">The Jellyfin user who marked the movie.</param>
/// <param name="ItemId">The Jellyfin movie.</param>
/// <param name="Played">Whether the movie is now played.</param>
/// <param name="WatchedAtUtc">When the movie was last played, in UTC.</param>
/// <param name="Ids">The movie's identifiers, keyed by Simkl's ID names.</param>
/// <param name="Title">The movie's title.</param>
/// <param name="Year">The movie's release year.</param>
public sealed record MovieMark(
    Guid UserId,
    Guid ItemId,
    bool Played,
    DateTime WatchedAtUtc,
    IReadOnlyDictionary<string, object> Ids,
    string? Title,
    int? Year)
    : MarkChange(UserId, ItemId, Played, WatchedAtUtc);
