using System.Security.Claims;
using Jellyfin.Plugin.EnglishSdh;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static SubtitleSelectorTests;

/// <summary>Stand-in for Jellyfin.Api's PlaybackInfoDto, which the filter reads by property name.</summary>
public sealed class FakePlaybackInfoDto
{
    public Guid? UserId { get; set; }
    public int? AudioStreamIndex { get; set; }
    public int? SubtitleStreamIndex { get; set; }
    public string? MediaSourceId { get; set; }
}

public sealed class StaleTrackFilterTests
{
    private readonly Mock<IMediaSourceManager> _sources = new();
    private readonly Mock<ILogger<StaleTrackFilter>> _logger = new();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();
    private readonly PluginConfiguration _config;

    public StaleTrackFilterTests()
    {
        _config = new PluginConfiguration { Enabled = true, UserIds = [_user.ToString("N")] };
        // The 2026-10-14 file: video 0, audio 1, English 2 (default), Arabic 3, German 4, Spanish 5.
        _sources.Setup(s => s.GetMediaStreams(_item)).Returns(Layout());
    }

    private static List<MediaStream> Layout(params MediaStream[] extra) =>
    [
        new() { Index = 0, Type = MediaStreamType.Video },
        new() { Index = 1, Type = MediaStreamType.Audio, Language = "jpn" },
        new() { Index = 2, Type = MediaStreamType.Subtitle, Language = "eng", IsDefault = true },
        Sub(3, "ara"), Sub(4, "deu"), Sub(5, "spa"), .. extra
    ];

    private StaleTrackFilter Filter() => new(_sources.Object, _logger.Object, () => _config);

