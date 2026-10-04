# Max Quality for Jellyfin

A Jellyfin server plugin that plays every video at its **original quality**. A client's bitrate limit no longer
forces a transcode, but a quality **you pick during playback is always respected** for the rest of that video.

Built and tested against **Jellyfin 12.1** (`targetAbi 12.1.0.0`, .NET 10).

## Why

Jellyfin clients send a maximum streaming bitrate with every playback request. When a file's bitrate is above it,
the server transcodes (`TranscodeReason: ContainerBitrateExceedsLimit`), even when the client could direct play the
file and the network can carry it. A 4K remux at 55.6 Mbps with a client set to 40 Mbps gets re-encoded. On a
server without a hardware encoder, that software transcode ran at 9.3 fps, under half of the film's 23.976 fps, so
playback buffered constantly. At 60 Mbps the same file direct played without trouble.

## What it does

For each `POST /Items/{id}/PlaybackInfo` request for a **video**:

| Situation | Result |
|---|---|
| First request for a video on a device (start of a "playback round") | Bitrate cap removed: direct play whenever the device can decode the file |
| Later request in the same round at the same bitrate (audio/subtitle change, stream restart) | Cap stays removed |
| Later request in the same round at a **different** bitrate | That is you picking a quality: **left exactly as sent** |
| Any further request in that round | Your choice keeps being respected |
| Another video, or the same video after the round ends | New round, cap removed again |

A round lasts while the device's session reports the video as playing (pauses included), plus 90 seconds after the
last request or playback report. Rounds are tracked per device.

What it does **not** change:

- Codec, container, audio-channel or subtitle incompatibilities still transcode. The plugin only removes the bitrate cap.
- The server's own **Internet streaming bitrate limit** (Dashboard › Playback, or per-user remote limit) still applies to remote clients.
- Music and other non-video items are untouched.

The removed cap is `int.MaxValue`, the same value Jellyfin's `StreamBuilder` uses for "no maximum". A cap of `0`
would also skip the bitrate check, but Jellyfin's transcode path would then target 64 kbps video. If a transcode is
still needed, Jellyfin's encoder keeps its own 400 Mbps ceiling.

### Limits of the detection

The server only sees the bitrate each request carries. Picking the **same** value your client was already sending is
indistinguishable from a stream restart, so it does not count as a change: pick a different value to force a lower
quality. Clients that save the picked value as their new default send it again for the next video; that next video
starts a new round and gets its cap removed, as intended.

## Install

### From the plugin repository

Dashboard › Plugins › Repositories › add:

```
https://raw.githubusercontent.com/rebel6969/jellyfin-plugins/main/manifest.json
```

Then install **Max Quality** from the catalog and restart Jellyfin.

### Manually

Download `max-quality_1.0.1.0.zip` from the [releases](https://github.com/rebel6969/jellyfin-plugins/releases/tag/max-quality-v1.0.1.0),
extract it into `<jellyfin data dir>/plugins/Max Quality_1.0.1.0/`, and restart Jellyfin.

### Check that it is active

Dashboard › Plugins lists **Max Quality**. Each playback decision is logged at Information level, for example:

```
NewRound: The Fantastic 4: First Steps on device <id> asked for 40000000 bps; bitrate cap removed
ViewerChangedQuality: The Fantastic 4: First Steps on device <id> asked for 20000000 bps; keeping the viewer's choice
```

## Build and test

```
dotnet build Jellyfin.Plugin.MaxQuality.slnx -c Release
dotnet test Jellyfin.Plugin.MaxQuality.slnx -c Release
```

Warnings are errors and every .NET, StyleCop and Serilog analyzer is enabled. The tests cover the round rules, the
filter against Jellyfin's request shape, the service registration, the plugin class as Jellyfin's loader creates it,
and Jellyfin's own `StreamBuilder` deciding on a 55.6 Mbps HEVC/TrueHD remux.

## How it hooks in

`PluginServiceRegistrator` adds a global MVC action filter. Jellyfin's `MediaInfoController.GetPostedPlaybackInfo`
takes the `maxStreamingBitrate` argument ahead of the posted body and the device profile, so setting that one argument
overrides every client-side source of the cap. `PlaybackActivityListener` feeds playback start, progress and stop
reports into the round tracker.

## License

GPL-3.0, the license of the Jellyfin packages it builds against.
