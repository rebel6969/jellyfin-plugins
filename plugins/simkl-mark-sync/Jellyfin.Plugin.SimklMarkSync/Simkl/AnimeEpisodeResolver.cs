using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// Places anime episodes that Simkl could not match by TVDB numbering, using Simkl's own catalogue.
/// </summary>
/// <remarks>
/// Simkl routes a TVDB-numbered write (<c>use_tvdb_anime_seasons</c>) through the TVDB season and episode it stores on
/// each episode of its anime titles. When that stored coordinate is missing or wrong (for example a new season's later
/// episodes still filed under TVDB season 1), the write lands in <c>not_found</c>. The episode is then looked up among
/// the titles Simkl files under the series' TVDB ID, and placed only when exactly one episode fits:
/// <list type="number">
/// <item>its stored coordinate is the episode's TVDB season and number; or</item>
/// <item>it sits at the episode's position in a title mapped to that TVDB season (the offset is taken from the title's
/// episodes filed under that season, which must all agree), its stored coordinate names no other episode of the title's
/// own seasons, and it aired within <see cref="AirDateTolerance"/> of Jellyfin's premiere date.</item>
/// </list>
/// Anything else stays unplaced, so no other episode is ever marked in its place.
/// </remarks>
public sealed class AnimeEpisodeResolver
{
    /// <summary>
    /// The most Simkl titles read for one series; a series with more is left unplaced.
    /// </summary>
    public const int MaxTitles = 10;

    /// <summary>
    /// How far Simkl's air date may be from Jellyfin's premiere date, which can differ by a time zone.
    /// </summary>
    public static readonly TimeSpan AirDateTolerance = TimeSpan.FromDays(1);

    private readonly ISimklClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeEpisodeResolver"/> class.
    /// </summary>
    /// <param name="client">The Simkl client that reads the catalogue.</param>
    public AnimeEpisodeResolver(ISimklClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Places the episodes of one season of one series.
    /// </summary>
    /// <param name="marks">The marks, all of one series and season.</param>
    /// <param name="clientId">The client ID.</param>
    /// <param name="user">The user's login.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One placement for each episode number of each mark.</returns>
    public async Task<IReadOnlyList<EpisodePlacement>> PlaceAsync(IReadOnlyList<EpisodeMark> marks, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marks);
        if (marks.Count == 0)
        {
            return [];
        }

        var first = marks[0];
        if (marks.Any(m => m.SeriesId != first.SeriesId || m.Season != first.Season))
        {
            throw new ArgumentException("All marks must be of one series and one season.", nameof(marks));
        }

        if (!first.ShowIds.TryGetValue("tvdb", out var tvdbValue)
            || Convert.ToString(tvdbValue, CultureInfo.InvariantCulture) is not { Length: > 0 } tvdb)
        {
            return Unplaced(marks, "the series has no TVDB ID, so Simkl's titles for it cannot be found");
        }

        var (titles, failure) = await ReadTitlesAsync(tvdb, first.Season, clientId, user, cancellationToken).ConfigureAwait(false);
        return titles is null
            ? Unplaced(marks, failure)
            : marks.SelectMany(m => m.Episodes.Select(number => Place(m, number, titles))).ToList();
    }

    /// <summary>
    /// Places one episode among Simkl titles whose episodes have been read.
    /// </summary>
    /// <param name="mark">The mark the episode belongs to.</param>
    /// <param name="tvdbEpisode">The episode's number in its TVDB season.</param>
    /// <param name="titles">The Simkl titles, with their episodes.</param>
    /// <returns>The placement.</returns>
    public static EpisodePlacement Place(EpisodeMark mark, int tvdbEpisode, IReadOnlyList<AnimeTitle> titles)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(titles);
        var season = mark.Season;
        var inSeason = titles.Where(t => t.MappedTvdbSeasons.Contains(season)).ToList();
        if (inSeason.Count == 0)
        {
            return Unplaced(mark, tvdbEpisode, $"no Simkl title is mapped to TVDB season {season}");
        }

        var filed = inSeason
            .SelectMany(t => t.Episodes
                .Where(e => e.IsEpisode && e.TvdbSeason == season && e.TvdbEpisode == tvdbEpisode)
                .Select(e => (Title: t, Episode: e)))
            .ToList();
        if (filed.Count == 1)
        {
            return new EpisodePlacement(mark, tvdbEpisode, filed[0].Title.SimklId, filed[0].Episode.Number, "by its TVDB season and episode");
        }

        if (filed.Count > 1)
        {
            return Unplaced(mark, tvdbEpisode, $"{filed.Count} Simkl episodes are filed under TVDB S{season}E{tvdbEpisode}");
        }

