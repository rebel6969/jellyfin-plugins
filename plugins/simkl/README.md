<h1 align="center">Jellyfin SIMKL Plugin</h1>
<h3 align="center">Part of the <a href="https://jellyfin.org/">Jellyfin Project</a></h3>

###

## About this build (10.0.1.0)

This folder is the official [jellyfin-plugin-simkl](https://github.com/jellyfin/jellyfin-plugin-simkl) at tag `v10`
(commit `6faee47`), with one change. It keeps the official plugin's ID, so it installs over 10.0.0.0.

**What happened to the 9.1.0.0 fix.** 9.1.0.0 fixed a lost watch: version 9 ignored a playback started again shortly
after a stop, and its final stop, so on 2026-10-13 a whole movie never reached Simkl. Version 10 fixes that itself: its
scrobbler listens to Jellyfin's playback start and rebinds the session's state there. This folder's test project proves
it. Unmodified `v10` passes the replay test, the retry of an item the scrobbler gave up on, and the check that progress
after a finished playback opens no new scrobble.

**The change: a start reported again mid-playback no longer re-sends the start.** Version 10 rebinds and reports on
every playback start. A client also reports a start for the item already playing when it changes an audio or subtitle
track or a transcode restarts the stream. Version 10 then sends Simkl a second start, and the reset position reads as a
seek, which sends a third. 9.1.0.0 left such a scrobble alone, and this build does the same on version 10. A start for
an item whose scrobble is still open (started or paused, not given up on) only refreshes the session's last-seen time.
A finished, given-up or different item is rebound and reported at once, as in version 10.

Changed file: `Services/PlaybackScrobbler.cs`. The test project `Jellyfin.Plugin.Simkl.Tests` (upstream has none)
drives the scrobbler through Jellyfin's session events against a fake Simkl in real time, five tests, about 30 s:

- Unmodified `v10` fails only the mid-playback start test, which expects 1 start and gets 3.
- A mutant that removes the guard is caught by that test.
- Dropping the guard's item check is caught by the other-item test.
- Dropping its given-up check is caught by the retry test.
- Dropping its state check is caught by the replay test.

The build is pinned to Jellyfin.Controller 12.2.0 (upstream floats on `12.*-*`, which now resolves a 12.4 package) and
targets ABI 12.2.0.0.

```
dotnet build Jellyfin.Plugin.Simkl.sln -c Release
dotnet test Jellyfin.Plugin.Simkl.Tests/Jellyfin.Plugin.Simkl.Tests.csproj -c Release
```

The [Simkl Mark Sync](../simkl-mark-sync) plugin works with this build and with the official 9 and 10. From version
10 the official plugin has two client IDs, and Mark Sync 1.3.0.0 picks the one each login belongs to.

## Features
- Multi-user: every user connects their own Simkl account
- Reports play, pause and stop to Simkl as they happen, so your profile shows what you are watching now
- Marks an item as watched when playback stops past 80%; stopping earlier saves your position on Simkl, where the Playback Progress Manager lists it
- Mirrors marking played or unplayed in Jellyfin to your Simkl history, and can be switched off per user
- Imports your Simkl watch history into Jellyfin, on demand or on a schedule, with the date you watched each item
- Logs a rewatch when you finish something already in your history (needs Simkl Pro or VIP)
- Identifies an item by its file name when Simkl cannot match it by id
- Skips anything shorter than a runtime you choose

## Setup
Open Dashboard, then Plugins, then Simkl. Pick a user, click Log In, and enter the code it shows at
[simkl.com/pin](https://simkl.com/pin).

Jellyfin serves plugin settings to administrators only, so an administrator connects each user's account from
that one page. Accounts connected with the older sign-in keep working and can be reconnected from the same
page whenever convenient.
