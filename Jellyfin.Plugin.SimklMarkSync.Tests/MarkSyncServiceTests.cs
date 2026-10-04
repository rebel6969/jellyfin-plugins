using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SimklMarkSync.Simkl;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SimklMarkSync.Tests;

/// <summary>
/// The listener and batching worker, driven by a fake clock.
/// </summary>
public sealed class MarkSyncServiceTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 4, 3, 30, 0, TimeSpan.Zero));
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<ISimklCredentials> _credentials = new();
    private readonly Mock<ISimklClient> _client = new();
    private readonly ListLogger<MarkSyncService> _logger = new();
    private readonly ConcurrentQueue<HistoryRequest> _added = new();
    private readonly MarkSyncService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkSyncServiceTests"/> class.
    /// </summary>
    public MarkSyncServiceTests()
    {
        _credentials.Setup(c => c.GetClientId()).Returns("fixture-client-id");
        _credentials.Setup(c => c.GetUser(Marks.User)).Returns(new SimklUser("token-a", syncMovies: true, syncShows: true));
        _client
            .Setup(c => c.AddToHistoryAsync(It.IsAny<HistoryRequest>(), It.IsAny<string>(), It.IsAny<SimklUser>(), It.IsAny<CancellationToken>()))
            .Callback<HistoryRequest, string, SimklUser, CancellationToken>((request, _, _, _) => _added.Enqueue(request))
            .ReturnsAsync(new SimklWriteResult(0, 0, 0, []));
        var processor = new MarkSyncProcessor(_credentials.Object, _client.Object, NullLogger<MarkSyncProcessor>.Instance);
        _service = new MarkSyncService(_userData.Object, processor, _clock, _logger);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IReadOnlyList<int> BatchSizes => _added.Select(r => r.Movies?.Count ?? 0).ToList();

    /// <inheritdoc />
    public void Dispose()
    {
        _service.Dispose();
    }

    /// <summary>
    /// Marks saved together, as when a season is marked, go out as one request.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsABurstOfMarksAsOneRequest()
    {
        await _service.StartAsync(Token);

        Raise(MovieEvent("tt1"));
        Raise(MovieEvent("tt2"));
        Raise(MovieEvent("tt3"));
        await AdvanceUntil(() => !_added.IsEmpty, TimeSpan.FromSeconds(10));
        await Advance(TimeSpan.FromSeconds(20));

        Assert.Equal([3], BatchSizes);
    }

    /// <summary>
    /// Marks with a quiet gap between them go out as separate requests.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsMarksAfterAQuietGapSeparately()
    {
        await _service.StartAsync(Token);

        Raise(MovieEvent("tt1"));
        await AdvanceUntil(() => _added.Count == 1, TimeSpan.FromSeconds(10));
        Raise(MovieEvent("tt2"));
        await AdvanceUntil(() => _added.Count == 2, TimeSpan.FromSeconds(10));

        Assert.Equal([1, 1], BatchSizes);
    }

    /// <summary>
    /// A steady stream of marks is still sent within the longest batch delay, not held until it stops.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsAStreamOfMarksWithinTheLongestBatchDelay()
    {
        await _service.StartAsync(Token);

        for (var i = 0; i < 15; i++)
        {
            Raise(MovieEvent($"tt{i}"));
            await Advance(TimeSpan.FromSeconds(2));
        }

        // Marks arrived every 2 s, inside the 3 s quiet period, for 30 s; the 15 s cap must have sent a batch meanwhile.
        Assert.NotEmpty(_added);
        Assert.InRange(BatchSizes[0], 1, 10);
    }

    /// <summary>
    /// Playback saves user data constantly; none of it is sent.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task IgnoresPlaybackSaves()
    {
        await _service.StartAsync(Token);

        var playback = MovieEvent("tt1");
        playback.SaveReason = UserDataSaveReason.PlaybackFinished;
        Raise(playback);
        await Advance(TimeSpan.FromSeconds(20));

        Assert.Empty(_added);
    }

    /// <summary>
    /// The service subscribes when started and unsubscribes when stopped.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SubscribesWhileRunningOnly()
    {
        await _service.StartAsync(Token);
        _userData.VerifyAdd(m => m.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
        _credentials.Verify(c => c.GetClientId(), Times.Once);

        await _service.StopAsync(Token);

        _userData.VerifyRemove(m => m.UserDataSaved -= It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
    }

    /// <summary>
    /// Marks collected when Jellyfin stops are sent before the service finishes stopping.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SendsCollectedMarksWhenJellyfinStops()
    {
        await _service.StartAsync(Token);

        Raise(MovieEvent("tt1"));
        await Task.Delay(100, Token);
        await _service.StopAsync(Token);

        Assert.Equal([1], BatchSizes);
    }

    /// <summary>
    /// A failure while capturing a mark is logged and never reaches the request that saved it.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AFailureWhileCapturingDoesNotFailTheSave()
    {
        await _service.StartAsync(Token);

        // Episode.Series looks the series up through Jellyfin's library manager, which a test host does not have.
        var episode = new Episode { Id = Marks.ItemId("episode"), ParentIndexNumber = 1, IndexNumber = 1, SeriesId = Marks.Series };
        var exception = Record.Exception(() => Raise(Event(episode)));

        Assert.Null(exception);
        Assert.Contains("Could not queue a mark", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A batch that fails is logged, and the next batch is still sent.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task KeepsWorkingAfterABatchFails()
    {
        _credentials.SetupSequence(c => c.GetUser(Marks.User))
            .Throws(new InvalidOperationException("settings unreadable"))
            .Returns(new SimklUser("token-a", syncMovies: true, syncShows: true));
        await _service.StartAsync(Token);

        Raise(MovieEvent("tt1"));
        await AdvanceUntil(() => _logger.At(LogLevel.Error).Count == 1, TimeSpan.FromSeconds(10));
        Raise(MovieEvent("tt2"));
        await AdvanceUntil(() => !_added.IsEmpty, TimeSpan.FromSeconds(10));

        Assert.Contains("Sending 1 marks to Simkl failed: settings unreadable", Assert.Single(_logger.At(LogLevel.Error)), StringComparison.Ordinal);
        Assert.Equal([1], BatchSizes);
    }

    private static UserDataSaveEventArgs MovieEvent(string imdb) => Event(new Movie
    {
        Id = Marks.ItemId(imdb),
        Name = imdb,
        ProviderIds = new Dictionary<string, string> { ["Imdb"] = imdb },
    });

    private static UserDataSaveEventArgs Event(BaseItem item) => new()
    {
        UserId = Marks.User,
        Item = item,
        SaveReason = UserDataSaveReason.TogglePlayed,
        UserData = new UserItemData { Key = "key", Played = true },
    };

    private void Raise(UserDataSaveEventArgs e) => _userData.Raise(m => m.UserDataSaved += null, e);

    private async Task Advance(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += Step)
        {
            _clock.Advance(Step);
            await Task.Delay(2, Token).ConfigureAwait(false);
        }
    }

    private async Task AdvanceUntil(Func<bool> condition, TimeSpan limit)
    {
        var deadline = _clock.GetUtcNow() + limit;
        while (!condition())
        {
            Assert.True(_clock.GetUtcNow() < deadline, $"Nothing happened within {limit} of test time.");
            _clock.Advance(Step);
            await Task.Delay(2, Token).ConfigureAwait(false);
        }
    }
}