        if (mark.PremiereDate is not DateTime premiere)
        {
            return Unplaced(mark, tvdbEpisode, "Jellyfin has no premiere date to confirm its position by");
        }

        var byPosition = inSeason
            .Select(t => (Title: t, Episode: AtPosition(t, season, tvdbEpisode)))
            .Where(c => c.Episode is not null && AiredNear(c.Episode, premiere))
            .ToList();
        return byPosition.Count switch
        {
            1 => new EpisodePlacement(mark, tvdbEpisode, byPosition[0].Title.SimklId, byPosition[0].Episode!.Number, "by its position in the season and its air date"),
            0 => Unplaced(mark, tvdbEpisode, $"no Simkl episode is filed under TVDB S{season}E{tvdbEpisode} or sits at its position with its air date"),
            _ => Unplaced(mark, tvdbEpisode, $"{byPosition.Count} Simkl titles have an episode at its position with its air date"),
        };
    }

    // The episode at a TVDB episode's position in a title, by the offset that the title's episodes filed under that
    // season share. An episode Simkl files under another episode of the title's own seasons is a real mapping, not a
    // stale one, so it is not taken.
    private static AnimeEpisode? AtPosition(AnimeTitle title, int season, int tvdbEpisode)
    {
        var offsets = title.Episodes
            .Where(e => e.IsEpisode && e.TvdbSeason == season && e.TvdbEpisode is not null)
            .Select(e => e.Number - e.TvdbEpisode!.Value)
            .Distinct()
            .ToList();
        if (offsets.Count != 1)
        {
            return null;
        }

        var number = tvdbEpisode + offsets[0];
        var found = title.Episodes.Where(e => e.IsEpisode && e.Number == number).ToList();
        if (found.Count != 1)
        {
            return null;
        }

        var episode = found[0];
        return episode.TvdbSeason is int filedSeason && title.MappedTvdbSeasons.Contains(filedSeason) ? null : episode;
    }

    // Simkl dates an episode in the broadcaster's time zone; Jellyfin keeps the premiere as a date.
    private static bool AiredNear(AnimeEpisode episode, DateTime premiere) =>
        episode.Aired is DateTimeOffset aired
        && (aired.Date - premiere.Date).Duration() <= AirDateTolerance;

    private static EpisodePlacement Unplaced(EpisodeMark mark, int tvdbEpisode, string reason) =>
        new(mark, tvdbEpisode, 0, 0, reason);

    private static List<EpisodePlacement> Unplaced(IReadOnlyList<EpisodeMark> marks, string reason) =>
        marks.SelectMany(m => m.Episodes.Select(number => Unplaced(m, number, reason))).ToList();

    // Reads the titles Simkl files under the series' TVDB ID, following relations that share it, then the episodes of
    // those mapped to the season. A failed read, or more titles than MaxTitles, places nothing.
    private async Task<(List<AnimeTitle>? Titles, string Failure)> ReadTitlesAsync(string tvdb, int season, string clientId, SimklUser user, CancellationToken cancellationToken)
    {
        var start = await _client.FindAnimeAsync("tvdb", tvdb, clientId, user, cancellationToken).ConfigureAwait(false);
        if (start is null)
        {
            return (null, "Simkl's catalogue could not be read");
        }

        if (start.Count == 0)
        {
            return (null, $"Simkl files no anime under TVDB ID {tvdb}");
        }

        var queue = new Queue<int>(start);
        var seen = new HashSet<int>(start);
        var titles = new List<AnimeTitle>();
        while (queue.Count > 0)
        {
            if (titles.Count == MaxTitles)
            {
                return (null, $"Simkl files more than {MaxTitles} anime titles under TVDB ID {tvdb}");
            }

            var title = await _client.GetAnimeAsync(queue.Dequeue(), clientId, user, cancellationToken).ConfigureAwait(false);
            if (title is null)
            {
                return (null, "Simkl's catalogue could not be read");
            }

            titles.Add(title);
            foreach (var relation in title.Relations)
            {
                if (string.Equals(relation.Tvdb, tvdb, StringComparison.Ordinal) && seen.Add(relation.SimklId))
                {
                    queue.Enqueue(relation.SimklId);
                }
            }
        }

        var inSeason = new List<AnimeTitle>();
        foreach (var title in titles.Where(t => t.MappedTvdbSeasons.Contains(season)))
        {
            var episodes = await _client.GetAnimeEpisodesAsync(title.SimklId, clientId, user, cancellationToken).ConfigureAwait(false);
            if (episodes is null)
            {
                return (null, "Simkl's catalogue could not be read");
            }

            inSeason.Add(title with { Episodes = episodes });
        }

        return (inSeason, string.Empty);
    }
}
