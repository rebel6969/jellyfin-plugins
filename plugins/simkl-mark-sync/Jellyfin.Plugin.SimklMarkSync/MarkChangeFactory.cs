using System;
using System.Linq;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// Turns Jellyfin's user-data save events into the marks this plugin sends to Simkl.
/// </summary>
public static class MarkChangeFactory
{
    /// <summary>
    /// The most episodes one file may span. A wider range is treated as bad metadata and only the first number is sent.
    /// </summary>
    public const int MaxEpisodesPerFile = 10;

    /// <summary>
    /// Captures a manual played or unplayed mark of a movie or episode.
    /// </summary>
    /// <remarks>
    /// Only <see cref="UserDataSaveReason.TogglePlayed"/> is a manual mark. Playback saves user data constantly with
    /// other reasons; the official Simkl plugin already sends playback. Marking a season or series raises one event per
    /// episode, so series and seasons themselves are ignored. Missing (virtual) episodes are ignored, as the Trakt
    /// plugin does, because there is no file for them.
    /// </remarks>
    /// <param name="e">The event Jellyfin raised.</param>
    /// <param name="seriesOf">Finds an episode's series.</param>
    /// <param name="nowUtc">The current time, used when the item has no last-played date.</param>
    /// <param name="logger">Receives the reason a manual mark cannot be sent.</param>
    /// <returns>The mark, or <c>null</c> when there is nothing to send.</returns>
    public static MarkChange? Create(UserDataSaveEventArgs e, Func<Episode, Series?> seriesOf, DateTime nowUtc, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(seriesOf);
        ArgumentNullException.ThrowIfNull(logger);

        if (e.SaveReason != UserDataSaveReason.TogglePlayed || e.UserData is null || e.Item is null || e.Item.IsVirtualItem)
        {
            return null;
        }

        var played = e.UserData.Played;
        var watchedAt = ToUtc(e.UserData.LastPlayedDate ?? nowUtc);
        switch (e.Item)
        {
            case Movie movie:
                var ids = SimklIds.FromProviderIds(movie.ProviderIds);
                if (ids.Count == 0)
                {
                    logger.LogWarning("Not sent to Simkl: movie {Name} ({ItemId}) has no IMDb, TMDB or other ID Simkl knows", movie.Name, movie.Id);
                    return null;
                }

                return new MovieMark(e.UserId, movie.Id, played, watchedAt, ids, movie.Name, movie.ProductionYear);
            case Episode episode:
                return CreateEpisodeMark(e, episode, seriesOf, played, watchedAt, logger);
            default:
                return null;
        }
    }

    private static EpisodeMark? CreateEpisodeMark(
        UserDataSaveEventArgs e,
        Episode episode,
        Func<Episode, Series?> seriesOf,
        bool played,
        DateTime watchedAt,
        ILogger logger)
    {
        if (episode.ParentIndexNumber is not int season || episode.IndexNumber is not int first)
        {
            logger.LogWarning("Not sent to Simkl: episode {Name} ({ItemId}) has no season or episode number", episode.Name, episode.Id);
            return null;
        }

        var series = seriesOf(episode);
        if (series is null)
        {
            logger.LogWarning("Not sent to Simkl: episode {Name} ({ItemId}) has no series", episode.Name, episode.Id);
            return null;
        }

        var showIds = SimklIds.FromProviderIds(series.ProviderIds);
        if (showIds.Count == 0)
        {
            logger.LogWarning("Not sent to Simkl: series {Name} ({ItemId}) has no TVDB, TMDB, IMDb or anime ID Simkl knows", series.Name, series.Id);
            return null;
        }

        var last = episode.IndexNumberEnd is int end && end > first && end - first < MaxEpisodesPerFile ? end : first;
        var numbers = Enumerable.Range(first, last - first + 1).ToArray();
        return new EpisodeMark(e.UserId, episode.Id, played, watchedAt, series.Id, showIds, series.Name, series.ProductionYear, season, numbers);
    }

    // Jellyfin stores user-data dates in UTC; one read back from its database has no kind.
    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
