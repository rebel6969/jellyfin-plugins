namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// Where one episode of a mark sits in Simkl's catalogue, or why it could not be placed.
/// </summary>
/// <param name="Mark">The mark the episode belongs to.</param>
/// <param name="TvdbEpisode">The episode's number in its TVDB season, as Jellyfin has it.</param>
/// <param name="SimklId">The Simkl title holding the episode; 0 when it could not be placed.</param>
/// <param name="SimklEpisode">The episode's number within that title; 0 when it could not be placed.</param>
/// <param name="Detail">How the episode was placed, or why it could not be.</param>
public sealed record EpisodePlacement(EpisodeMark Mark, int TvdbEpisode, int SimklId, int SimklEpisode, string Detail)
{
    /// <summary>
    /// Gets a value indicating whether the episode was placed.
    /// </summary>
    public bool IsPlaced => SimklId > 0 && SimklEpisode > 0;
}
