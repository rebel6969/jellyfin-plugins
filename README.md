# Simkl Mark Sync for Jellyfin

A Jellyfin server plugin that sends the movies and episodes you **mark played or unplayed by hand** to
[Simkl](https://simkl.com). It completes the official Simkl plugin, which only sends what you actually play.

Built and tested against **Jellyfin 12.1** (`targetAbi 12.1.0.0`, .NET 10) and the official **Simkl plugin 9.0.0.0**.

## Why

The official Simkl plugin listens to playback only. Ticking "Mark played" on an episode, or marking a whole
season or series, never reaches Simkl. The Trakt plugin sends these marks; this plugin does the same for Simkl.

## Requirements

- The official **Simkl** plugin, installed, with your Jellyfin user linked to Simkl in its settings.
  This plugin has no login of its own. It reads the login the Simkl plugin saved, plus that plugin's per-user
  **Scrobble movies** and **Scrobble shows** switches.

## What it sends

| You do this in Jellyfin | Simkl gets |
|---|---|
| Mark a movie or episode played | Added to your history, with Jellyfin's last-played time as the watch time |
| Mark a season or series played | One request carrying every episode, grouped by show and season |
| Mark an episode unplayed | That episode unmarked; the show stays in your library |
| Mark a movie unplayed | Removed from your history, **only if Simkl lists it as completed** (see below) |

- Marks are collected until none arrive for 3 seconds (at most 15 seconds), then sent together. Marking and then
  unmarking an item within that window sends only the unmark.
- Playback is left to the official plugin. Ratings, favourites and missing (virtual) episodes are ignored.
- A user who turned **Scrobble movies** or **Scrobble shows** off in the Simkl plugin gets no movies, or no
  episodes, sent.

### Safety

- **A removal never names a show without explicit episodes.** Simkl removes a show sent that way from your library
  entirely, with all its history. The plugin builds every removal episode by episode, and checks the request again
  just before sending it. A request that fails the check is refused and never sent.
- **Removals carry IDs only.** Adds include the title and year, so Simkl can fall back to them when no ID
  matches. A removal leaves them out, so an unmatched ID is reported as not found instead of matching a different
  title by name.
- **Movies are removed only when completed.** Simkl's only way to unmark a movie is to remove it from your
  library. That would also delete a "Plan to watch" entry, so the plugin first asks Simkl
  (`POST /sync/watched`). It removes a movie only when Simkl reports it in the **completed** list.

### Simkl's API rules

- One request at a time, at least 1.1 seconds apart. Simkl allows one POST per second per login.
- A 429, a 5xx or a network timeout is retried up to 3 times. The waits are 2 s, then 4 s, or whatever
  `Retry-After` says, capped at 60 s. These calls are safe to repeat, because Simkl ignores an add for an item
  already watched.
- Any other rejection is logged and not retried. So is a web page instead of JSON, such as a proxy's challenge page.
- Calls use the client ID of the installed official plugin, because Simkl ties each login to the client ID it was
  issued to. The plugin reads that ID from the loaded Simkl plugin at runtime and does not bundle a copy. Simkl
  documents a client ID as a public identifier, not a secret.

## Install

### From the plugin repository

In Dashboard › Plugins › Repositories, add:

```
https://raw.githubusercontent.com/rebel6969/jellyfin-plugin-simkl-mark-sync/main/manifest.json
```

Then install **Simkl Mark Sync** from the catalog and restart Jellyfin.

### Manually

1. Download `simkl-mark-sync_1.0.0.0.zip` from the
   [releases](https://github.com/rebel6969/jellyfin-plugin-simkl-mark-sync/releases).
2. Extract it into `<jellyfin data dir>/plugins/Simkl Mark Sync_1.0.0.0/`.
3. Restart Jellyfin.

### Check that it is active

Dashboard › Plugins lists **Simkl Mark Sync**. At startup it logs whether it found the official Simkl plugin, and
each batch is logged at Information level, for example:

```
Simkl Mark Sync is ready: found the official Simkl plugin's client ID
Simkl: 3 items marked watched for user <id>; newly added 0 movies and 3 episodes, the rest were already watched
Simkl: unmarked for user <id>: 0 of 0 movies and 1 episodes removed
```

Items Simkl could not match, rejected logins and failed calls are logged as warnings or errors, with Simkl's reason.

## Build and test

```
dotnet build Jellyfin.Plugin.SimklMarkSync.slnx -c Release
dotnet test Jellyfin.Plugin.SimklMarkSync.slnx -c Release
```

The build treats every analyzer warning as an error.

## License

GPL-3.0, like Jellyfin and the official Simkl plugin. See [LICENSE](LICENSE).
