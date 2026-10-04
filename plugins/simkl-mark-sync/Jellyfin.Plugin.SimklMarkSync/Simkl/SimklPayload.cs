using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SimklMarkSync.Simkl;

/// <summary>
/// Builds the bodies this plugin sends to Simkl.
/// </summary>
public static class SimklPayload
{
    /// <summary>
    /// The JSON options for every request body: snake_case names and no null fields.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds the body that marks items watched.
    /// </summary>
    /// <param name="marks">The played marks.</param>
    /// <returns>The request, with titles, years and watch times as Simkl's fallback for matching.</returns>
    public static HistoryRequest BuildAdd(IReadOnlyCollection<MarkChange> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var movies = marks.OfType<MovieMark>()
            .Select(m => new HistoryMovie(m.Ids, m.Title, m.Year, ToIso(m.WatchedAtUtc)))
            .ToList();
        var shows = BuildShows(marks.OfType<EpisodeMark>(), adding: true);
        return new HistoryRequest(NullIfEmpty(movies), NullIfEmpty(shows));
    }

    /// <summary>
    /// Builds the body that unmarks items.
    /// </summary>
    /// <remarks>
    /// IDs only: no title or year, so an ID Simkl cannot match lands in <c>not_found</c> instead of falling back to a
    /// title match that could pick another title. Every show lists its episodes, because Simkl removes a show sent
    /// without seasons and episodes from the user's library entirely. Movies are removed whole, which is the only
    /// way Simkl unmarks a movie, so the caller passes only movies Simkl reports as completed.
    /// </remarks>
    /// <param name="episodes">The unplayed episodes.</param>
    /// <param name="completedMovies">The unplayed movies that Simkl reports as completed.</param>
    /// <returns>The request.</returns>
    public static HistoryRequest BuildRemove(IReadOnlyCollection<EpisodeMark> episodes, IReadOnlyCollection<MovieMark> completedMovies)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(completedMovies);
        var movies = completedMovies.Select(m => new HistoryMovie(m.Ids, null, null, null)).ToList();
        var request = new HistoryRequest(NullIfEmpty(movies), NullIfEmpty(BuildShows(episodes, adding: false)));
        EnsureNoWholeShowRemoval(request);
        return request;
    }

    /// <summary>
    /// Builds the lookup that asks Simkl whether each movie is in the user's library.
    /// </summary>
    /// <param name="movies">The movies.</param>
    /// <returns>One lookup item per movie, in the same order.</returns>
    public static IReadOnlyList<WatchedLookupItem> BuildMovieLookup(IReadOnlyList<MovieMark> movies)
    {
        ArgumentNullException.ThrowIfNull(movies);
        return movies.Select(m => new WatchedLookupItem(m.Ids, "movie")).ToList();
    }

    /// <summary>
    /// Throws unless every show in a removal names explicit episodes.
    /// </summary>
    /// <param name="request">The removal request.</param>
    /// <exception cref="InvalidOperationException">A show or season lists no episodes.</exception>
    public static void EnsureNoWholeShowRemoval(HistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var show in request.Shows ?? [])
        {
            if (show.Seasons is null || show.Seasons.Count == 0 || show.Seasons.Any(s => s.Episodes is null || s.Episodes.Count == 0))
            {
                throw new InvalidOperationException(
                    "Refusing to send a removal that names a show or season without episodes: Simkl would remove all of it.");
            }
        }
    }

    private static List<HistoryShow> BuildShows(IEnumerable<EpisodeMark> marks, bool adding)
    {
        return marks
            .GroupBy(m => m.SeriesId)
            .Select(series =>
            {
                var first = series.First();
                var seasons = series
                    .GroupBy(m => m.Season)
                    .OrderBy(season => season.Key)
                    .Select(season => new HistorySeason(
                        season.Key,
                        season
                            .SelectMany(m => m.Episodes.Select(number => (Number: number, m.WatchedAtUtc)))
                            .GroupBy(e => e.Number)
                            .OrderBy(e => e.Key)
                            .Select(e => new HistoryEpisode(e.Key, adding ? ToIso(e.Max(x => x.WatchedAtUtc)) : null))
                            .ToList()))
                    .ToList();
                return new HistoryShow(
                    first.ShowIds,
                    adding ? first.ShowTitle : null,
                    adding ? first.ShowYear : null,
                    UseTvdbAnimeSeasons: true,
                    seasons);
            })
            .ToList();
    }

    private static List<T>? NullIfEmpty<T>(List<T> items) => items.Count == 0 ? null : items;

    private static string ToIso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
