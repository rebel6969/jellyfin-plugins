using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
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
    private readonly List<bool> _rewatchFlags = [];
    private readonly FakeSettings _settings = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 12, 15, 0, TimeSpan.Zero));
    private readonly List<IReadOnlyList<WatchedLookupItem>> _lookups = [];
    private readonly MarkSyncProcessor _processor;
    private SimklUser _user = new("token-a", syncMovies: true, syncShows: true);
    private SimklWriteResult? _addResult = new(0, 0, 0, []);
    private string? _plan = "free";
    private int _planReads;
    private Func<IReadOnlyList<WatchedLookupItem>, IReadOnlyList<WatchedStatus>?> _watched = items => items.Select(_ => Completed).ToList();

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkSyncProcessorTests"/> class.
    /// </summary>
    public MarkSyncProcessorTests()
    {
        _credentials.Setup(c => c.GetClientId()).Returns(ClientId);
        _credentials.Setup(c => c.GetUser(Marks.User)).Returns(() => _user);
        _client
            .Setup(c => c.AddToHistoryAsync(It.IsAny<HistoryRequest>(), ClientId, It.IsAny<SimklUser>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<HistoryRequest, string, SimklUser, bool, CancellationToken>((request, _, user, rewatch, _) =>
            {
                _added.Add((request, user));
                _rewatchFlags.Add(rewatch);
            })
            .ReturnsAsync(() => _addResult);
        _client
            .Setup(c => c.RemoveFromHistoryAsync(It.IsAny<HistoryRequest>(), ClientId, It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()))
            .Callback<HistoryRequest, string, SimklUser, CancellationToken>((request, _, _, _) => _removed.Add(request))
            .ReturnsAsync(new SimklWriteResult(0, 0, 0, []));
        _client
            .Setup(c => c.GetWatchedAsync(It.IsAny<IReadOnlyList<WatchedLookupItem>>(), ClientId, It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<WatchedLookupItem>, string, SimklUser, CancellationToken>((items, _, _, _) => _lookups.Add(items))
            .ReturnsAsync((IReadOnlyList<WatchedLookupItem> items, string _, SimklUser _, CancellationToken _) => _watched(items));
        _client
            .Setup(c => c.GetAccountTypeAsync(ClientId, It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()))
            .Callback(() => _planReads++)
            .ReturnsAsync(() => _plan);
        _processor = new MarkSyncProcessor(_credentials.Object, _client.Object, _settings, _clock, _logger);
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

    /// <summary>
    /// With rewatch tracking off, finished playbacks go out as plain watches with the manual marks, and the plan is
    /// never read.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsFinishedPlaybacksAsPlainWatchesWhenRewatchesAreOff()
    {
        await _processor.ProcessAsync([Finished("tt1"), Marks.Movie("tt2")], Token);

        Assert.Equal([false], _rewatchFlags);
        Assert.Equal(2, Assert.Single(_added).Request.Movies!.Count);
        Assert.Equal(0, _planReads);
    }

    /// <summary>
    /// On a PRO account, finished playbacks go out with allow_rewatch and manual marks without it.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsFinishedPlaybacksAsRewatchesOnAPaidPlan()
    {
        _settings.TrackRewatches = true;
        _plan = "pro";
        _addResult = new(1, 0, 0, []) { RewatchStatuses = ["completed"] };

        await _processor.ProcessAsync([Finished("tt1"), Marks.Movie("tt2")], Token);

        Assert.Equal([false, true], _rewatchFlags);
        Assert.Equal("tt2", Assert.Single(_added[0].Request.Movies!).Ids["imdb"]);
        Assert.Equal("tt1", Assert.Single(_added[1].Request.Movies!).Ids["imdb"]);
        Assert.Contains(_logger.At(LogLevel.Information), m => m.Contains("rewatch status completed for 1 items", StringComparison.Ordinal));
    }

    /// <summary>
    /// A VIP account counts as paid too.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TreatsVipAsAPaidPlan()
    {
        _settings.TrackRewatches = true;
        _plan = "vip";

        await _processor.ProcessAsync([Finished("tt1")], Token);

        Assert.Equal([true], _rewatchFlags);
    }

    /// <summary>
    /// On a free account nothing carries allow_rewatch, which would make Simkl drop the whole write, and the plan is
    /// read once until the cache expires.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsPlainWatchesOnAFreePlanAndCachesThePlan()
    {
        _settings.TrackRewatches = true;
        _plan = "free";

        await _processor.ProcessAsync([Finished("tt1")], Token);
        await _processor.ProcessAsync([Finished("tt2")], Token);
        _clock.Advance(MarkSyncProcessor.PlanCacheTime);
        await _processor.ProcessAsync([Finished("tt3")], Token);

        Assert.Equal([false, false, false], _rewatchFlags);
        Assert.Equal(2, _planReads);
        Assert.Contains(_logger.At(LogLevel.Information), m => m.Contains("on the Simkl free plan", StringComparison.Ordinal));
    }

    /// <summary>
    /// A plan that cannot be read counts as free and is read again next time.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RereadsAPlanItCouldNotRead()
    {
        _settings.TrackRewatches = true;
        _plan = null;

        await _processor.ProcessAsync([Finished("tt1")], Token);
        await _processor.ProcessAsync([Finished("tt2")], Token);

        Assert.Equal([false, false], _rewatchFlags);
        Assert.Equal(2, _planReads);
        Assert.Equal(2, _logger.At(LogLevel.Warning).Count(m => m.Contains("could not read the Simkl plan", StringComparison.Ordinal)));
    }

    /// <summary>
    /// When Simkl answers pro_required, the plan has lapsed since it was read, so it is read again.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RereadsThePlanAfterProRequired()
    {
        _settings.TrackRewatches = true;
        _plan = "pro";
        _addResult = new(0, 0, 0, []) { RewatchStatuses = ["pro_required"] };

        await _processor.ProcessAsync([Finished("tt1")], Token);
        _plan = "free";
        await _processor.ProcessAsync([Finished("tt2")], Token);

        Assert.Equal([true, false], _rewatchFlags);
        Assert.Equal(2, _planReads);
    }

    /// <summary>
    /// A new Simkl login (another account) has its plan read afresh.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RereadsThePlanForANewLogin()
    {
        _settings.TrackRewatches = true;
        _plan = "pro";

        await _processor.ProcessAsync([Finished("tt1")], Token);
        _user = new SimklUser("token-new", syncMovies: true, syncShows: true);
        _plan = "free";
        await _processor.ProcessAsync([Finished("tt2")], Token);

        Assert.Equal([true, false], _rewatchFlags);
        Assert.Equal(2, _planReads);
    }

    /// <summary>
    /// Manual marks alone never need the plan.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReadsNoPlanForManualMarksAlone()
    {
        _settings.TrackRewatches = true;

        await _processor.ProcessAsync([Marks.Movie("tt1")], Token);

        Assert.Equal([false], _rewatchFlags);
        Assert.Equal(0, _planReads);
    }

    private static MarkChange Finished(string imdb) => Marks.Movie(imdb) with { FromPlayback = true };
}
