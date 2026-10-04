namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// An episode in a history request.
/// </summary>
/// <param name="Number">The episode number within its season.</param>
/// <param name="WatchedAt">When it was watched, as ISO-8601 UTC; sent only when adding.</param>
public sealed record HistoryEpisode(int Number, string? WatchedAt);
