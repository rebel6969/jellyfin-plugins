using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// The body of Simkl's <c>POST /sync/history</c> and <c>POST /sync/history/remove</c>, which share one shape.
/// </summary>
/// <param name="Movies">The movies, or <c>null</c> for none.</param>
/// <param name="Shows">The shows, or <c>null</c> for none. Simkl takes anime here too.</param>
public sealed record HistoryRequest(IReadOnlyList<HistoryMovie>? Movies, IReadOnlyList<HistoryShow>? Shows)
{
    /// <summary>
    /// Gets a value indicating whether the request names no title at all.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty => (Movies is null || Movies.Count == 0) && (Shows is null || Shows.Count == 0);
}
