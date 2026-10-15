using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// The request bodies sent to Simkl.
/// </summary>
public sealed class SimklPayloadTests
{
    private static readonly Guid OtherSeries = new("aaaaaaaa-0000-0000-0000-000000000002");

    /// <summary>
    /// An add groups episodes by show and season, in order, and carries titles, years and watch times.
    /// </summary>
    [Fact]
    public void AddGroupsEpisodesByShowAndSeason()
    {
        var request = SimklPayload.BuildAdd(
        [
            Marks.Episode(2, 3),
            Marks.Movie("tt0181852"),
            Marks.Episode(1, 2),
            Marks.Episode(1, 1),
            Marks.Episode(1, 1, series: OtherSeries),
        ]);

        Assert.Equal(
            """
            {"movies":[{"ids":{"imdb":"tt0181852"},"title":"Movie tt0181852","year":2010,"watched_at":"2026-10-04T03:30:00Z"}],
            "shows":[
            {"ids":{"tvdb":"153021"},"title":"The Walking Dead","year":2010,"use_tvdb_anime_seasons":true,"seasons":[
            {"number":1,"episodes":[{"number":1,"watched_at":"2026-10-04T03:30:00Z"},{"number":2,"watched_at":"2026-10-04T03:30:00Z"}]},
            {"number":2,"episodes":[{"number":3,"watched_at":"2026-10-04T03:30:00Z"}]}]},
            {"ids":{"tvdb":"153021"},"title":"The Walking Dead","year":2010,"use_tvdb_anime_seasons":true,"seasons":[
            {"number":1,"episodes":[{"number":1,"watched_at":"2026-10-04T03:30:00Z"}]}]}]}
            """.ReplaceLineEndings(string.Empty),
            Serialize(request));
    }

    /// <summary>
    /// A file holding several episodes adds each of them, and an episode marked twice is sent once.
    /// </summary>
    [Fact]
    public void AddSendsEveryEpisodeOfAFileOnce()
    {
        var twoPart = Marks.Episode(1, 1) with { Episodes = [1, 2] };

        var request = SimklPayload.BuildAdd([twoPart, Marks.Episode(1, 2)]);

        var season = Assert.Single(Assert.Single(request.Shows!).Seasons);
        Assert.Equal([1, 2], season.Episodes.Select(e => e.Number));
    }

    /// <summary>
    /// A removal names IDs and explicit episodes only: no title or year that Simkl could match to another title.
    /// </summary>
    [Fact]
    public void RemoveSendsIdsAndExplicitEpisodesOnly()
    {
        var request = SimklPayload.BuildRemove([Marks.Episode(1, 2, played: false), Marks.Episode(1, 1, played: false)], [Marks.Movie("tt0181852", played: false)]);

        Assert.Equal(
            """
            {"movies":[{"ids":{"imdb":"tt0181852"}}],
            "shows":[{"ids":{"tvdb":"153021"},"use_tvdb_anime_seasons":true,"seasons":[
            {"number":1,"episodes":[{"number":1},{"number":2}]}]}]}
            """.ReplaceLineEndings(string.Empty),
            Serialize(request));
    }

