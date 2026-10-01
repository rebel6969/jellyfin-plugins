using System;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.MaxQuality.Tests;

/// <summary>
/// Rules of a playback round: when the cap is removed and when the viewer's choice is kept.
/// </summary>
public sealed class PlaybackRoundTrackerTests
{
    private const string Device = "device-a";
    private const string OtherDevice = "device-b";
    private const int FortyMbps = 40_000_000;
    private const int TwentyMbps = 20_000_000;

    private static readonly Guid Movie = new("5d7b6b0c-4b5e-4c38-9a53-0f4e6f1d2a01");
    private static readonly Guid Episode = new("5d7b6b0c-4b5e-4c38-9a53-0f4e6f1d2a02");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly PlaybackRoundTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackRoundTrackerTests"/> class.
    /// </summary>
    public PlaybackRoundTrackerTests()
    {
        _tracker = new PlaybackRoundTracker(_clock);
    }

    /// <summary>
    /// The first request for a video starts a round.
    /// </summary>
    [Fact]
    public void FirstRequestStartsANewRound()
    {
        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// A later request at the round's original bitrate (track change, restart) keeps the cap removed.
    /// </summary>
    [Fact]
    public void SameBitrateLaterInTheRoundKeepsTheCapRemoved()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(QualityDecision.SameRound, _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: true));
    }

    /// <summary>
    /// A different bitrate during playback is the viewer's choice.
    /// </summary>
    [Fact]
    public void DifferentBitrateDuringPlaybackIsTheViewersChoice()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true));
    }

    /// <summary>
    /// Once the viewer chose, every later request of the round is left alone, even back at the original bitrate.
    /// </summary>
    [Fact]
    public void ViewersChoiceHoldsForTheRestOfTheRoundEvenAtTheOriginalBitrate()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true);

        Assert.Equal(QualityDecision.KeepViewerQuality, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true));
        Assert.Equal(QualityDecision.KeepViewerQuality, _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: true));
    }

    /// <summary>
    /// A round that started without a bitrate treats a later bitrate as the viewer's choice.
    /// </summary>
    [Fact]
    public void ARequestWithoutABitrateFollowedByOneWithABitrateIsTheViewersChoice()
    {
        _tracker.Decide(Device, Movie, requestedBitrate: null, isPlayingOnDevice: false);

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true));
    }

    /// <summary>
    /// Moving to another video starts a new round, so the choice does not carry over.
    /// </summary>
    [Fact]
    public void AnotherVideoStartsANewRoundAndForgetsTheChoice()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true);

        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(Device, Episode, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// A round ends once it is idle past the grace period and the device is not playing it.
    /// </summary>
    [Fact]
    public void RoundEndsOnceIdlePastTheGracePeriodAndNotPlaying()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true);
        _clock.Advance(PlaybackRoundTracker.RoundGracePeriod + TimeSpan.FromSeconds(1));

        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// A round survives a gap no longer than the grace period.
    /// </summary>
    [Fact]
    public void RoundSurvivesWithinTheGracePeriodWhenNotPlaying()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(PlaybackRoundTracker.RoundGracePeriod);

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// A round survives a long pause while the device still has the video open.
    /// </summary>
    [Fact]
    public void RoundSurvivesALongPauseWhileTheDeviceStillHasTheVideoOpen()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true));
    }

    /// <summary>
    /// Playback reports keep the round alive across a stop-and-restart of the stream.
    /// </summary>
    [Fact]
    public void PlaybackReportsKeepTheRoundAliveAcrossAStreamRestart()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(TimeSpan.FromMinutes(30));
        _tracker.RecordActivity(Device, Movie);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(QualityDecision.ViewerChangedQuality, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// Without playback reports, a stale round is not reused.
    /// </summary>
    [Fact]
    public void WithoutPlaybackReportsAStaleRoundIsNotReused()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// Reports for another video do not extend the round.
    /// </summary>
    [Fact]
    public void ReportsForAnotherVideoDoNotExtendTheRound()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(TimeSpan.FromMinutes(30));
        _tracker.RecordActivity(Device, Episode);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// Each device has its own round.
    /// </summary>
    [Fact]
    public void DevicesAreTrackedSeparately()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _tracker.Decide(Device, Movie, TwentyMbps, isPlayingOnDevice: true);

        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(OtherDevice, Movie, TwentyMbps, isPlayingOnDevice: false));
    }

    /// <summary>
    /// Rounds idle for a day are dropped when another round starts.
    /// </summary>
    [Fact]
    public void RoundsIdleForADayAreForgottenWhenAnotherRoundStarts()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(PlaybackRoundTracker.ForgetAfter + TimeSpan.FromSeconds(1));
        _tracker.Decide(OtherDevice, Episode, FortyMbps, isPlayingOnDevice: false);

        Assert.Equal(QualityDecision.NewRound, _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: true));
    }

    /// <summary>
    /// Idle rounds are only dropped when another round starts (negative control for the test above).
    /// </summary>
    [Fact]
    public void RoundsIdleForADayAreKeptUntilAnotherRoundStarts()
    {
        _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: false);
        _clock.Advance(PlaybackRoundTracker.ForgetAfter + TimeSpan.FromSeconds(1));

        Assert.Equal(QualityDecision.SameRound, _tracker.Decide(Device, Movie, FortyMbps, isPlayingOnDevice: true));
    }

    /// <summary>
    /// An empty device id is rejected.
    /// </summary>
    [Fact]
    public void RejectsAnEmptyDeviceId()
    {
        Assert.Throws<ArgumentException>(() => _tracker.Decide(string.Empty, Movie, FortyMbps, isPlayingOnDevice: false));
    }
}
