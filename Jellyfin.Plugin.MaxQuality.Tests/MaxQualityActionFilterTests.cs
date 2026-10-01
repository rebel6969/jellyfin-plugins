using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MaxQuality.Tests;

/// <summary>
/// The filter against Jellyfin's PlaybackInfo request shape.
/// </summary>
public sealed class MaxQualityActionFilterTests
{
    private const string Device = "rebel";
    private const int FortyMbps = 40_000_000;
    private const int TwentyMbps = 20_000_000;

    private static readonly Guid VideoId = new("9a1f3c55-7f0e-4d0c-8f69-6c1b2f1d0001");
    private static readonly Guid SongId = new("9a1f3c55-7f0e-4d0c-8f69-6c1b2f1d0002");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly List<SessionInfo> _sessions = [];
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly MaxQualityActionFilter _filter;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaxQualityActionFilterTests"/> class.
    /// </summary>
    public MaxQualityActionFilterTests()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(VideoId)).Returns(new Movie { Name = "The Fantastic 4: First Steps" });
        library.Setup(l => l.GetItemById(SongId)).Returns(new Audio { Name = "A song" });
        _sessionManager.Setup(s => s.Sessions).Returns(_sessions);
        _filter = new MaxQualityActionFilter(
            new PlaybackRoundTracker(_clock),
            library.Object,
            _sessionManager.Object,
            NullLogger<MaxQualityActionFilter>.Instance);
    }

    /// <summary>
    /// The first PlaybackInfo request for a video gets the cap removed.
    /// </summary>
    [Fact]
    public void RemovesTheCapOnTheFirstRequestForAVideo()
    {
        var context = PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device);

        _filter.Apply(context);

        Assert.Equal(int.MaxValue, context.ActionArguments[MaxQualityActionFilter.BitrateArgument]);
    }

    /// <summary>
    /// A cap sent as the query argument is replaced too.
    /// </summary>
    [Fact]
    public void ReplacesABitrateSentInTheQuery()
    {
        var context = PlaybackInfoContext(VideoId, null, Device);
        context.ActionArguments[MaxQualityActionFilter.BitrateArgument] = FortyMbps;

        _filter.Apply(context);

        Assert.Equal(int.MaxValue, context.ActionArguments[MaxQualityActionFilter.BitrateArgument]);
    }

    /// <summary>
    /// A quality picked during playback reaches Jellyfin unchanged.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LeavesAQualityPickedDuringPlaybackAsSent()
    {
        _filter.Apply(PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device));
        await using var session = PlayingSession(Device, VideoId);

        var change = PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = TwentyMbps }, Device);
        _filter.Apply(change);

        Assert.False(change.ActionArguments.ContainsKey(MaxQualityActionFilter.BitrateArgument));
    }

    /// <summary>
    /// A repeat request of the same round at its original bitrate keeps the cap removed.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task KeepsTheCapRemovedWhenTheSameRoundAsksAgainAtItsOriginalBitrate()
    {
        _filter.Apply(PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device));
        await using var session = PlayingSession(Device, VideoId);

        var trackChange = PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device);
        _filter.Apply(trackChange);

        Assert.Equal(int.MaxValue, trackChange.ActionArguments[MaxQualityActionFilter.BitrateArgument]);
    }

    /// <summary>
    /// A quality picked half an hour into playback is honoured because the device's session still plays the video.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HonoursAChoiceMadeLongAfterTheStartWhileTheVideoIsPlaying()
    {
        _filter.Apply(PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device));
        _clock.Advance(TimeSpan.FromMinutes(30));
        await using var session = PlayingSession(Device, VideoId);

        var change = PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = TwentyMbps }, Device);
        _filter.Apply(change);

        Assert.False(change.ActionArguments.ContainsKey(MaxQualityActionFilter.BitrateArgument));
    }

    /// <summary>
    /// The same request half an hour later with nothing playing on the device starts a new round (negative control).
    /// </summary>
    [Fact]
    public void StartsANewRoundWhenTheDeviceNoLongerPlaysTheVideo()
    {
        _filter.Apply(PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device));
        _clock.Advance(TimeSpan.FromMinutes(30));

        var next = PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = TwentyMbps }, Device);
        _filter.Apply(next);

        Assert.Equal(int.MaxValue, next.ActionArguments[MaxQualityActionFilter.BitrateArgument]);
    }

    /// <summary>
    /// Other actions are not touched.
    /// </summary>
    [Fact]
    public void LeavesOtherActionsUntouched()
    {
        var context = Context("Videos", "GetVideoStream", VideoId, Device);
        context.ActionArguments[MaxQualityActionFilter.BitrateArgument] = FortyMbps;

        _filter.Apply(context);

        Assert.Equal(FortyMbps, context.ActionArguments[MaxQualityActionFilter.BitrateArgument]);
    }

    /// <summary>
    /// Music is not touched.
    /// </summary>
    [Fact]
    public void LeavesMusicUntouched()
    {
        var context = PlaybackInfoContext(SongId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device);

        _filter.Apply(context);

        Assert.False(context.ActionArguments.ContainsKey(MaxQualityActionFilter.BitrateArgument));
    }

    /// <summary>
    /// Unknown items are not touched.
    /// </summary>
    [Fact]
    public void LeavesUnknownItemsUntouched()
    {
        var context = PlaybackInfoContext(Guid.NewGuid(), new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device);

        _filter.Apply(context);

        Assert.False(context.ActionArguments.ContainsKey(MaxQualityActionFilter.BitrateArgument));
    }

    /// <summary>
    /// Requests without a device id are not touched.
    /// </summary>
    [Fact]
    public void LeavesRequestsWithoutADeviceIdUntouched()
    {
        var context = PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, deviceId: null);

        _filter.Apply(context);

        Assert.False(context.ActionArguments.ContainsKey(MaxQualityActionFilter.BitrateArgument));
    }

    /// <summary>
    /// The action sees the new cap.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AppliesThePolicyBeforeTheActionRuns()
    {
        var context = PlaybackInfoContext(VideoId, new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }, Device);
        object? seenByAction = null;

        await _filter.OnActionExecutionAsync(context, () =>
        {
            seenByAction = context.ActionArguments[MaxQualityActionFilter.BitrateArgument];
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });

        Assert.Equal(int.MaxValue, seenByAction);
    }

    /// <summary>
    /// The requested cap is read from the query first, as Jellyfin does.
    /// </summary>
    [Fact]
    public void RequestedBitratePrefersTheQueryOverTheBody()
    {
        var arguments = new Dictionary<string, object?>
        {
            [MaxQualityActionFilter.BitrateArgument] = TwentyMbps,
            ["playbackInfoDto"] = new FakePlaybackInfoDto { MaxStreamingBitrate = FortyMbps }
        };

        Assert.Equal(TwentyMbps, MaxQualityActionFilter.GetRequestedBitrate(arguments));
    }

    /// <summary>
    /// The requested cap falls back to the posted device profile.
    /// </summary>
    [Fact]
    public void RequestedBitrateFallsBackToTheDeviceProfile()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["playbackInfoDto"] = new FakePlaybackInfoDto { DeviceProfile = new DeviceProfile { MaxStreamingBitrate = TwentyMbps } }
        };

        Assert.Equal(TwentyMbps, MaxQualityActionFilter.GetRequestedBitrate(arguments));
    }

    /// <summary>
    /// No cap anywhere reads as null.
    /// </summary>
    [Fact]
    public void RequestedBitrateIsNullWhenNothingWasSent()
    {
        var arguments = new Dictionary<string, object?> { ["playbackInfoDto"] = null };

        Assert.Null(MaxQualityActionFilter.GetRequestedBitrate(arguments));
    }

    private static ActionExecutingContext PlaybackInfoContext(Guid itemId, FakePlaybackInfoDto? body, string? deviceId)
    {
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", itemId, deviceId);
        context.ActionArguments["playbackInfoDto"] = body;
        return context;
    }

    private static ActionExecutingContext Context(string controller, string action, Guid itemId, string? deviceId)
    {
        var httpContext = new DefaultHttpContext();
        if (deviceId is not null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(MaxQualityActionFilter.DeviceIdClaimType, deviceId)], "Test"));
        }

        var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), descriptor),
            [],
            new Dictionary<string, object?> { ["itemId"] = itemId },
            new object());
    }

    private SessionInfo PlayingSession(string deviceId, Guid itemId)
    {
        var session = new SessionInfo(_sessionManager.Object, NullLogger.Instance)
        {
            DeviceId = deviceId,
            NowPlayingItem = new BaseItemDto { Id = itemId }
        };
        _sessions.Add(session);
        return session;
    }

    /// <summary>
    /// Mirrors the two members of Jellyfin.Api's PlaybackInfoDto that the filter reads.
    /// </summary>
    private sealed class FakePlaybackInfoDto
    {
        public int? MaxStreamingBitrate { get; init; }

        public DeviceProfile? DeviceProfile { get; init; }
    }
}
