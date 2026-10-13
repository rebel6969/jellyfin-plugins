<h1 align="center">Jellyfin SIMKL Plugin</h1>
<h3 align="center">Part of the <a href="https://jellyfin.org/">Jellyfin Project</a></h3>

###

## About this build (9.1.0.0)

This folder is the official [jellyfin-plugin-simkl](https://github.com/jellyfin/jellyfin-plugin-simkl) at tag `v9`
(commit `82b3695`), with one fix. It keeps the official plugin's ID, so it installs over 9.0.0.0 and Jellyfin replaces
it with any later official release.

**The fix: a playback started again shortly after a stop is now scrobbled.** Version 9 keeps one scrobble state per
Jellyfin session. A session ID is reused across playbacks, and once a state reached "stopped" it ignored every later
event for the same item until five minutes passed with no progress at all. So stopping a movie, starting it again within
those five minutes (to restart it, or to reopen it with another audio or subtitle track) and watching it to the end sent
nothing to Simkl, not even the final stop, and logged nothing. On 2026-10-13 that lost a full watch: the client
stopped at 0.4%, started again 24 seconds later and stopped at 100%. Upstream issue
[#71](https://github.com/jellyfin/jellyfin-plugin-simkl/issues/71) reports the same symptom.

This build also listens to Jellyfin's playback start event. A start resets the session's state unless a scrobble of
the item is still open (a client that reports a start again mid-playback causes no extra calls), and it also retries an
item the plugin had given up on. A stop ignored for an already finished playback is now logged at debug level.

Changed files: `Services/PlaybackScrobbler.cs` and `Services/SessionScrobbleTracker.cs`. New: the test project
`Jellyfin.Plugin.Simkl.Tests` (upstream has none). It drives the scrobbler through Jellyfin's session events against a
fake Simkl in real time (the plugin spaces calls 5 s apart, so the four tests take about 45 s). Unmodified upstream `v9`
fails two of them, and each of three mutants of the fix fails a named test.

The build is pinned to Jellyfin.Controller 12.2.0 (upstream floats on `12.*-*`) and targets ABI 12.2.0.0.

```
dotnet build Jellyfin.Plugin.Simkl.sln -c Release
dotnet test Jellyfin.Plugin.Simkl.Tests/Jellyfin.Plugin.Simkl.Tests.csproj -c Release
```

The [Simkl Mark Sync](../simkl-mark-sync) plugin also sends every playback Jellyfin counts as finished to Simkl, so a
watch reaches Simkl even if a future official release brings the problem back.

## Current features
- Multi-user support
- Auto scrobble Movies and TV Shows at given percentage to Simkl
- Easy login using pin
- If scrobbling fails, search it using filename using Simkl's API and then scrobble it

## Future features
- Sync all watch status with Simkl
