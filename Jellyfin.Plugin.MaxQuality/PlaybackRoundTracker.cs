using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.MaxQuality;

/// <summary>
/// Remembers, per device, which video is being played and whether the viewer picked a quality for it.
/// </summary>
/// <remarks>
/// A round starts with the first PlaybackInfo request for a video on a device. Later requests for the same
/// video belong to the same round while the device is playing it, or while its last request or playback
/// report is no older than <see cref="RoundGracePeriod"/>. Within a round, a request whose bitrate differs
/// from the round's first request can only come from the viewer changing the quality, so that choice is kept
/// for the rest of the round.
/// </remarks>
public sealed class PlaybackRoundTracker
{
    /// <summary>
    /// How long a round survives without a request or playback report while the device is not playing the video.
    /// </summary>
    public static readonly TimeSpan RoundGracePeriod = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Rounds idle for longer than this are forgotten when a new round starts.
    /// </summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Round> _rounds = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackRoundTracker"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock.</param>
    public PlaybackRoundTracker(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Classifies a PlaybackInfo request and records it.
    /// </summary>
    /// <param name="deviceId">The requesting device.</param>
    /// <param name="itemId">The video requested.</param>
    /// <param name="requestedBitrate">The bitrate cap the client asked for, or <c>null</c> when it sent none.</param>
    /// <param name="isPlayingOnDevice">Whether the device's session currently reports this video as playing.</param>
    /// <returns>How the request should be treated.</returns>
    public QualityDecision Decide(string deviceId, Guid itemId, int? requestedBitrate, bool isPlayingOnDevice)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_rounds.TryGetValue(deviceId, out var round)
                || round.ItemId != itemId
                || (!isPlayingOnDevice && now - round.LastSeen > RoundGracePeriod))
            {
                ForgetIdleRounds(now);
                _rounds[deviceId] = new Round(itemId, requestedBitrate, now);
                return QualityDecision.NewRound;
            }

            round.LastSeen = now;
            if (round.ViewerChoseQuality)
            {
                return QualityDecision.KeepViewerQuality;
            }

            if (requestedBitrate != round.InitialBitrate)
            {
                round.ViewerChoseQuality = true;
                return QualityDecision.ViewerChangedQuality;
            }

            return QualityDecision.SameRound;
        }
    }

    /// <summary>
    /// Records a playback report so the round outlives short gaps such as a player restarting its stream.
    /// </summary>
    /// <param name="deviceId">The reporting device.</param>
    /// <param name="itemId">The video reported.</param>
    public void RecordActivity(string deviceId, Guid itemId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_rounds.TryGetValue(deviceId, out var round) && round.ItemId == itemId)
            {
                round.LastSeen = now;
            }
        }
    }

    private void ForgetIdleRounds(DateTimeOffset now)
    {
        foreach (var deviceId in _rounds.Where(pair => now - pair.Value.LastSeen > ForgetAfter).Select(pair => pair.Key).ToList())
        {
            _rounds.Remove(deviceId);
        }
    }

    private sealed class Round(Guid itemId, int? initialBitrate, DateTimeOffset lastSeen)
    {
        public Guid ItemId { get; } = itemId;

        public int? InitialBitrate { get; } = initialBitrate;

        public DateTimeOffset LastSeen { get; set; } = lastSeen;

        public bool ViewerChoseQuality { get; set; }
    }
}
