using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// A show in a history request, always with explicit seasons and episodes.
/// </summary>
/// <param name="Ids">The show's identifiers.</param>
/// <param name="Title">The title; sent only when adding.</param>
/// <param name="Year">The year the show started; sent only when adding.</param>
/// <param name="UseTvdbAnimeSeasons">
/// Tells Simkl the season and episode numbers are TVDB-style, as Jellyfin's are. Simkl documents it as a no-op for
/// shows that are not anime.
/// </param>
/// <param name="Seasons">The seasons, each listing its episodes.</param>
public sealed record HistoryShow(
    IReadOnlyDictionary<string, object> Ids,
    string? Title,
    int? Year,
    bool UseTvdbAnimeSeasons,
    IReadOnlyList<HistorySeason> Seasons);
