using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MaxQuality;

/// <summary>
/// Removes the client's bitrate cap from <c>POST /Items/{itemId}/PlaybackInfo</c> for videos, unless the viewer
/// changed the quality during the current playback round.
/// </summary>
/// <remarks>
/// Jellyfin's MediaInfoController uses the <c>maxStreamingBitrate</c> query argument ahead of the posted body and
/// the device profile, so setting that argument is enough to override every client-side source of the cap. The
/// server's own remote-client bitrate limit is applied after this and still holds.
/// </remarks>
public sealed class MaxQualityActionFilter : IAsyncActionFilter
{
    /// <summary>
    /// Claim carrying the requesting device's id (Jellyfin.Api InternalClaimTypes.DeviceId).
    /// </summary>
    public const string DeviceIdClaimType = "Jellyfin-DeviceId";

    /// <summary>
    /// The value Jellyfin's StreamBuilder itself uses for "no maximum bitrate".
    /// </summary>
    public const int UnlimitedBitrate = int.MaxValue;

    /// <summary>
    /// Name of the controller action argument that carries the bitrate cap.
    /// </summary>
    public const string BitrateArgument = "maxStreamingBitrate";

    private const string ControllerName = "MediaInfo";
    private const string ActionName = "GetPostedPlaybackInfo";
    private const string ItemIdArgument = "itemId";
    private const string BodyArgument = "playbackInfoDto";

    private readonly PlaybackRoundTracker _tracker;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<MaxQualityActionFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaxQualityActionFilter"/> class.
    /// </summary>
    /// <param name="tracker">The playback round tracker.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="logger">The logger.</param>
    public MaxQualityActionFilter(
        PlaybackRoundTracker tracker,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        ILogger<MaxQualityActionFilter> logger)
    {
        _tracker = tracker;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Apply(context);
        return next();
    }

    /// <summary>
    /// Reads the bitrate cap the client asked for, with the same precedence as Jellyfin's MediaInfoController:
    /// query argument, then posted body, then the posted device profile.
    /// </summary>
    /// <param name="arguments">The action arguments.</param>
    /// <returns>The requested cap, or <c>null</c> when the client sent none.</returns>
    public static int? GetRequestedBitrate(IDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.TryGetValue(BitrateArgument, out var query) && query is int queryBitrate)
        {
            return queryBitrate;
        }

        if (!arguments.TryGetValue(BodyArgument, out var body) || body is null)
        {
            return null;
        }

        var bodyType = body.GetType();
        if (bodyType.GetProperty("MaxStreamingBitrate", BindingFlags.Public | BindingFlags.Instance)?.GetValue(body) is int bodyBitrate)
        {
            return bodyBitrate;
        }

        return bodyType.GetProperty("DeviceProfile", BindingFlags.Public | BindingFlags.Instance)?.GetValue(body) is DeviceProfile profile
            ? profile.MaxStreamingBitrate
            : null;
    }

    /// <summary>
    /// Applies the policy to a PlaybackInfo request; any other action is left untouched.
    /// </summary>
    /// <param name="context">The executing action.</param>
    public void Apply(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || !string.Equals(action.ControllerName, ControllerName, StringComparison.Ordinal)
            || !string.Equals(action.ActionName, ActionName, StringComparison.Ordinal)
            || !context.ActionArguments.TryGetValue(ItemIdArgument, out var itemIdArgument)
            || itemIdArgument is not Guid itemId)
        {
            return;
        }

        var deviceId = context.HttpContext.User.FindFirst(DeviceIdClaimType)?.Value;
        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogDebug("PlaybackInfo for {ItemId} carries no device id; leaving the client's bitrate as sent", itemId);
            return;
        }

        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            return;
        }

        var requested = GetRequestedBitrate(context.ActionArguments);
        var isPlaying = _sessionManager.Sessions.Any(session =>
            string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal) && session.NowPlayingItem?.Id == itemId);
        var decision = _tracker.Decide(deviceId, itemId, requested, isPlaying);

        if (decision is QualityDecision.NewRound or QualityDecision.SameRound)
        {
            context.ActionArguments[BitrateArgument] = UnlimitedBitrate;
            _logger.LogInformation(
                "{Decision}: {Item} on device {DeviceId} asked for {RequestedBitrate} bps; bitrate cap removed",
                decision,
                video.Name,
                deviceId,
                requested);
        }
        else
        {
            _logger.LogInformation(
                "{Decision}: {Item} on device {DeviceId} asked for {RequestedBitrate} bps; keeping the viewer's choice",
                decision,
                video.Name,
                deviceId,
                requested);
        }
    }
}
