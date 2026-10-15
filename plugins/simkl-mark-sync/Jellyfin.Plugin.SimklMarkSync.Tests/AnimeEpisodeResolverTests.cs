using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Placing anime episodes that Simkl could not match by TVDB numbering.
/// </summary>
public sealed class AnimeEpisodeResolverTests
{
    private const string ClientId = "fixture-client-id";
    private const string Tvdb = "432815";

    private static readonly SimklUser User = new("token-a", syncMovies: true, syncShows: true);
    private static readonly DateTime Premiere = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ISimklClient> _client = new(MockBehavior.Strict);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// An episode Simkl files under the episode's TVDB season and number is placed there, with no air date needed.
    /// </summary>
    [Fact]
    public void PlacesAnEpisodeFiledUnderItsTvdbCoordinate()
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1), Ep(5, 2, 5)) };

        var placement = AnimeEpisodeResolver.Place(Mark(2, 5, withPremiere: false), 5, titles);

        Assert.True(placement.IsPlaced);
        Assert.Equal((7, 5), (placement.SimklId, placement.SimklEpisode));
        Assert.Equal("by its TVDB season and episode", placement.Detail);
    }

    /// <summary>
    /// Two episodes filed under the same TVDB coordinate leave the episode unplaced.
    /// </summary>
    [Fact]
    public void LeavesAnEpisodeUnplacedWhenTwoAreFiledUnderItsCoordinate()
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1)), Title(8, [2], Ep(1, 2, 1)) };

        var placement = AnimeEpisodeResolver.Place(Mark(2, 1), 1, titles);

        Assert.False(placement.IsPlaced);
        Assert.Contains("2 Simkl episodes", placement.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case found on 2026-10-15: Simkl files episode 2 of the season-2 title under TVDB season 1. It sits at the
    /// episode's position, aired on its premiere date (Japan time), and is placed there.
    /// </summary>
    [Fact]
    public void PlacesAStaleFiledEpisodeByPositionAndAirDate()
    {
        var titles = new[] { Title(2330432, [2], Ep(1, 2, 1, "2026-10-08T00:45:00+09:00"), Ep(2, 1, 2, "2026-10-15T00:45:00+09:00")) };

        var placement = AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles);

        Assert.True(placement.IsPlaced);
        Assert.Equal((2330432, 2), (placement.SimklId, placement.SimklEpisode));
        Assert.Equal("by its position in the season and its air date", placement.Detail);
    }

    /// <summary>
    /// A placement by position needs Jellyfin's premiere date.
    /// </summary>
    [Fact]
    public void LeavesAnEpisodeUnplacedByPositionWithoutAPremiereDate()
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1, "2026-10-08T00:45:00+09:00"), Ep(2, 1, 2, "2026-10-15T00:45:00+09:00")) };

        var placement = AnimeEpisodeResolver.Place(Mark(2, 2, withPremiere: false), 2, titles);

        Assert.False(placement.IsPlaced);
        Assert.Contains("no premiere date", placement.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The air date may differ from the premiere date by one day (time zones), not more.
    /// </summary>
    /// <param name="aired">Simkl's air date.</param>
    /// <param name="placed">Whether the episode is placed.</param>
    [Theory]
    [InlineData("2026-10-14T23:30:00-05:00", true)]
    [InlineData("2026-10-16T00:45:00+09:00", true)]
    [InlineData("2026-10-13T23:59:00+09:00", false)]
    [InlineData("2026-10-17T00:45:00+09:00", false)]
    public void AcceptsAnAirDateWithinOneDayOfThePremiere(string aired, bool placed)
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1), Ep(2, 1, 2, aired)) };

        Assert.Equal(placed, AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles).IsPlaced);
    }

    /// <summary>
    /// An episode at the position that Simkl files under an episode of another season the same title covers is a real
    /// mapping (here the title's episode 2 is TVDB S3E1), so it is not taken.
    /// </summary>
    [Fact]
    public void DoesNotTakeAnEpisodeFiledUnderAnotherSeasonOfTheTitle()
    {
        var titles = new[] { Title(7, [2, 3], Ep(1, 2, 1), Ep(2, 3, 1, "2026-10-15T00:45:00+09:00")) };

        Assert.False(AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles).IsPlaced);
    }

    /// <summary>
    /// An episode at the position that Simkl files under another episode of the same season breaks the shared offset,
    /// so no position is derived.
    /// </summary>
    [Fact]
    public void DoesNotTakeAnEpisodeFiledUnderAnotherEpisodeOfTheSeason()
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1), Ep(2, 2, 5, "2026-10-15T00:45:00+09:00")) };

        Assert.False(AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles).IsPlaced);
    }

    /// <summary>
    /// An episode with no TVDB coordinate at all may be placed by position and air date.
    /// </summary>
    [Fact]
    public void PlacesAnEpisodeWithNoCoordinateByPositionAndAirDate()
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1), Ep(2, null, null, "2026-10-15T00:45:00+09:00")) };

        Assert.Equal(2, AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles).SimklEpisode);
    }

    /// <summary>
    /// When the title's episodes filed under the season disagree on the offset, no position can be derived.
    /// </summary>
    [Fact]
    public void LeavesAnEpisodeUnplacedWhenTheOffsetsDisagree()
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1), Ep(3, 2, 4), Ep(2, 1, 2, "2026-10-15T00:45:00+09:00")) };

        Assert.False(AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles).IsPlaced);
    }

    /// <summary>
    /// A title whose later part starts mid-season (a cour split) places by its own offset.
    /// </summary>
    [Fact]
    public void PlacesByTheOffsetOfACourSplitTitle()
    {
        var partOne = Title(10, [3], Enumerable.Range(1, 12).Select(n => Ep(n, 3, n)).ToArray());
        var partTwo = Title(11, [3], Ep(1, 3, 13), Ep(2, 3, 14), Ep(3, 1, 3, "2026-10-15T00:45:00+09:00"));

        var placement = AnimeEpisodeResolver.Place(Mark(3, 15), 15, [partOne, partTwo]);

        Assert.Equal((11, 3), (placement.SimklId, placement.SimklEpisode));
    }

    /// <summary>
    /// Two titles with a fitting episode at the position leave the episode unplaced.
    /// </summary>
    [Fact]
    public void LeavesAnEpisodeUnplacedWhenTwoTitlesFitByPosition()
    {
        var titles = new[]
        {
            Title(7, [2], Ep(1, 2, 1), Ep(2, 1, 2, "2026-10-15T00:45:00+09:00")),
            Title(8, [2], Ep(1, 2, 1), Ep(2, 1, 2, "2026-10-15T00:45:00+09:00")),
        };

        var placement = AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles);

        Assert.False(placement.IsPlaced);
        Assert.Contains("2 Simkl titles", placement.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A special is never placed, by coordinate or by position.
    /// </summary>
    [Fact]
    public void NeverPlacesASpecial()
    {
        var titles = new[] { Title(7, [2], Ep(1, 2, 1), new AnimeEpisode(2, IsEpisode: false, DateTimeOffset.Parse("2026-10-15T00:45:00+09:00", System.Globalization.CultureInfo.InvariantCulture), 2, 2)) };

        Assert.False(AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles).IsPlaced);
    }

    /// <summary>
    /// Titles not mapped to the episode's TVDB season are never used.
    /// </summary>
    [Fact]
    public void UsesOnlyTitlesMappedToTheSeason()
    {
        var titles = new[] { Title(7, [1], Ep(1, 1, 1), Ep(2, 2, 2, "2026-10-15T00:45:00+09:00")) };

        var placement = AnimeEpisodeResolver.Place(Mark(2, 2), 2, titles);

        Assert.False(placement.IsPlaced);
        Assert.Contains("no Simkl title is mapped to TVDB season 2", placement.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A series without a TVDB ID is left unplaced without any call.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LeavesASeriesWithoutATvdbIdUnplacedWithoutACall()
    {
        var mark = Mark(2, 2) with { ShowIds = new Dictionary<string, object> { ["anidb"] = "17944" } };

        var placement = Assert.Single(await Resolver().PlaceAsync([mark], ClientId, User, Token));

        Assert.False(placement.IsPlaced);
        Assert.Contains("no TVDB ID", placement.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed search, or one finding no anime, leaves every episode unplaced.
    /// </summary>
    /// <param name="found">Whether the search answered with no anime (true) or failed (false).</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeavesEpisodesUnplacedWhenTheSearchFindsNoAnime(bool found)
    {
        Search(found ? [] : null);

        var placements = await Resolver().PlaceAsync([Mark(2, 2), Mark(2, 3)], ClientId, User, Token);

        Assert.Equal(2, placements.Count);
        Assert.All(placements, p => Assert.False(p.IsPlaced));
        Assert.All(placements, p => Assert.Contains(found ? "no anime under TVDB ID 432815" : "could not be read", p.Detail, StringComparison.Ordinal));
    }

    /// <summary>
    /// The walk follows only relations filed under the same TVDB series, reads episodes only of titles mapped to the
    /// season, and places from them.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task WalksRelationsOfTheSameSeriesAndReadsOnlyTheSeasonsTitles()
    {
        Search([1]);
        Anime(Title(1, [1]) with { Relations = [new AnimeRelation(2, Tvdb), new AnimeRelation(3, "999")] });
        Anime(Title(2, [2]) with { Relations = [new AnimeRelation(1, Tvdb)] });
        Episodes(2, Ep(1, 2, 1, "2026-10-08T00:45:00+09:00"), Ep(2, 1, 2, "2026-10-15T00:45:00+09:00"));

        var placement = Assert.Single(await Resolver().PlaceAsync([Mark(2, 2)], ClientId, User, Token));

        Assert.Equal((2, 2), (placement.SimklId, placement.SimklEpisode));
        _client.Verify(c => c.GetAnimeAsync(3, It.IsAny<string>(), It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.GetAnimeEpisodesAsync(1, It.IsAny<string>(), It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.GetAnimeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// A series Simkl files under more than <see cref="AnimeEpisodeResolver.MaxTitles"/> titles is left unplaced.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LeavesASeriesWithTooManyTitlesUnplaced()
    {
        var count = AnimeEpisodeResolver.MaxTitles + 1;
        Search(Enumerable.Range(1, count).ToArray());
        foreach (var id in Enumerable.Range(1, count))
        {
            Anime(Title(id, [1]));
        }

        var placement = Assert.Single(await Resolver().PlaceAsync([Mark(2, 2)], ClientId, User, Token));

        Assert.False(placement.IsPlaced);
        Assert.Contains("more than 10 anime titles", placement.Detail, StringComparison.Ordinal);
        _client.Verify(c => c.GetAnimeAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()), Times.Exactly(AnimeEpisodeResolver.MaxTitles));
    }

    /// <summary>
    /// A title or episode list that cannot be read places nothing.
    /// </summary>
    /// <param name="titleFails">Whether the title read fails (true) or the episode read (false).</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlacesNothingWhenACatalogueReadFails(bool titleFails)
    {
        Search([2]);
        _client.Setup(c => c.GetAnimeAsync(2, ClientId, User, It.IsAny<CancellationToken>())).ReturnsAsync(titleFails ? null : Title(2, [2]));
        _client.Setup(c => c.GetAnimeEpisodesAsync(2, ClientId, User, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<AnimeEpisode>?)null);

        var placement = Assert.Single(await Resolver().PlaceAsync([Mark(2, 2)], ClientId, User, Token));

        Assert.False(placement.IsPlaced);
        Assert.Contains("could not be read", placement.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Marks of more than one series or season are refused.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefusesMarksOfTwoSeasons()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Resolver().PlaceAsync([Mark(2, 2), Mark(3, 2)], ClientId, User, Token));
    }

    private static EpisodeMark Mark(int season, int episode, bool withPremiere = true) =>
        new(
            Marks.User,
            Marks.ItemId($"returner-{season}-{episode}"),
            true,
            Marks.WatchedAt,
            Marks.Series,
            new Dictionary<string, object> { ["anidb"] = "17944", ["tvdb"] = Tvdb },
            "A Returner's Magic Should Be Special",
            2023,
            season,
            [episode])
        {
            PremiereDate = withPremiere ? Premiere : null,
        };

    private static AnimeTitle Title(int simklId, int[] seasons, params AnimeEpisode[] episodes) =>
        new(simklId, seasons, []) { Episodes = episodes };

    private static AnimeEpisode Ep(int number, int? tvdbSeason, int? tvdbEpisode, string? aired = null) =>
        new(number, IsEpisode: true, aired is null ? null : DateTimeOffset.Parse(aired, System.Globalization.CultureInfo.InvariantCulture), tvdbSeason, tvdbEpisode);

    private AnimeEpisodeResolver Resolver() => new(_client.Object);

    private void Search(int[]? found) =>
        _client.Setup(c => c.FindAnimeAsync("tvdb", Tvdb, ClientId, User, It.IsAny<CancellationToken>())).ReturnsAsync(found);

    private void Anime(AnimeTitle title) =>
        _client.Setup(c => c.GetAnimeAsync(title.SimklId, ClientId, User, It.IsAny<CancellationToken>())).ReturnsAsync(title with { Episodes = [] });

    private void Episodes(int simklId, params AnimeEpisode[] episodes) =>
        _client.Setup(c => c.GetAnimeEpisodesAsync(simklId, ClientId, User, It.IsAny<CancellationToken>())).ReturnsAsync(episodes);
}
