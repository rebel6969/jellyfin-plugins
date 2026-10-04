using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MaxQuality.Tests;

/// <summary>
/// Playback reports reaching the round tracker.
/// </summary>
public sealed class PlaybackActivityListenerTests
{
    private const string Device = "rebel";
    private const int FortyMbps = 40_000_000;
    private const int TwentyMbps = 20_000_000;

    private static readonly Guid VideoId = new("3c0e9f7a-2b1d-4e55-9a8c-7d6e5f4a0001");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly PlaybackRoundTracker _tracker;
    private readonly PlaybackActivityListener _listener;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackActivityListenerTests"/> class.
    /// </summary>
    public PlaybackActivityListenerTests()
    {
        _tracker = new PlaybackRoundTracker(_clock);
        _listener = new PlaybackActivityListener(_sessionManager.Object, _tracker);
    }

    /// <summary>
    /// Progress reports keep a round alive past the grace period.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ProgressReportsKeepTheRoundAlive()
    {
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        StartRoundThenWait();

        _sessionManager.Raise(s => s.PlaybackProgress += null, Report<PlaybackProgressEventArgs>());
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, VideoId, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// A stop report keeps the round alive for the grace period, covering a player that stops before re-requesting.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StopReportsKeepTheRoundAlive()
    {
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        StartRoundThenWait();

        _sessionManager.Raise(s => s.PlaybackStopped += null, Report<PlaybackStopEventArgs>());
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, VideoId, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// Start reports keep the round alive.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StartReportsKeepTheRoundAlive()
    {
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        StartRoundThenWait();

        _sessionManager.Raise(s => s.PlaybackStart += null, Report<PlaybackProgressEventArgs>());
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, VideoId, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// After the listener stops, reports no longer reach the tracker.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReportsAfterStopAreIgnored()
    {
        await _listener.StartAsync(TestContext.Current.CancellationToken);
        await _listener.StopAsync(TestContext.Current.CancellationToken);
        StartRoundThenWait();

        _sessionManager.Raise(s => s.PlaybackProgress += null, Report<PlaybackProgressEventArgs>());
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(Device, VideoId, TwentyMbps, isPlayingOnDevice: false));
    }

    private static T Report<T>()
        where T : PlaybackProgressEventArgs, new()
        => new() { DeviceId = Device, Item = new Movie { Id = VideoId } };

    private void StartRoundThenWait()
    {
        _tracker.Decide(Device, VideoId, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(TimeSpan.FromMinutes(30));
    }
}
