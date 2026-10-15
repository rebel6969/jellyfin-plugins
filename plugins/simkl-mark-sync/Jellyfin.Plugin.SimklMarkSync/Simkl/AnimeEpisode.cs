using System;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// An episode of a Simkl anime title, as <c>GET /anime/episodes/{id}</c> lists it.
/// </summary>
/// <param name="Number">The episode number within the title, counted from 1 (AniDB order).</param>
/// <param name="IsEpisode">Whether it is a regular episode; specials are not.</param>
/// <param name="Aired">When it aired, in the broadcaster's time zone, if Simkl knows.</param>
/// <param name="TvdbSeason">The TVDB season Simkl maps it to, if any.</param>
/// <param name="TvdbEpisode">The TVDB episode Simkl maps it to, if any.</param>
public sealed record AnimeEpisode(int Number, bool IsEpisode, DateTimeOffset? Aired, int? TvdbSeason, int? TvdbEpisode);