    /// <summary>
    /// A removal with no completed movies leaves the movies list out entirely.
    /// </summary>
    [Fact]
    public void RemoveLeavesOutEmptyLists()
    {
        var request = SimklPayload.BuildRemove([Marks.Episode(1, 1, played: false)], []);

        Assert.Null(request.Movies);
        Assert.DoesNotContain("movies", Serialize(request), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing to remove builds an empty request, which the caller does not send.
    /// </summary>
    [Fact]
    public void RemoveOfNothingIsEmpty()
    {
        Assert.True(SimklPayload.BuildRemove([], []).IsEmpty);
        Assert.Equal("{}", Serialize(SimklPayload.BuildRemove([], [])));
    }

    /// <summary>
    /// A show without seasons would remove the whole show from the user's Simkl library, so it is refused.
    /// </summary>
    [Fact]
    public void RefusesARemovalOfAShowWithoutSeasons()
    {
        var wholeShow = new HistoryRequest(null, [new HistoryShow(new Dictionary<string, object> { ["tvdb"] = "1" }, null, null, true, [])]);

        Assert.Throws<InvalidOperationException>(() => SimklPayload.EnsureNoWholeShowRemoval(wholeShow));
    }

    /// <summary>
    /// A season without episodes would unmark the whole season, so it is refused.
    /// </summary>
    [Fact]
    public void RefusesARemovalOfASeasonWithoutEpisodes()
    {
        var wholeSeason = new HistoryRequest(null, [new HistoryShow(new Dictionary<string, object> { ["tvdb"] = "1" }, null, null, true, [new HistorySeason(1, [])])]);

        Assert.Throws<InvalidOperationException>(() => SimklPayload.EnsureNoWholeShowRemoval(wholeSeason));
    }

    /// <summary>
    /// A removal naming episodes passes the check.
    /// </summary>
    [Fact]
    public void AcceptsARemovalThatNamesEpisodes()
    {
        SimklPayload.EnsureNoWholeShowRemoval(SimklPayload.BuildRemove([Marks.Episode(3, 4, played: false)], []));
    }

    /// <summary>
    /// The movie lookup sends each movie's IDs with its type, in order.
    /// </summary>
    [Fact]
    public void LookupSendsIdsAndTypeInOrder()
    {
        var lookup = SimklPayload.BuildMovieLookup([Marks.Movie("tt2"), Marks.Movie("tt1")]);

        Assert.Equal("""[{"ids":{"imdb":"tt2"},"type":"movie"},{"ids":{"imdb":"tt1"},"type":"movie"}]""", JsonSerializer.Serialize(lookup, SimklPayload.JsonOptions));
    }

    /// <summary>
    /// A catalogue write addresses each title by its Simkl ID alone, one season, episodes in order and once each with
    /// the latest watch time, and leaves out episodes that were not placed.
    /// </summary>
    [Fact]
    public void CatalogueAddUsesSimklIdsAndNumbering()
    {
        var early = Marks.Episode(2, 3);
        var late = early with { WatchedAtUtc = Marks.WatchedAt.AddHours(1) };
        var request = SimklPayload.BuildCatalogWrite(
        [
            new EpisodePlacement(late, 3, 9, 3, "by position"),
            new EpisodePlacement(Marks.Episode(2, 2), 2, 9, 2, "by position"),
            new EpisodePlacement(early, 3, 9, 3, "by position"),
            new EpisodePlacement(Marks.Episode(3, 1), 1, 4, 1, "by coordinate"),
            new EpisodePlacement(Marks.Episode(2, 9), 9, 0, 0, "no fit"),
        ],
        adding: true);

        Assert.Equal(
            """{"shows":[{"ids":{"simkl":4},"use_tvdb_anime_seasons":false,"seasons":[{"number":1,"episodes":[{"number":1,"watched_at":"2026-10-04T03:30:00Z"}]}]},"""
            + """{"ids":{"simkl":9},"use_tvdb_anime_seasons":false,"seasons":[{"number":1,"episodes":[{"number":2,"watched_at":"2026-10-04T03:30:00Z"},{"number":3,"watched_at":"2026-10-04T04:30:00Z"}]}]}]}""",
            Serialize(request));
    }

    /// <summary>
    /// A catalogue removal carries no watch times, still names each episode, and is empty when nothing was placed.
    /// </summary>
    [Fact]
    public void CatalogueRemovalNamesEpisodesWithoutWatchTimes()
    {
        var removal = SimklPayload.BuildCatalogWrite([new EpisodePlacement(Marks.Episode(2, 2, played: false), 2, 9, 2, "by position")], adding: false);
        var nothing = SimklPayload.BuildCatalogWrite([new EpisodePlacement(Marks.Episode(2, 2, played: false), 2, 0, 0, "no fit")], adding: false);

        Assert.Equal("""{"shows":[{"ids":{"simkl":9},"use_tvdb_anime_seasons":false,"seasons":[{"number":1,"episodes":[{"number":2}]}]}]}""", Serialize(removal));
        Assert.True(nothing.IsEmpty);
    }

    private static string Serialize(HistoryRequest request) => JsonSerializer.Serialize(request, SimklPayload.JsonOptions);
}
