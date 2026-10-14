# English SDH Preference for Jellyfin 12.1

Standalone, opt-in subtitle preference plugin. Selects non-forced English SDH/HI/CC when present, otherwise full regular English. Does not alter audio choices, media files, subtitle files, server user preferences, or accounts not explicitly selected; the one audio change is that a play request naming an audio track the file does not have falls back to Jellyfin's default audio.

## Install

In Jellyfin, add this repository URL:

`https://raw.githubusercontent.com/rebel6969/jellyfin-plugins/main/manifest.json`

Install **English SDH Preference**, restart Jellyfin, then open the plugin configuration page. Enable it and select users. It is disabled with no users selected by default. Existing Language Failover or other automatic subtitle-switching plugins should not also be enabled for the same users.

Manual install: stop Jellyfin; extract the release ZIP ([releases](https://github.com/rebel6969/jellyfin-plugins/releases/tag/english-sdh-v1.1.0.0)) into a new `EnglishSdh_1.1.0.0` directory under the server's plugins directory; restart. Do not overwrite another plugin. Keep a stopped-server backup first.

## Behavior

- PlaybackInfo check (1.1.0.0): when an opted-in user's play request names an audio or subtitle index that the file does not have, or one of the wrong kind (an audio index that is a subtitle), the request was built for a different file, typically an item page left open while Sonarr replaced the file. Both indexes are then dropped: audio falls back to Jellyfin's default and subtitles start on this plugin's English choice. Requests whose indexes all fit the file, including subtitles off (-1), are left as sent. The query argument is read ahead of the posted body, as Jellyfin does.
- At PlaybackStart, wait 1.5 seconds for client initialization, then send at most one `SetSubtitleStreamIndex` command.
- Require the current session user to be opted in and the client to advertise this command. A client that has reported no capabilities at all (no commands and no media types) but is connected is sent the command anyway (1.1.0.0): Jellyfin keeps capabilities only in memory and web-based clients such as Jellium Desktop report them once at sign-in, so after a server restart their session lists none although they still handle commands. A client that reported capabilities without this command is still skipped.
- Match English language metadata (`en`, `eng`, `English`, and hyphenated regional tags such as `en-US`, `en-GB`, `eng-US`), not guesses from the title.
- Prefer the hearing-impaired flag; recognize SDH, CC, HI, hearing-impaired and closed-caption title labels. Exclude English `forced` labels/flags and commentary titles; ignore negated labels such as `non-SDH` and `non-forced`.
- Among equivalent tracks prefer SDH, then the hearing-impaired flag, default flag, and lowest stream index.
- No eligible English/full track: leave the client's choice untouched, including forced-only content.
- Recheck item, source, user and selected track after the delay and before sending. A reported manual selection during initialization wins. Duplicate start events are suppressed for that playback. Later manual changes are not polled or reverted.
- Stop events invalidate pending work. Commands have a 10-second overall deadline and server-shutdown cancellation. Exceptions are recorded in Jellyfin logs without aborting playback.

## Boundaries

The SDH preference is applied after playback starts; Jellyfin's initial default-track algorithm is changed only for a play request whose track numbers do not exist in the file (see the PlaybackInfo check). Otherwise the original subtitle may appear briefly. A stale request whose old numbers happen to name tracks of the right kind in the new file cannot be told apart from a deliberate choice; for subtitles the post-start command still applies the preference. A client must report playback and honor the command; advertising a capability or receiving a command is not proof of rendering. The plugin cannot detect manual choices that the client does not report, or atomically prevent a session changing after the final check while a command is in flight. Non-GUID/live sources are skipped rather than guessing track indices.

Do not use it to solve failed video playback. It is not a universal client compatibility fix. Unsupported/mislabeled tracks may need manual selection. Only English title labels are interpreted; the metadata flags remain authoritative.

## Build and tests

.NET SDK 10; Jellyfin.Controller and Jellyfin.Model pinned to 12.1.0; ASP.NET Core shared framework for the PlaybackInfo filter. Production assembly version: 1.1.0.0. Unique plugin ID: `36dab5c5-2396-4b09-9c63-a46c56288c68`.

```sh
dotnet test EnglishSdh.Tests/EnglishSdh.Tests.csproj -c Release
dotnet format EnglishSdh.Tests/EnglishSdh.Tests.csproj --verify-no-changes
dotnet build EnglishSdh/EnglishSdh.csproj -c Release -p:ContinuousIntegrationBuild=true
```

1.1.0.0 was built with `mcr.microsoft.com/dotnet/sdk@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29` (1.0.0.x with `sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d`).

Verification (1.1.0.0): 86 automated tests, warnings as errors, `dotnet format` clean. The settle-delay tests now run on a fake clock (`FakeTimeProvider`), because the 30 ms real-time versions failed under a throttled CPU. 11 of 11 targeted mutants (one per new safety rule) were killed by their named test. A throwaway Jellyfin 12.2.0 from the production image, with a synthetic file laid out like the 2026-10-14 episode (video 0, audio 1, English 2 default, Arabic 3, German 4, Spanish 5) plus English SDH 6:
- PlaybackInfo with audio 2 and subtitle 5, in the body and in the query: 1.1.0.0 returned audio 1 and subtitle 6; 1.0.0.1 returned subtitle 5. A request that fits the file (audio 1, subtitle 5) kept subtitle 5 on both.
- A client that reported capabilities, then only reconnected its WebSocket after a server restart, listed no commands. 1.1.0.0 sent it `SetSubtitleStreamIndex` 6; 1.0.0.1 logged "SetSubtitleStreamIndex not advertised" and sent nothing.
- A client that reported capabilities without the command received nothing on both versions.

This proves the server/plugin path, not every actual player. No production media or credentials were used in the sandbox.

## Provenance

The design was informed by [Hightmar/jellyfin-langage-failover](https://github.com/Hightmar/jellyfin-langage-failover) at `054d72c6e30a0db1e30caef52c16413e5495e6d2` (MIT). Its playback-event and GeneralCommand approach was inspected. This is a newly written, smaller implementation with its own identity, selection logic and tests; it does not include the upstream audio/language-priority engine. It is isolated development, **not a formal clean-room claim**, because upstream source was read. This is not an official Jellyfin or Hightmar release.

## Rollback

Disable the plugin on its configuration page to stop new selections without restarting. To uninstall, use Jellyfin's plugin management and restart, or stop Jellyfin and remove only the `EnglishSdh_1.1.0.0` directory. Configuration is `Jellyfin.Plugin.EnglishSdh.xml`. No media or database restore is needed for normal removal; avoid restoring old databases unless separately necessary.

## Diagnostics

Jellyfin logs contain `EnglishSdh` messages for sent commands, unsupported clients, clients that reported no capabilities since Jellyfin started, PlaybackInfo requests whose track numbers did not fit the file, and failures. "Sent" deliberately does not mean "applied by the client". Compare playback progress's SubtitleStreamIndex or the player's subtitle menu to confirm application.
