# Simkl Mark Sync for Jellyfin

A Jellyfin server plugin that sends to [Simkl](https://simkl.com) the movies and episodes you **mark played or
unplayed by hand**, and **every playback Jellyfin counts as finished**. It completes the official Simkl plugin, which
only scrobbles playback and can miss one.

Built and tested against **Jellyfin 12.1** (`targetAbi 12.1.0.0`, .NET 10) and the official **Simkl plugin 9.0.0.0**
(also with this repository's [9.1.0.0 build](../simkl) of it).

## Why

- The official Simkl plugin listens to playback only. Ticking "Mark played" on an episode, or marking a whole season
  or series, never reaches Simkl. The Trakt plugin sends these marks; this plugin does the same for Simkl.
- The official plugin's scrobble can also miss a playback. On 2026-10-13 version 9 dropped a whole movie: the client
  stopped it at 0.4%, started it again 24 seconds later and watched it to the end, and the plugin's per-session state
  ignored the second playback and its final stop. Since 1.1.0.0 this plugin sends every finished playback as well, so
  the watch reaches Simkl whatever the official plugin does. Simkl ignores a watch it already has, so a playback both
  plugins report is counted once.

## Requirements

- The official **Simkl** plugin, installed, with your Jellyfin user linked to Simkl in its settings.
  This plugin has no login of its own. It reads the login the Simkl plugin saved, plus that plugin's per-user
  **Scrobble movies** and **Scrobble shows** switches.

## What it sends

| You do this in Jellyfin | Simkl gets |
|---|---|
| Watch a movie or episode past Jellyfin's "played" point (90% by default) | Added to your history, with the time playback stopped as the watch time |
| Mark a movie or episode played | Added to your history, with Jellyfin's last-played time as the watch time |
| Mark a season or series played | One request carrying every episode, grouped by show and season |
| Mark an episode unplayed | That episode unmarked; the show stays in your library |
| Mark a movie unplayed | Removed from your history, **only if Simkl lists it as completed** (see below) |

- Marks are collected until none arrive for 3 seconds (at most 15 seconds), then sent together. Marking and then
  unmarking an item within that window sends only the unmark.
- A playback stopped before the "played" point sends nothing; the official plugin keeps its progress on Simkl.
- Ratings, favourites and missing (virtual) episodes are ignored.
- A user who turned **Scrobble movies** or **Scrobble shows** off in the Simkl plugin gets no movies, or no
  episodes, sent.

### Rewatches (Simkl PRO or VIP)

Simkl records a second watch of something you already finished only when the app asks for it with `allow_rewatch=yes`,
and only for PRO and VIP accounts. On a free account a request carrying that flag is dropped whole, first watches
included. So:

- **Track rewatches** on the plugin's settings page (Dashboard › Plugins › Simkl Mark Sync) is **off by default**, as
  Simkl asks of apps.
- When it is on, the plugin reads your plan from Simkl (`GET /users/settings`, cached for 6 hours) before sending
  finished playbacks. On PRO or VIP they go out with `allow_rewatch=yes`; on a free plan, or when the plan cannot be
  read, they go out as plain watches. A `pro_required` answer makes the plugin read the plan again on the next batch.
- Only finished playbacks can become rewatches. A manual "Mark played" is always sent as a plain watch.
- Simkl absorbs two watches of the same item less than two days apart, so a playback the official plugin also
  scrobbled is never counted twice.

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
https://raw.githubusercontent.com/rebel6969/jellyfin-plugins/main/manifest.json
```

Then install **Simkl Mark Sync** from the catalog and restart Jellyfin.

### Manually

1. Download `simkl-mark-sync_1.1.0.0.zip` from the
   [releases](https://github.com/rebel6969/jellyfin-plugins/releases/tag/simkl-mark-sync-v1.1.0.0).
2. Extract it into `<jellyfin data dir>/plugins/Simkl Mark Sync_1.1.0.0/`.
3. Restart Jellyfin.

### Check that it is active

Dashboard › Plugins lists **Simkl Mark Sync**. At startup it logs whether it found the official Simkl plugin, and
each batch is logged at Information level, for example:

```
Simkl Mark Sync is ready: found the official Simkl plugin's client ID
Simkl: 1 items marked watched for user <id> (1 from finished playbacks); newly added 0 movies and 0 episodes, the rest were already watched
Simkl: rewatch status completed for 1 items of user <id>
Simkl: unmarked for user <id>: 0 of 0 movies and 1 episodes removed
```

"Newly added 0" after a finished playback means the official plugin had already recorded it. Items Simkl could not
match, rejected logins and failed calls are logged as warnings or errors, with Simkl's reason.

## Build and test

```
dotnet build Jellyfin.Plugin.SimklMarkSync.slnx -c Release
dotnet test Jellyfin.Plugin.SimklMarkSync.slnx -c Release
```

The build treats every analyzer warning as an error.

## License

GPL-3.0, like Jellyfin and the official Simkl plugin. See [LICENSE](LICENSE).
