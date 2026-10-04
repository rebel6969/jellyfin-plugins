using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// A season of a show in a history request.
/// </summary>
/// <param name="Number">The season number.</param>
/// <param name="Episodes">The episodes.</param>
public sealed record HistorySeason(int Number, IReadOnlyList<HistoryEpisode> Episodes);
