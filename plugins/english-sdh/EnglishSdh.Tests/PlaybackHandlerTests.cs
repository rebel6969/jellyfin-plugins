using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EnglishSdh;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static SubtitleSelectorTests;

public sealed class PlaybackHandlerTests
{
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<IMediaSourceManager> _sources = new();
    private readonly Mock<IHostApplicationLifetime> _lifetime = new();
    private readonly Mock<ILogger<PlaybackHandler>> _logger = new();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();
    private readonly List<GeneralCommand> _sent = [];
    private readonly SessionInfo _session;
    private readonly PluginConfiguration _config;

    public PlaybackHandlerTests()
    {
        _session = new SessionInfo(_sessions.Object, NullLogger.Instance)
        {
            Id = "test-session",
            UserId = _user,
            Client = "Test client",
            NowPlayingItem = new BaseItemDto { Id = _item },
            PlayState = new PlayerStateInfo { MediaSourceId = _item.ToString("N"), SubtitleStreamIndex = 2 },
            Capabilities = new ClientCapabilities { SupportedCommands = [GeneralCommandType.SetSubtitleStreamIndex] }
        };
        _config = new PluginConfiguration { Enabled = true, UserIds = [_user.ToString("N")] };
        _sessions.SetupGet(s => s.Sessions).Returns([_session]);
        _sources.Setup(s => s.GetMediaStreams(_item)).Returns([Sub(2), Sub(3, hearing: true)]);
        _sessions.Setup(s => s.SendGeneralCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GeneralCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, GeneralCommand, CancellationToken>((_, _, c, _) => _sent.Add(c)).Returns(Task.CompletedTask);
    }
    private PlaybackStartEventArgs Start(string play = "play-1") => new()
    {
        Session = _session,
        Item = new Episode { Id = _item },
        MediaSourceId = _item.ToString("N"),
        PlaySessionId = play
    };
    // The settle delay runs on this clock, so tests that act "during the delay" cannot race real time.
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new();
    private PlaybackHandler Handler(TimeSpan? delay = null) => new(_sessions.Object, _sources.Object,
        _logger.Object, _lifetime.Object, () => _config, delay ?? TimeSpan.Zero, _time);

    [Fact]
    public async Task SendsOnlySubtitleCommandWithCorrectIndex()
    {
        await Handler().OnEvent(Start());
        var command = Assert.Single(_sent);
        Assert.Equal(GeneralCommandType.SetSubtitleStreamIndex, command.Name);
        Assert.Equal("3", command.Arguments["Index"]);
    }
    [Fact]
    public async Task DisabledIsNoOp() { _config.Enabled = false; await Handler().OnEvent(Start()); Assert.Empty(_sent); }
    [Fact]
    public async Task OtherUsersAreNoOp() { _config.UserIds = [Guid.NewGuid().ToString()]; await Handler().OnEvent(Start()); Assert.Empty(_sent); }
    [Fact]
    public async Task UnsupportedClientIsNoOp()
    {
        _session.Capabilities.SupportedCommands = [];
        await Handler().OnEvent(Start()); Assert.Empty(_sent);
        Assert.Contains(_logger.Invocations, i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Warning);
    }
    [Fact]
    public async Task AlreadySelectedIsNoOp() { _session.PlayState.SubtitleStreamIndex = 3; await Handler().OnEvent(Start()); Assert.Empty(_sent); }
    [Fact]
    public async Task NoMatchPreservesCurrentTrack()
    {
        _sources.Setup(s => s.GetMediaStreams(_item)).Returns([Sub(8, "fra")]);
        await Handler().OnEvent(Start()); Assert.Empty(_sent);
    }
    [Fact]
    public async Task DuplicateStartDoesNotOverrideLaterManualChoice()
    {
        var handler = Handler(); await handler.OnEvent(Start());
        _session.PlayState.SubtitleStreamIndex = -1;
        await handler.OnEvent(Start()); Assert.Single(_sent);
    }
    [Fact]
    public async Task StopDuringDelayPreventsCommand()
    {
        var handler = Handler(TimeSpan.FromMilliseconds(30)); var task = handler.OnEvent(Start());
        await handler.OnEvent(new PlaybackStopEventArgs { Session = _session, Item = new Episode { Id = _item }, PlaySessionId = "play-1" });
        _time.Advance(TimeSpan.FromMilliseconds(30));
        await task; Assert.Empty(_sent);
    }
    [Fact]
    public async Task ChangedItemDuringDelayPreventsCommand()
    {
        var handler = Handler(TimeSpan.FromMilliseconds(30)); var task = handler.OnEvent(Start());
        _session.NowPlayingItem = new BaseItemDto { Id = Guid.NewGuid() };
        _time.Advance(TimeSpan.FromMilliseconds(30));
        await task; Assert.Empty(_sent);
    }
    [Fact]
    public async Task ManualChoiceDuringDelayWins()
    {
        var handler = Handler(TimeSpan.FromMilliseconds(30)); var task = handler.OnEvent(Start());
        _session.PlayState.SubtitleStreamIndex = -1;
        _time.Advance(TimeSpan.FromMilliseconds(30));
        await task; Assert.Empty(_sent);
    }
    [Fact]
    public async Task UnchangedPlaybackAfterTheDelayGetsTheCommand()
    {
        // Positive control for the three tests above: the same delay with nothing changed sends the command.
        var handler = Handler(TimeSpan.FromMilliseconds(30)); var task = handler.OnEvent(Start());
        Assert.Empty(_sent);
        _time.Advance(TimeSpan.FromMilliseconds(30));
        await task; Assert.Single(_sent);
    }
    [Fact]
    public async Task ChangedSourceDuringLookupPreventsWrongIndex()
    {
        _sources.Setup(s => s.GetMediaStreams(_item)).Callback(() => _session.PlayState.MediaSourceId = Guid.NewGuid().ToString("N"))
            .Returns([Sub(2), Sub(3, hearing: true)]);
        await Handler().OnEvent(Start()); Assert.Empty(_sent);
    }
    [Fact]
    public async Task SendFailureIsLoggedWithoutFailingPlayback()
    {
        _sessions.Setup(s => s.SendGeneralCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GeneralCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated client disconnect"));
        await Handler().OnEvent(Start());
        Assert.Contains(_logger.Invocations, i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Error);
    }
    private void Connection(bool active)
    {
        var controller = new Mock<ISessionController>();
        controller.SetupGet(c => c.IsSessionActive).Returns(active);
        _session.SessionControllers = [controller.Object];
    }
    [Fact]
    public async Task UnreportedCapabilitiesOnLiveConnectionStillGetTheCommand()
    {
        // Jellium Desktop after the 2026-10-14 restarts: no capabilities on the server, websocket still connected.
        _session.Capabilities = new ClientCapabilities(); Connection(active: true);
        await Handler().OnEvent(Start());
        Assert.Equal("3", Assert.Single(_sent).Arguments["Index"]);
        Assert.DoesNotContain(_logger.Invocations, i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Warning);
    }
    [Fact]
    public async Task ReportedCapabilitiesWithoutTheCommandAreRespected()
    {
        _session.Capabilities = new ClientCapabilities
        {
            PlayableMediaTypes = [MediaType.Video],
            SupportedCommands = [GeneralCommandType.DisplayMessage]
        };
        Connection(active: true);
        await Handler().OnEvent(Start()); Assert.Empty(_sent);
        Assert.Contains(_logger.Invocations, i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Warning);
    }
    [Fact]
    public async Task ReportedCapabilitiesWithNoCommandsAreRespected()
    {
        _session.Capabilities = new ClientCapabilities { PlayableMediaTypes = [MediaType.Video] };
        Connection(active: true);
        await Handler().OnEvent(Start()); Assert.Empty(_sent);
    }
    [Fact]
    public async Task UnreportedCapabilitiesWithoutLiveConnectionAreSkipped()
    {
        _session.Capabilities = new ClientCapabilities(); Connection(active: false);
        await Handler().OnEvent(Start()); Assert.Empty(_sent);
    }
}
