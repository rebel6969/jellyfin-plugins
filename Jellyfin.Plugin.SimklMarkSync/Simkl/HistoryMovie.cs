using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// A movie in a history request.
/// </summary>
/// <param name="Ids">The movie's identifiers.</param>
/// <param name="Title">The title; sent only when adding, as Simkl's fallback when no ID matches.</param>
/// <param name="Year">The release year; sent only when adding.</param>
/// <param name="WatchedAt">When it was watched, as ISO-8601 UTC; sent only when adding.</param>
public sealed record HistoryMovie(IReadOnlyDictionary<string, object> Ids, string? Title, int? Year, string? WatchedAt);
