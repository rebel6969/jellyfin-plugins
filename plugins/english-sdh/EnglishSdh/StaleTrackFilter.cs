using System.Reflection;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnglishSdh;

/// <summary>Drops PlaybackInfo track indexes that do not exist in the file being played.</summary>
/// <remarks>
/// jellyfin-web sends an item page's audio and subtitle selectors with the play request, and Jellyfin returns them
/// unchecked as the playback defaults. When the file behind the item is replaced while the page is open (a Sonarr
/// upgrade), those numbers describe the old file: on 2026-10-14 audio 2 and subtitle 5 reached a file whose index 2
/// is a subtitle and index 5 a Spanish one. An index that names a missing stream, or a stream of the other kind,
/// proves the request was built for another file, so both indexes are dropped: audio falls back to Jellyfin's own
/// default and subtitles to this plugin's English choice. Requests whose indexes all fit the file are left as sent.
/// </remarks>
public sealed class StaleTrackFilter : IAsyncActionFilter
{
    /// <summary>Claim carrying the signed-in user (Jellyfin.Api InternalClaimTypes.UserId).</summary>
    public const string UserIdClaimType = "Jellyfin-UserId";
    public const string AudioArgument = "audioStreamIndex";
    public const string SubtitleArgument = "subtitleStreamIndex";
    private const string ControllerName = "MediaInfo";
    private const string ActionName = "GetPostedPlaybackInfo";
    private const string ItemIdArgument = "itemId";
    private const string UserIdArgument = "userId";
    private const string MediaSourceArgument = "mediaSourceId";
    private const string BodyArgument = "playbackInfoDto";

    private readonly IMediaSourceManager _sources;
    private readonly ILogger<StaleTrackFilter> _logger;
    private readonly Func<PluginConfiguration?> _configuration;

    public StaleTrackFilter(IMediaSourceManager sources, ILogger<StaleTrackFilter> logger,
        Func<PluginConfiguration?> configuration)
    {
        _sources = sources; _logger = logger; _configuration = configuration;
    }

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            Apply(context);
        }
        catch (Exception ex)
        {
            // A plugin fault must not stop playback: the request goes on as the client sent it.
            _logger.LogError(ex, "EnglishSdh could not check PlaybackInfo track indexes; request left as sent");
        }
        return next();
    }

    /// <summary>Applies the check to a PlaybackInfo request; any other action is left untouched.</summary>
    public void Apply(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || !string.Equals(action.ControllerName, ControllerName, StringComparison.Ordinal)
            || !string.Equals(action.ActionName, ActionName, StringComparison.Ordinal)
            || !context.ActionArguments.TryGetValue(ItemIdArgument, out var itemArgument)
            || itemArgument is not Guid itemId) return;

        var arguments = context.ActionArguments;
        arguments.TryGetValue(BodyArgument, out var body);
        var userId = Argument<Guid?>(arguments, UserIdArgument) ?? BodyValue<Guid?>(body, "UserId");
        if (userId is null || userId == Guid.Empty)
            userId = Guid.TryParse(context.HttpContext.User.FindFirst(UserIdClaimType)?.Value, out var claimed)
                ? claimed : null;
        if (userId is null || !OptIn.EnabledFor(_configuration(), userId.Value)) return;

        // Jellyfin reads each query argument ahead of the posted body; mirror that precedence.
        var audio = Argument<int?>(arguments, AudioArgument) ?? BodyValue<int?>(body, "AudioStreamIndex");
        var subtitle = Argument<int?>(arguments, SubtitleArgument) ?? BodyValue<int?>(body, "SubtitleStreamIndex");
        if (audio is null && subtitle is null) return;

        // Resolve exactly the requested source; never guess indexes from another version or a live stream.
        var source = Argument<string>(arguments, MediaSourceArgument) ?? BodyValue<string>(body, "MediaSourceId");
        var sourceId = itemId;
        if (!string.IsNullOrEmpty(source) && !Guid.TryParse(source, out sourceId)) return;
        var streams = _sources.GetMediaStreams(sourceId);
        if (!streams.Any(s => s.Type == MediaStreamType.Video)) return;

        var audioFits = audio is null || streams.Any(s => s.Index == audio && s.Type == MediaStreamType.Audio);
        var subtitleFits = subtitle is null or -1
            || streams.Any(s => s.Index == subtitle && s.Type == MediaStreamType.Subtitle);
        if (audioFits && subtitleFits) return;

        var choice = SubtitleSelector.Select(streams);
        arguments[AudioArgument] = null;
        arguments[SubtitleArgument] = choice;
        ClearBodyValue(body, "AudioStreamIndex");
        ClearBodyValue(body, "SubtitleStreamIndex");
        _logger.LogInformation(
            "EnglishSdh: PlaybackInfo for item {ItemId} asked for audio {Audio} and subtitle {Subtitle}, which this file does not have as such (the client's page predates a file change); using the default audio and subtitle {Choice}",
            itemId, audio, subtitle, choice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default");
    }

    private static T? Argument<T>(IDictionary<string, object?> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value is T typed ? typed : default;

    private static T? BodyValue<T>(object? body, string property) =>
        body?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(body) is T typed
            ? typed : default;

    private static void ClearBodyValue(object? body, string property)
    {
        var info = body?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (info is not null && info.CanWrite && info.GetValue(body) is not null) info.SetValue(body, null);
    }
}
