using System.Collections.Generic;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// A Simkl anime title, as <c>GET /anime/{id}?extended=full</c> describes it. Simkl files each anime season (or
/// broadcast cour) as its own title, sharing the series' TVDB ID.
/// </summary>
/// <param name="SimklId">The title's Simkl ID.</param>
/// <param name="MappedTvdbSeasons">The TVDB seasons Simkl maps the title to.</param>
/// <param name="Relations">The related titles.</param>
public sealed record AnimeTitle(int SimklId, IReadOnlyList<int> MappedTvdbSeasons, IReadOnlyList<AnimeRelation> Relations)
{
    /// <summary>
    /// Gets the title's episodes, once they have been read.
    /// </summary>
    public IReadOnlyList<AnimeEpisode> Episodes { get; init; } = [];
}