    private ActionExecutingContext Context(Dictionary<string, object?> arguments, Guid? claimUser = null,
        string controller = "MediaInfo", string action = "GetPostedPlaybackInfo")
    {
        arguments.TryAdd("itemId", _item);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(StaleTrackFilter.UserIdClaimType, (claimUser ?? _user).ToString("N"))], "test"))
        };
        var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], arguments, new object());
    }

    private bool Logged(LogLevel level) =>
        _logger.Invocations.Any(i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == level);

    [Fact]
    public void IncidentRequestFallsBackToDefaultAudioAndEnglish()
    {
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 });
        Filter().Apply(context);
        Assert.Null(context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(2, context.ActionArguments["subtitleStreamIndex"]);
        Assert.True(Logged(LogLevel.Information));
    }

    [Fact]
    public void StaleRequestPrefersSdh()
    {
        _sources.Setup(s => s.GetMediaStreams(_item)).Returns(Layout(Sub(6, "eng", "English SDH")));
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 });
        Filter().Apply(context);
        Assert.Equal(6, context.ActionArguments["subtitleStreamIndex"]);
    }

    [Fact]
    public void StaleBodyIsClearedAndSubtitleChoiceOverridesIt()
    {
        var body = new FakePlaybackInfoDto { AudioStreamIndex = 2, SubtitleStreamIndex = 5 };
        var context = Context(new() { ["playbackInfoDto"] = body });
        Filter().Apply(context);
        Assert.Null(body.AudioStreamIndex);
        Assert.Null(body.SubtitleStreamIndex);
        Assert.Null(context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(2, context.ActionArguments["subtitleStreamIndex"]);
    }

    [Fact]
    public void QueryArgumentIsReadAheadOfTheBodyLikeJellyfin()
    {
        var body = new FakePlaybackInfoDto { AudioStreamIndex = 2, SubtitleStreamIndex = 9 };
        var context = Context(new() { ["audioStreamIndex"] = 1, ["subtitleStreamIndex"] = 5, ["playbackInfoDto"] = body });
        Filter().Apply(context);
        Assert.Equal(1, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(5, context.ActionArguments["subtitleStreamIndex"]);
        Assert.Equal(2, body.AudioStreamIndex);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(1, -1)]
    [InlineData(null, 3)]
    [InlineData(1, null)]
    public void RequestThatFitsTheFileIsLeftAsSent(int? audio, int? subtitle)
    {
        var context = Context(new() { ["audioStreamIndex"] = audio, ["subtitleStreamIndex"] = subtitle });
        Filter().Apply(context);
        Assert.Equal(audio, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(subtitle, context.ActionArguments["subtitleStreamIndex"]);
        Assert.False(Logged(LogLevel.Information));
    }

    [Fact]
    public void MissingSubtitleIndexIsStale()
    {
        var context = Context(new() { ["audioStreamIndex"] = 1, ["subtitleStreamIndex"] = 9 });
        Filter().Apply(context);
        Assert.Null(context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(2, context.ActionArguments["subtitleStreamIndex"]);
    }

    [Fact]
    public void NoEnglishTrackLeavesBothToTheServer()
    {
        _sources.Setup(s => s.GetMediaStreams(_item)).Returns(
            [new() { Index = 0, Type = MediaStreamType.Video }, new() { Index = 1, Type = MediaStreamType.Audio }, Sub(2, "spa")]);
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 2 });
        Filter().Apply(context);
        Assert.Null(context.ActionArguments["audioStreamIndex"]);
        Assert.Null(context.ActionArguments["subtitleStreamIndex"]);
    }

    [Fact]
    public void NoIndexesIsNoOp()
    {
        var context = Context([]);
        Filter().Apply(context);
        Assert.False(context.ActionArguments.ContainsKey("subtitleStreamIndex"));
        _sources.Verify(s => s.GetMediaStreams(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void OtherUserIsNoOp()
    {
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 }, Guid.NewGuid());
        Filter().Apply(context);
        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
    }

    [Fact]
    public void DisabledIsNoOp()
    {
        _config.Enabled = false;
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 });
        Filter().Apply(context);
        Assert.Equal(5, context.ActionArguments["subtitleStreamIndex"]);
    }

    [Fact]
    public void RequestedUserArgumentIsTheOneChecked()
    {
        var context = Context(new() { ["userId"] = (Guid?)_user, ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 },
            Guid.NewGuid());
        Filter().Apply(context);
        Assert.Equal(2, context.ActionArguments["subtitleStreamIndex"]);
    }

    [Fact]
    public void OtherActionIsNoOp()
    {
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 }, action: "GetPlaybackInfo");
        Filter().Apply(context);
        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
    }

    [Fact]
    public void NonFileSourceIsNoOp()
    {
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5, ["mediaSourceId"] = "live-tv-1" });
        Filter().Apply(context);
        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
    }

    [Fact]
    public void RequestedSourceIsTheOneChecked()
    {
        var version = Guid.NewGuid();
        _sources.Setup(s => s.GetMediaStreams(version)).Returns(
            [new() { Index = 0, Type = MediaStreamType.Video }, new() { Index = 1, Type = MediaStreamType.Audio },
             new() { Index = 2, Type = MediaStreamType.Audio }, Sub(5, "eng")]);
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5, ["mediaSourceId"] = version.ToString("N") });
        Filter().Apply(context);
        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.Equal(5, context.ActionArguments["subtitleStreamIndex"]);
    }

    [Fact]
    public void NonVideoIsNoOp()
    {
        _sources.Setup(s => s.GetMediaStreams(_item)).Returns([new() { Index = 0, Type = MediaStreamType.Audio }]);
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 });
        Filter().Apply(context);
        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
    }

    [Fact]
    public async Task FaultIsLoggedAndTheRequestStillRuns()
    {
        _sources.Setup(s => s.GetMediaStreams(_item)).Throws(new InvalidOperationException("simulated database fault"));
        var context = Context(new() { ["audioStreamIndex"] = 2, ["subtitleStreamIndex"] = 5 });
        var ran = false;
        await Filter().OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });
        Assert.True(ran);
        Assert.Equal(2, context.ActionArguments["audioStreamIndex"]);
        Assert.True(Logged(LogLevel.Error));
    }

    [Fact]
    public void RegistratorAddsTheFilterToMvc()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MvcOptions>>().Value;
        Assert.Contains(options.Filters, f => f is ServiceFilterAttribute s && s.ServiceType == typeof(StaleTrackFilter));
    }
}
