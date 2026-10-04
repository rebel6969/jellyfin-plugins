using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// An episode marked played or unplayed. One file can hold several episodes.
/// </summary>
/// <param name="UserId">The Jellyfin user who marked the episode.</param>
/// <param name="ItemId">The Jellyfin episode.</param>
/// <param name="Played">Whether the episode is now played.</param>
/// <param name="WatchedAtUtc">When the episode was last played, in UTC.</param>
/// <param name="SeriesId">The Jellyfin series the episode belongs to.</param>
/// <param name="ShowIds">The series' identifiers, keyed by Simkl's ID names.</param>
/// <param name="ShowTitle">The series' title.</param>
/// <param name="ShowYear">The year the series started.</param>
/// <param name="Season">The season number.</param>
/// <param name="Episodes">The episode numbers the file holds, in order.</param>
public sealed record EpisodeMark(
    Guid UserId,
    Guid ItemId,
    bool Played,
    DateTime WatchedAtUtc,
    Guid SeriesId,
    IReadOnlyDictionary<string, object> ShowIds,
    string? ShowTitle,
    int? ShowYear,
    int Season,
    IReadOnlyList<int> Episodes)
    : MarkChange(UserId, ItemId, Played, WatchedAtUtc);
