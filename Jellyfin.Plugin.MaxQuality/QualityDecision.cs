namespace Jellyfin.Plugin.MaxQuality;

/// <summary>
/// How a PlaybackInfo request is treated.
/// </summary>
public enum QualityDecision
{
    /// <summary>
    /// First request of a playback round: the bitrate cap is removed.
    /// </summary>
    NewRound,

    /// <summary>
    /// A later request of the round with the round's original bitrate (track change, stream restart): the cap stays removed.
    /// </summary>
    SameRound,

    /// <summary>
    /// The viewer picked a different quality during playback: the request is left as sent.
    /// </summary>
    ViewerChangedQuality,

    /// <summary>
    /// The viewer picked a quality earlier in this round: the request is left as sent.
    /// </summary>
    KeepViewerQuality
}
