using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Simkl.API;
using Jellyfin.Plugin.Simkl.Configuration;
using Jellyfin.Plugin.Simkl.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.Simkl.Tests;

/// <summary>
/// Drives the scrobbler through Jellyfin's session events against a fake Simkl, in real time: the
/// scrobbler spaces calls at least five seconds apart per user, so each test takes some seconds.
/// </summary>
public sealed class PlaybackScrobblerRestartTests : IAsyncLifetime
{
    private const string Start = "/scrobble/start";
    private const string Pause = "/scrobble/pause";
    private const string Stop = "/scrobble/stop";

    private static readonly Guid UserId = Guid.Parse("51581cf5-c622-429c-ae8e-736004f5f7be");
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "simkl-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly FakeSimklHandler _simkl = new();
    private readonly BaseItemDto _movie = new()
    {
        Id = Guid.Parse("1400d593-dead-5bc2-86d8-7c5415436a7e"),
        Name = "Hope",
        Type = BaseItemKind.Movie,
        ProductionYear = 2026,
        RunTimeTicks = TimeSpan.FromMinutes(157).Ticks,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1058424", ["Imdb"] = "tt27369017" },
    };

    private SessionInfo _session = null!;
    private PlaybackScrobbler _scrobbler = null!;

    public async ValueTask InitializeAsync()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_configDir);
        paths.SetupGet(p => p.PluginsPath).Returns(_configDir);
        var plugin = new SimklPlugin(paths.Object, Mock.Of<IXmlSerializer>());
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            UserConfigs = [new UserConfig { Id = UserId, UserToken = "test-token" }],
        });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_simkl, disposeHandler: false));
        var api = new SimklApi(NullLogger<SimklApi>.Instance, factory.Object);

        _session = new SessionInfo(_sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-1",
            UserId = UserId,
            UserName = "admin",
        };
        _scrobbler = new PlaybackScrobbler(_sessionManager.Object, NullLogger<PlaybackScrobbler>.Instance, api, Mock.Of<ILibraryManager>());
        await _scrobbler.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _scrobbler.StopAsync(TestContext.Current.CancellationToken);
        _simkl.Dispose();
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    /// <summary>
    /// The 2026-10-13 incident: the client stopped a movie at 0.4%, started it again 24 s later in
    /// the same session and watched it to the end. Simkl got neither the replay nor its stop.
    /// </summary>
    [Fact]
    public async Task AReplayStartedSecondsAfterAStopIsScrobbledToTheEnd()
    {
        await PlayUntilStartedAsync();

        RaiseStopped(0.4);
        Assert.True(await WaitForAsync(() => _simkl.Count(Stop) == 1), "the first stop was not sent");

        RaiseStart(0.35);
        Assert.True(
            await WaitForAsync(() => _simkl.Count(Start) == 2, () => RaiseProgress(10)),
            "the replay was never scrobbled");

        RaiseStopped(100);
        Assert.True(
            await WaitForAsync(() => _simkl.Calls.Any(c => c.Endpoint == Stop && c.Progress >= 99.9f)),
            "the replay's final stop at 100% was never sent");
    }

    [Fact]
    public async Task AStartReportedAgainMidPlaybackKeepsTheOpenScrobble()
    {
        await PlayUntilStartedAsync();

        RaiseStart(5);
        await KeepReportingAsync(TimeSpan.FromSeconds(7), () => RaiseProgress(6));

        Assert.Equal(1, _simkl.Count(Start));
    }

    [Fact]
    public async Task ProgressAfterAFinishedPlaybackOpensNoNewScrobble()
    {
        await PlayUntilStartedAsync();
        RaiseStopped(100);
        Assert.True(await WaitForAsync(() => _simkl.Count(Stop) == 1), "the stop was not sent");

        await KeepReportingAsync(TimeSpan.FromSeconds(7), () => RaiseProgress(100));

        Assert.Equal(1, _simkl.Count(Start));
        Assert.Equal(1, _simkl.Count(Stop));
    }

    [Fact]
    public async Task ANewPlaybackRetriesAnItemTheScrobblerGaveUpOn()
    {
        await PlayUntilStartedAsync();
        _simkl.FailWith(Pause, "id_err");
        Assert.True(
            await WaitForAsync(() => _simkl.Count(Pause) == 1, () => RaiseProgress(2, paused: true)),
            "the pause was not sent");
        await KeepReportingAsync(TimeSpan.FromSeconds(6), () => RaiseProgress(2));
        Assert.Equal(1, _simkl.Count(Start));

        _simkl.Succeed(Pause);
        RaiseStart(2);

        Assert.True(
            await WaitForAsync(() => _simkl.Count(Start) == 2, () => RaiseProgress(3)),
            "the new playback was never scrobbled");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, Action? poke = null)
    {
        var end = DateTime.UtcNow + StepTimeout;
        while (DateTime.UtcNow < end)
        {
            poke?.Invoke();
            if (condition())
            {
                return true;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    private static async Task KeepReportingAsync(TimeSpan duration, Action report)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            report();
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private async Task PlayUntilStartedAsync()
    {
        RaiseStart(0);
        Assert.True(await WaitForAsync(() => _simkl.Count(Start) == 1, () => RaiseProgress(0.1)), "playback was never scrobbled");
    }

    private long Ticks(double percent) => (long)(_movie.RunTimeTicks!.Value * percent / 100);

    private void RaiseStart(double percent) =>
        _sessionManager.Raise(m => m.PlaybackStart += null, _sessionManager.Object, new PlaybackProgressEventArgs
        {
            Session = _session,
            MediaInfo = _movie,
            PlaybackPositionTicks = Ticks(percent),
        });

    private void RaiseProgress(double percent, bool paused = false)
    {
        _session.PlayState.IsPaused = paused;
        _sessionManager.Raise(m => m.PlaybackProgress += null, _sessionManager.Object, new PlaybackProgressEventArgs
        {
            Session = _session,
            MediaInfo = _movie,
            PlaybackPositionTicks = Ticks(percent),
        });
    }

    private void RaiseStopped(double percent) =>
        _sessionManager.Raise(m => m.PlaybackStopped += null, _sessionManager.Object, new PlaybackStopEventArgs
        {
            Session = _session,
            MediaInfo = _movie,
            PlaybackPositionTicks = Ticks(percent),
        });
}
