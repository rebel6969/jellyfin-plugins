# Trakt Gap Filler

A small companion to the official Jellyfin **Trakt** plugin. It finds episodes you watched in Jellyfin that never
reached your Trakt history, works out which Trakt episode they are, and adds the play. Single Python file, standard
library only, run every 15 minutes by a systemd timer. Tested with Jellyfin 12.1, Trakt plugin 33.0.0.0, Python 3.12.

## Why

Jellyfin numbers anime by TVDB (`S03E02`); Trakt follows TMDB, which often keeps a show in one long season
(`S01E26`). For a new episode Trakt also tends to hold only its TMDB id for the first days, while Jellyfin holds
TVDB and IMDb ids. The Trakt plugin then gets "not found" twice, by ids and by show + season/episode, and gives up.
It logs that only at Debug level, so the episode is silently missing from Trakt.

## How it decides

Each run, for every Jellyfin user linked in the Trakt plugin with **Scrobble** or **Post set watched** on:

1. Lists the episodes Jellyfin marks played whose last play is within the lookback (default 72 h).
2. Finds the Trakt episode: by the episode's own TVDB/TMDB/IMDb ids, accepted only if Trakt says it belongs to the
   same show; otherwise in the show's Trakt episode list by **air date (within one day)**, preferring the same
   season and number, then the same title. Anything that is not exactly one episode is left alone and reported once.
3. Skips the episode if Trakt already has **any** play of it. Jellyfin marks an episode played only when it is
   played to the end or marked by hand, so an episode Trakt has never seen is a missed play.
4. Adds one play, dated by Jellyfin's "finished playing" activity entry (else the last-played time), but only after
   the gap has been seen on two runs at least 10 minutes apart, so it never races the plugin's own scrobble.

It never removes anything from Trakt, never refreshes the plugin's login (it reads the token the plugin saved), and
does not handle movies (the plugin matches movies by id reliably) or replays of an episode Trakt already has.
The Trakt client id is found automatically: of the two keys inside the plugin's DLL, the one Trakt accepts with
your token.

## Install

```sh
install -m 755 trakt_gap_filler.py /usr/local/bin/trakt_gap_filler.py
install -m 644 trakt-gap-filler.service trakt-gap-filler.timer /etc/systemd/system/
printf 'JELLYFIN_API_TOKEN=%s\n' '<api key>' > /etc/trakt-gap-filler.env && chmod 600 /etc/trakt-gap-filler.env
# edit JELLYFIN_PLUGINS_DIR in the service, then try a dry run:
env JELLYFIN_API_TOKEN='<api key>' JELLYFIN_PLUGINS_DIR=/var/lib/jellyfin/plugins STATE_DIRECTORY=/tmp/tgf \
    python3 /usr/local/bin/trakt_gap_filler.py
systemctl daemon-reload && systemctl enable --now trakt-gap-filler.timer
```

Without `--apply` it only logs what it would add. Every decision is logged to the journal
(`journalctl -u trakt-gap-filler`), for example:

```
As a Reincarnated Aristocrat, I'll Use My Appraisal Skill to Rise in the World S03E02 'Episode 2' -> Trakt S01E26 'Canarre in Crisis' (id 14531280, by air date): already on Trakt
```

| Variable | Default | Meaning |
|---|---|---|
| `JELLYFIN_API_TOKEN` | required | A Jellyfin API key |
| `JELLYFIN_PLUGINS_DIR` | required | Folder holding `configurations/Trakt.xml` and `Trakt_*/Trakt.dll` |
| `JELLYFIN_URL` | `http://127.0.0.1:8096` | Jellyfin's address |
| `TRAKT_GAP_FILLER_LOOKBACK_HOURS` | `72` | How far back to look at plays |
| `TRAKT_GAP_FILLER_SINCE` | none | Never look at plays before this UTC time (e.g. when you linked Trakt) |
| `TRAKT_GAP_FILLER_SETTLE_MINUTES` | `10` | How long a gap must persist before it is filled |
| `TRAKT_CLIENT_ID` | auto | Set only to skip the automatic detection |
| `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID` | none | Optional: a message for each play added and each episode it could not match |

## Tests

```sh
python3 -W error -m unittest test_trakt_gap_filler
```

25 tests against fake Jellyfin and Trakt APIs built from the real case above; each safety rule was also broken on
purpose and caught by its test (21 of 21).
