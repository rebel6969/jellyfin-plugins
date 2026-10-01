using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MaxQuality.Tests;

/// <summary>
/// Runs Jellyfin's own StreamBuilder on a 55.6 Mbps 4K HEVC + TrueHD remux to show what the cap value does.
/// </summary>
public sealed class StreamBuilderDecisionTests
{
    private const int FortyMbps = 40_000_000;
    private const int RemuxBitrate = 55_642_432;

    /// <summary>
    /// A 40 Mbps client cap makes Jellyfin transcode the remux because of its bitrate alone.
    /// </summary>
    [Fact]
    public void AFortyMbpsCapForcesATranscodeOfTheRemux()
    {
        var stream = Decide(FortyMbps, deviceVideoCodecs: "hevc,h264");

        Assert.NotEqual(PlayMethod.DirectPlay, stream.PlayMethod);
        Assert.True(stream.TranscodeReasons.HasFlag(TranscodeReason.ContainerBitrateExceedsLimit));
    }

    /// <summary>
    /// The cap the filter sets lets the same remux direct play.
    /// </summary>
    [Fact]
    public void TheFilterCapDirectPlaysTheRemux()
    {
        var stream = Decide(MaxQualityActionFilter.UnlimitedBitrate, deviceVideoCodecs: "hevc,h264");

        Assert.Equal(PlayMethod.DirectPlay, stream.PlayMethod);
        Assert.Equal((TranscodeReason)0, stream.TranscodeReasons);
    }

    /// <summary>
    /// The filter's cap does not hide a real incompatibility: a device without HEVC still gets a transcode.
    /// That transcode keeps a sane video bitrate, which a cap of 0 would not.
    /// </summary>
    [Fact]
    public void TheFilterCapStillTranscodesAVideoCodecTheDeviceCannotDecode()
    {
        var stream = Decide(MaxQualityActionFilter.UnlimitedBitrate, deviceVideoCodecs: "h264");

        Assert.NotEqual(PlayMethod.DirectPlay, stream.PlayMethod);
        Assert.True(stream.TranscodeReasons.HasFlag(TranscodeReason.VideoCodecNotSupported));
        Assert.False(stream.TranscodeReasons.HasFlag(TranscodeReason.ContainerBitrateExceedsLimit));

        // A cap of 0 would also read as "no maximum" for direct play, but this path would then target 64 kbps video.
        Assert.True(stream.VideoBitrate > 1_000_000, $"transcode video bitrate {stream.VideoBitrate}");
    }

    private static StreamInfo Decide(int maxBitrate, string deviceVideoCodecs)
    {
        var transcoderSupport = new Mock<ITranscoderSupport>();
        transcoderSupport.Setup(t => t.CanEncodeToAudioCodec(It.IsAny<string>())).Returns(true);
        var builder = new StreamBuilder(transcoderSupport.Object, NullLogger.Instance);

        var stream = builder.GetOptimalVideoStream(new MediaOptions
        {
            ItemId = new Guid("d10d2a2f-c1cf-346d-d2a6-4f696a61a42d"),
            DeviceId = "test-device",
            MediaSources = [Remux()],
            Profile = Profile(deviceVideoCodecs),
            MaxBitrate = maxBitrate,
            Context = EncodingContext.Streaming,
            AllowAudioStreamCopy = true,
            AllowVideoStreamCopy = true,
            EnableDirectStream = false
        });

        Assert.NotNull(stream);
        return stream;
    }

    private static MediaSourceInfo Remux() => new()
    {
        Id = "d10d2a2fc1cf346dd2a64f696a61a42d",
        Path = "/media/movies/remux.mkv",
        Protocol = MediaProtocol.File,
        Container = "mkv",
        Bitrate = RemuxBitrate,
        SupportsDirectPlay = true,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160, BitRate = 50_261_365, IsDefault = true },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "truehd", Channels = 8, BitRate = 4_141_125, IsDefault = true }
        ]
    };

    private static DeviceProfile Profile(string videoCodecs) => new()
    {
        DirectPlayProfiles =
        [
            new DirectPlayProfile { Container = "mkv", Type = DlnaProfileType.Video, VideoCodec = videoCodecs, AudioCodec = "truehd,ac3,aac" }
        ],
        TranscodingProfiles =
        [
            new TranscodingProfile
            {
                Container = "mp4",
                Type = DlnaProfileType.Video,
                VideoCodec = "h264",
                AudioCodec = "aac",
                Protocol = MediaStreamProtocol.hls,
                Context = EncodingContext.Streaming
            }
        ]
    };
}
