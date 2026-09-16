# English SDH Preference for Jellyfin 12.1

Standalone, opt-in subtitle preference plugin. Selects non-forced English SDH/HI/CC when present, otherwise full regular English. Does not alter audio, media files, subtitle files, server user preferences, or accounts not explicitly selected.

## Install

Use the package and manifest in this directory's `dist/` and `manifest.json`. In Jellyfin, add this repository URL:

`https://raw.githubusercontent.com/rebel6969/jellyfin-plugin-english-sdh/main/manifest.json`

Install **English SDH Preference**, restart Jellyfin, then open the plugin configuration page. Enable it and select users. It is disabled with no users selected by default. Existing Language Failover or other automatic subtitle-switching plugins should not also be enabled for the same users.

Manual install: stop Jellyfin; extract the release ZIP into a new `EnglishSdh_1.0.0.1` directory under the server's plugins directory; restart. Do not overwrite another plugin. Keep a stopped-server backup first.

## Behavior

- At PlaybackStart, wait 1.5 seconds for client initialization, then send at most one `SetSubtitleStreamIndex` command.
- Require the current session user to be opted in and the client to advertise this command.
- Match English language metadata (`en`, `eng`, `English`, and hyphenated regional tags such as `en-US`, `en-GB`, `eng-US`), not guesses from the title.
- Prefer the hearing-impaired flag; recognize SDH, CC, HI, hearing-impaired and closed-caption title labels. Exclude English `forced` labels/flags and commentary titles; ignore negated labels such as `non-SDH` and `non-forced`.
- Among equivalent tracks prefer SDH, then the hearing-impaired flag, default flag, and lowest stream index.
- No eligible English/full track: leave the client's choice untouched, including forced-only content.
- Recheck item, source, user and selected track after the delay and before sending. A reported manual selection during initialization wins. Duplicate start events are suppressed for that playback. Later manual changes are not polled or reverted.
- Stop events invalidate pending work. Commands have a 10-second overall deadline and server-shutdown cancellation. Exceptions are recorded in Jellyfin logs without aborting playback.

## Boundaries

This is a post-start preference, not a change to Jellyfin's initial default-track algorithm. The original subtitle may appear briefly. A client must report playback and honor the command; advertising a capability or receiving a command is not proof of rendering. The plugin cannot detect manual choices that the client does not report, or atomically prevent a session changing after the final check while a command is in flight. Non-GUID/live sources are skipped rather than guessing track indices.

Do not use it to solve failed video playback. It is not a universal client compatibility fix. Unsupported/mislabeled tracks may need manual selection. Only English title labels are interpreted; the metadata flags remain authoritative.

## Build and tests

.NET SDK 10; Jellyfin.Controller and Jellyfin.Model pinned to 12.1.0. Production assembly version: 1.0.0.1. Unique plugin ID: `36dab5c5-2396-4b09-9c63-a46c56288c68`.

```sh
dotnet test EnglishSdh.Tests/EnglishSdh.Tests.csproj -c Release
dotnet build EnglishSdh/EnglishSdh.csproj -c Release
```

Build used `mcr.microsoft.com/dotnet/sdk@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d`.

Verification: 61 automated tests, including negative controls, duplicate events, stopped/changed playback, manual-selection protection and logged command failure. A separate Jellyfin 12.1.0 instance with synthetic media loaded the plugin and sent index 3 (English SDH) instead of index 2 (regular English) to an authenticated WebSocket test client. This proves the server/plugin command path, not every actual player. No production media or credentials were used in that sandbox.

## Provenance

The design was informed by [Hightmar/jellyfin-langage-failover](https://github.com/Hightmar/jellyfin-langage-failover) at `054d72c6e30a0db1e30caef52c16413e5495e6d2` (MIT). Its playback-event and GeneralCommand approach was inspected. This is a newly written, smaller implementation with its own identity, selection logic and tests; it does not include the upstream audio/language-priority engine. It is isolated development, **not a formal clean-room claim**, because upstream source was read. This is not an official Jellyfin or Hightmar release.

## Rollback

Disable the plugin on its configuration page to stop new selections without restarting. To uninstall, use Jellyfin's plugin management and restart, or stop Jellyfin and remove only the `EnglishSdh_1.0.0.1` directory. Configuration is `Jellyfin.Plugin.EnglishSdh.xml`. No media or database restore is needed for normal removal; avoid restoring old databases unless separately necessary.

## Diagnostics

Jellyfin logs contain `EnglishSdh` messages for sent commands, unsupported clients, and failures. "Sent" deliberately does not mean "applied by the client". Compare playback progress's SubtitleStreamIndex or the player's subtitle menu to confirm application.
