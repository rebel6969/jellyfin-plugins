using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Turning Jellyfin's user-data save events into marks.
/// </summary>
public sealed class MarkChangeFactoryTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 4, 0, 0, DateTimeKind.Utc);

    private readonly ListLogger<MarkChangeFactoryTests> _logger = new();

    private readonly Series _series = new()
    {
        Id = Marks.Series,
        Name = "The Walking Dead",
        ProductionYear = 2010,
        ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "153021", ["TvdbSlug"] = "the-walking-dead" },
    };

    /// <summary>
    /// A movie marked played is captured with its IDs, title, year and last-played time.
    /// </summary>
    [Fact]
    public void CapturesAMovieMarkedPlayed()
    {
        var movie = Movie();

        var mark = Assert.IsType<MovieMark>(Create(movie, played: true, Marks.WatchedAt));

        Assert.Equal(Marks.User, mark.UserId);
        Assert.Equal(movie.Id, mark.ItemId);
        Assert.True(mark.Played);
        Assert.Equal(Marks.WatchedAt, mark.WatchedAtUtc);
        Assert.Equal(new Dictionary<string, object> { ["imdb"] = "tt1375666", ["tmdb"] = "27205" }, mark.Ids);
        Assert.Equal("Inception", mark.Title);
        Assert.Equal(2010, mark.Year);
        Assert.Empty(_logger.Entries);
    }

    /// <summary>
    /// A movie marked unplayed is captured as unplayed.
    /// </summary>
    [Fact]
    public void CapturesAMovieMarkedUnplayed()
    {
        Assert.False(Assert.IsType<MovieMark>(Create(Movie(), played: false, Marks.WatchedAt)).Played);
    }

    /// <summary>
    /// An episode is captured with its series' IDs, title and year, its season and its episode number.
    /// </summary>
    [Fact]
    public void CapturesAnEpisodeWithItsSeries()
    {
        var episode = Episode(season: 2, number: 5);

        var mark = Assert.IsType<EpisodeMark>(Create(episode, played: true, Marks.WatchedAt));

        Assert.Equal(episode.Id, mark.ItemId);
        Assert.Equal(Marks.Series, mark.SeriesId);
        Assert.Equal(new Dictionary<string, object> { ["tvdb"] = "153021" }, mark.ShowIds);
        Assert.Equal("The Walking Dead", mark.ShowTitle);
        Assert.Equal(2010, mark.ShowYear);
        Assert.Equal(2, mark.Season);
        Assert.Equal([5], mark.Episodes);
    }

    /// <summary>
    /// A file holding episodes 1 to 3 sends all three.
    /// </summary>
    [Fact]
    public void CapturesEveryEpisodeOfAMultiEpisodeFile()
    {
        var episode = Episode(season: 1, number: 1, end: 3);

        Assert.Equal([1, 2, 3], Assert.IsType<EpisodeMark>(Create(episode, true, null)).Episodes);
    }

    /// <summary>
    /// A range of exactly <see cref="MarkChangeFactory.MaxEpisodesPerFile"/> episodes is sent whole.
    /// </summary>
    [Fact]
    public void CapturesTheWidestPlausibleRange()
    {
        var episode = Episode(season: 1, number: 1, end: MarkChangeFactory.MaxEpisodesPerFile);

        Assert.Equal(MarkChangeFactory.MaxEpisodesPerFile, Assert.IsType<EpisodeMark>(Create(episode, true, null)).Episodes.Count);
    }

    /// <summary>
    /// A wider range is treated as bad metadata: only the first number is sent.
    /// </summary>
    [Fact]
    public void SendsOnlyTheFirstNumberOfAnImplausibleRange()
    {
        var episode = Episode(season: 1, number: 1, end: MarkChangeFactory.MaxEpisodesPerFile + 1);

        Assert.Equal([1], Assert.IsType<EpisodeMark>(Create(episode, true, null)).Episodes);
    }

    /// <summary>
    /// An end number before the start number is ignored.
    /// </summary>
    [Fact]
    public void IgnoresAnEndBeforeTheStart()
    {
        var episode = Episode(season: 1, number: 4, end: 2);

        Assert.Equal([4], Assert.IsType<EpisodeMark>(Create(episode, true, null)).Episodes);
    }

    /// <summary>
    /// Only a manual mark is sent; playback and other saves are not.
    /// </summary>
    /// <param name="reason">The save reason.</param>
    [Theory]
    [InlineData(UserDataSaveReason.PlaybackStart)]
    [InlineData(UserDataSaveReason.PlaybackProgress)]
    [InlineData(UserDataSaveReason.PlaybackFinished)]
    [InlineData(UserDataSaveReason.UpdateUserRating)]
    [InlineData(UserDataSaveReason.Import)]
    [InlineData(UserDataSaveReason.UpdateUserData)]
    public void IgnoresSavesThatAreNotManualMarks(UserDataSaveReason reason)
    {
        var e = Event(Movie(), played: true, Marks.WatchedAt);
        e.SaveReason = reason;

        Assert.Null(MarkChangeFactory.Create(e, _ => _series, Now, _logger));
        Assert.Empty(_logger.Entries);
    }

    /// <summary>
    /// A missing episode has no file, so it is not sent, as the Trakt plugin does.
    /// </summary>
    [Fact]
    public void IgnoresMissingEpisodes()
    {
        var episode = Episode(season: 1, number: 1);
        episode.IsVirtualItem = true;

        Assert.Null(Create(episode, true, null));
    }

    /// <summary>
    /// Marking a series raises one event per episode, so the series' own event is ignored.
    /// </summary>
    [Fact]
    public void IgnoresSeriesAndSeasons()
    {
        Assert.Null(Create(_series, true, null));
        Assert.Null(Create(new Season { ParentIndexNumber = 1 }, true, null));
        Assert.Empty(_logger.Entries);
    }

    /// <summary>
    /// An event without user data or an item is ignored.
    /// </summary>
    [Fact]
    public void IgnoresAnIncompleteEvent()
    {
        var noData = Event(Movie(), true, null);
        noData.UserData = null;
        var noItem = Event(Movie(), true, null);
        noItem.Item = null;

        Assert.Null(MarkChangeFactory.Create(noData, _ => _series, Now, _logger));
        Assert.Null(MarkChangeFactory.Create(noItem, _ => _series, Now, _logger));
    }

    /// <summary>
    /// A movie with no ID Simkl knows is not sent, and the reason is logged.
    /// </summary>
    [Fact]
    public void SkipsAMovieWithoutIdsAndSaysWhy()
    {
        var movie = Movie();
        movie.ProviderIds = new Dictionary<string, string> { ["TmdbCollection"] = "1" };

        Assert.Null(Create(movie, true, null));
        Assert.Contains("movie Inception", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An episode without a season number is not sent, and the reason is logged.
    /// </summary>
    [Fact]
    public void SkipsAnEpisodeWithoutASeasonNumber()
    {
        var episode = Episode(season: 1, number: 1);
        episode.ParentIndexNumber = null;

        Assert.Null(Create(episode, true, null));
        Assert.Contains("no season or episode number", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An episode without an episode number is not sent.
    /// </summary>
    [Fact]
    public void SkipsAnEpisodeWithoutAnEpisodeNumber()
    {
        var episode = Episode(season: 1, number: 1);
        episode.IndexNumber = null;

        Assert.Null(Create(episode, true, null));
        Assert.Contains("no season or episode number", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An episode whose series cannot be found is not sent.
    /// </summary>
    [Fact]
    public void SkipsAnEpisodeWithoutASeries()
    {
        var e = Event(Episode(season: 1, number: 1), true, null);

        Assert.Null(MarkChangeFactory.Create(e, _ => null, Now, _logger));
        Assert.Contains("has no series", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An episode whose series has no ID Simkl knows is not sent.
    /// </summary>
    [Fact]
    public void SkipsAnEpisodeWhoseSeriesHasNoIds()
    {
        _series.ProviderIds = new Dictionary<string, string> { ["TvdbSlug"] = "the-walking-dead" };

        Assert.Null(Create(Episode(season: 1, number: 1), true, null));
        Assert.Contains("series The Walking Dead", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A date read back from Jellyfin's database has no kind and is UTC.
    /// </summary>
    [Fact]
    public void TreatsAStoredDateWithoutAKindAsUtc()
    {
        var stored = new DateTime(2026, 10, 4, 3, 30, 0, DateTimeKind.Unspecified);

        var mark = Create(Movie(), true, stored)!;

        Assert.Equal(DateTimeKind.Utc, mark.WatchedAtUtc.Kind);
        Assert.Equal(Marks.WatchedAt, mark.WatchedAtUtc);
    }

    /// <summary>
    /// Without a last-played date the mark uses the current time.
    /// </summary>
    [Fact]
    public void UsesNowWithoutALastPlayedDate()
    {
        Assert.Equal(Now, Create(Movie(), true, null)!.WatchedAtUtc);
    }

    private static Movie Movie() => new()
    {
        Id = Marks.ItemId("inception"),
        Name = "Inception",
        ProductionYear = 2010,
        ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1375666", ["Tmdb"] = "27205", ["TmdbCollection"] = "1" },
    };

    private static Episode Episode(int season, int number, int? end = null) => new()
    {
        Id = Marks.ItemId($"episode-{season}-{number}"),
        Name = $"Episode {number}",
        ParentIndexNumber = season,
        IndexNumber = number,
        IndexNumberEnd = end,
        SeriesId = Marks.Series,
    };

    private static UserDataSaveEventArgs Event(BaseItem item, bool played, DateTime? lastPlayed) => new()
    {
        UserId = Marks.User,
        Item = item,
        SaveReason = UserDataSaveReason.TogglePlayed,
        UserData = new UserItemData { Key = "key", Played = played, LastPlayedDate = lastPlayed },
    };

    private MarkChange? Create(BaseItem item, bool played, DateTime? lastPlayed) =>
        MarkChangeFactory.Create(Event(item, played, lastPlayed), _ => _series, Now, _logger);
}
