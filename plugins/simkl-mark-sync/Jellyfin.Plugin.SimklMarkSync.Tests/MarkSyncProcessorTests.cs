using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// Sending one batch of marks.
/// </summary>
public sealed class MarkSyncProcessorTests
{
    private const string ClientId = "fixture-client-id";

    private static readonly Guid OtherUser = new("11111111-2222-3333-4444-555555555555");

    private readonly Mock<ISimklCredentials> _credentials = new();
    private readonly Mock<ISimklClient> _client = new(MockBehavior.Strict);
    private readonly ListLogger<MarkSyncProcessor> _logger = new();
    private readonly List<(HistoryRequest Request, SimklUser User)> _added = [];
    private readonly List<HistoryRequest> _removed = [];
    private readonly List<IReadOnlyList<WatchedLookupItem>> _lookups = [];
    private readonly MarkSyncProcessor _processor;
    private SimklUser _user = new("token-a", syncMovies: true, syncShows: true);
    private SimklWriteResult? _addResult = new(0, 0, 0, []);
    private Func<IReadOnlyList<WatchedLookupItem>, IReadOnlyList<WatchedStatus>?> _watched = items => items.Select(_ => Completed).ToList();

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkSyncProcessorTests"/> class.
    /// </summary>
    public MarkSyncProcessorTests()
    {
        _credentials.Setup(c => c.GetClientId()).Returns(ClientId);
        _credentials.Setup(c => c.GetUser(Marks.User)).Returns(() => _user);
        _client
            .Setup(c => c.AddToHistoryAsync(It.IsAny<HistoryRequest>(), ClientId, It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()))
            .Callback<HistoryRequest, string, SimklUser, CancellationToken>((request, _, user, _) => _added.Add((request, user)))
            .ReturnsAsync(() => _addResult);
        _client
            .Setup(c => c.RemoveFromHistoryAsync(It.IsAny<HistoryRequest>(), ClientId, It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()))
            .Callback<HistoryRequest, string, SimklUser, CancellationToken>((request, _, _, _) => _removed.Add(request))
            .ReturnsAsync(new SimklWriteResult(0, 0, 0, []));
        _client
            .Setup(c => c.GetWatchedAsync(It.IsAny<IReadOnlyList<WatchedLookupItem>>(), ClientId, It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<WatchedLookupItem>, string, SimklUser, CancellationToken>((items, _, _, _) => _lookups.Add(items))
            .ReturnsAsync((IReadOnlyList<WatchedLookupItem> items, string _, SimklUser _, CancellationToken _) => _watched(items));
        _processor = new MarkSyncProcessor(_credentials.Object, _client.Object, _logger);
    }

    private static WatchedStatus Completed => new(Matched: true, Watched: true, List: "completed");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Played movies and episodes go out together as one add, with no lookup and no remove.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsPlayedMarksAsOneAdd()
    {
        await _processor.ProcessAsync([Marks.Movie("tt1"), Marks.Episode(1, 1), Marks.Episode(1, 2)], Token);

        var (request, user) = Assert.Single(_added);
        Assert.Same(_user, user);
        Assert.Single(request.Movies!);
        Assert.Equal(2, Assert.Single(Assert.Single(request.Shows!).Seasons).Episodes.Count);
        Assert.Empty(_removed);
        Assert.Empty(_lookups);
    }

    /// <summary>
    /// Unplayed episodes go out as one remove naming each episode; episodes need no lookup.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsUnplayedEpisodesAsOneRemove()
    {
        await _processor.ProcessAsync([Marks.Episode(1, 1, played: false), Marks.Episode(1, 2, played: false)], Token);

        var request = Assert.Single(_removed);
        Assert.Null(request.Movies);
        Assert.Equal([1, 2], Assert.Single(Assert.Single(request.Shows!).Seasons).Episodes.Select(e => e.Number));
        Assert.Empty(_lookups);
        Assert.Empty(_added);
    }

    /// <summary>
    /// Only an unplayed movie Simkl has completed is removed; one planned, being watched or unknown is left alone.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RemovesOnlyMoviesSimklHasCompleted()
    {
        _watched = _ =>
        [
            Completed,
            new WatchedStatus(Matched: true, Watched: false, List: "plantowatch"),
            new WatchedStatus(Matched: true, Watched: true, List: "watching"),
            new WatchedStatus(Matched: false, Watched: false, List: null),
        ];

        await _processor.ProcessAsync(
            [Marks.Movie("tt1", played: false), Marks.Movie("tt2", played: false), Marks.Movie("tt3", played: false), Marks.Movie("tt4", played: false)],
            Token);

        Assert.Equal(["tt1", "tt2", "tt3", "tt4"], Assert.Single(_lookups).Select(i => i.Ids["imdb"]));
        Assert.Equal(["tt1"], Assert.Single(_removed).Movies!.Select(m => m.Ids["imdb"]));
        Assert.Equal(3, _logger.At(LogLevel.Information).Count(m => m.Contains("stays as it is", StringComparison.Ordinal)));
    }

    /// <summary>
    /// When the lookup fails, no movie is removed.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LeavesMoviesAloneWhenTheLookupFails()
    {
        _watched = _ => null;

        await _processor.ProcessAsync([Marks.Movie("tt1", played: false)], Token);

        Assert.Empty(_removed);
        Assert.Contains("could not confirm", Assert.Single(_logger.At(LogLevel.Warning)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A lookup answer of the wrong length cannot be matched to the movies, so no movie is removed.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LeavesMoviesAloneWhenTheLookupAnswersTheWrongCount()
    {
        _watched = _ => [Completed];

        await _processor.ProcessAsync([Marks.Movie("tt1", played: false), Marks.Movie("tt2", played: false)], Token);

        Assert.Empty(_removed);
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    /// <summary>
    /// Episodes are still unmarked when the movie lookup fails.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StillUnmarksEpisodesWhenTheMovieLookupFails()
    {
        _watched = _ => null;

        await _processor.ProcessAsync([Marks.Movie("tt1", played: false), Marks.Episode(2, 7, played: false)], Token);

        var request = Assert.Single(_removed);
        Assert.Null(request.Movies);
        Assert.Single(request.Shows!);
    }

    /// <summary>
    /// The lookup asks about at most Simkl's limit of movies per call.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LooksUpAtMostTheLimitPerCall()
    {
        var movies = Enumerable.Range(0, SimklClient.MaxLookupItems + 1).Select(i => (MarkChange)Marks.Movie($"tt{i}", played: false)).ToList();

        await _processor.ProcessAsync(movies, Token);

        Assert.Equal([SimklClient.MaxLookupItems, 1], _lookups.Select(l => l.Count));
        Assert.Equal(SimklClient.MaxLookupItems + 1, Assert.Single(_removed).Movies!.Count);
    }

    /// <summary>
    /// Marked and then unmarked within one batch, an item is only unmarked.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheLastMarkOfAnItemWins()
    {
        await _processor.ProcessAsync([Marks.Movie("tt1"), Marks.Episode(1, 1), Marks.Movie("tt1", played: false), Marks.Episode(1, 1, played: false)], Token);

        Assert.Empty(_added);
        var request = Assert.Single(_removed);
        Assert.Single(request.Movies!);
        Assert.Single(request.Shows!);
    }

    /// <summary>
    /// Unmarked and then marked within one batch, an item is only marked.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheLastMarkOfAnItemWinsTheOtherWayRound()
    {
        await _processor.ProcessAsync([Marks.Movie("tt1", played: false), Marks.Movie("tt1")], Token);

        Assert.Single(_added);
        Assert.Empty(_removed);
        Assert.Empty(_lookups);
    }

    /// <summary>
    /// A user who has not linked Simkl causes no call.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsNothingForAUserWithoutASimklLogin()
    {
        await _processor.ProcessAsync([Marks.Movie("tt1", user: OtherUser)], Token);

        _client.VerifyNoOtherCalls();
        Assert.Contains("has not linked Simkl", Assert.Single(_logger.At(LogLevel.Debug)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Each user's marks go out with that user's own login.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsEachUsersMarksWithTheirOwnLogin()
    {
        var other = new SimklUser("token-b", syncMovies: true, syncShows: true);
        _credentials.Setup(c => c.GetUser(OtherUser)).Returns(other);

        await _processor.ProcessAsync([Marks.Movie("tt1"), Marks.Movie("tt2", user: OtherUser)], Token);

        Assert.Equal(["token-a", "token-b"], _added.Select(a => a.User.Token));
    }

    /// <summary>
    /// A user who turned shows off in the Simkl plugin has no episodes sent.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SkipsEpisodesWhenTheUserTurnedShowsOff()
    {
        _user = new SimklUser("token-a", syncMovies: true, syncShows: false);

        await _processor.ProcessAsync([Marks.Movie("tt1"), Marks.Episode(1, 1), Marks.Episode(1, 2, played: false)], Token);

        var (request, _) = Assert.Single(_added);
        Assert.Single(request.Movies!);
        Assert.Null(request.Shows);
        Assert.Empty(_removed);
    }

    /// <summary>
    /// A user who turned movies off in the Simkl plugin has no movies sent.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SkipsMoviesWhenTheUserTurnedMoviesOff()
    {
        _user = new SimklUser("token-a", syncMovies: false, syncShows: true);

        await _processor.ProcessAsync([Marks.Movie("tt1"), Marks.Episode(1, 1), Marks.Movie("tt2", played: false)], Token);

        var (request, _) = Assert.Single(_added);
        Assert.Null(request.Movies);
        Assert.Single(request.Shows!);
        Assert.Empty(_lookups);
        Assert.Empty(_removed);
    }

    /// <summary>
    /// Without the official plugin's client ID nothing is sent.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsNothingWithoutAClientId()
    {
        _credentials.Setup(c => c.GetClientId()).Returns((string?)null);

        await _processor.ProcessAsync([Marks.Movie("tt1"), Marks.Movie("tt2", played: false)], Token);

        _client.VerifyNoOtherCalls();
    }

    /// <summary>
    /// What Simkl added is logged, and each item it could not match is warned about.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LogsWhatWasAddedAndWhatWasNotMatched()
    {
        _addResult = new SimklWriteResult(1, 0, 2, ["Nope {\"imdb\":\"tt0\"}"]);

        await _processor.ProcessAsync([Marks.Movie("tt1"), Marks.Episode(1, 1), Marks.Episode(1, 2)], Token);

        Assert.Contains("newly added 1 movies and 2 episodes", Assert.Single(_logger.At(LogLevel.Information)), StringComparison.Ordinal);
        Assert.Equal("Simkl could not match Nope {\"imdb\":\"tt0\"}; it was not marked watched", Assert.Single(_logger.At(LogLevel.Warning)));
    }

    /// <summary>
    /// Readiness is logged when the official plugin's client ID is found.
    /// </summary>
    [Fact]
    public void LogsReadinessWhenTheClientIdIsFound()
    {
        _processor.LogReadiness();

        Assert.Contains("is ready", Assert.Single(_logger.At(LogLevel.Information)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Without the client ID nothing is claimed; the credentials already logged why.
    /// </summary>
    [Fact]
    public void ClaimsNoReadinessWithoutAClientId()
    {
        _credentials.Setup(c => c.GetClientId()).Returns((string?)null);

        _processor.LogReadiness();

        Assert.Empty(_logger.Entries);
    }

    /// <summary>
    /// A failed add logs nothing more here; the client already logged why.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LogsNothingMoreWhenAnAddFails()
    {
        _addResult = null;

        await _processor.ProcessAsync([Marks.Movie("tt1")], Token);

        Assert.Empty(_logger.Entries);
    }
}
