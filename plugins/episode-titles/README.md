# Episode Titles for Jellyfin 12.1+

Replaces placeholder episode names with real titles, using the metadata providers the library already has, in the order
the library already sets. It adds no provider of its own and changes no library setting.

## Why

Jellyfin merges the remote providers' results in fetcher order and keeps the **first non-empty name**
(`MetadataService.MergeData` with `replaceData: false`). A provider that answers with a stand-in therefore hides a real
title from every provider after it:

- AniDB lists an untitled episode as `<title xml:lang="en">Episode 3</title>`, and the AniDB plugin passes that on as
  the name. In an anime library with AniDB first, the episode is called "Episode 3" although TheTVDB (and Sonarr,
  which reads TheTVDB) has its title.
- When no provider has a title, Jellyfin keeps the name it took from the file, such as
  `Show - S02E01 - TBA WEBDL-1080p`.
- Jellyfin fetches an episode's metadata when the file arrives and not again, so a title that reaches a provider
  later (new simulcasts often get their English title days after release) never replaces the placeholder.

Measured on the author's server on 2026-10-14: 40 episodes across 14 anime series were named "Episode N" while Sonarr
had real titles, and 17 of the files had been imported while TheTVDB still said "TBA".

## Behavior

- **After every metadata refresh of an episode** (a new file, *Refresh metadata*, any full refresh), if the name is a
  placeholder, the library's remote episode providers are asked again, in the library's order, and the first real
  title is kept. A real name is never replaced, so AniDB's titles still win where AniDB is first and has one.
- **Placeholders**: empty; `Episode <number>`; `TBA`; `TBD` (case-insensitive, surrounding spaces ignored); or exactly
  the file name without its extension. A title that only starts with those words ("Episode 6.5 ...",
  "Episode 3: The Return") is a real title.
- **Locks**: an item that is locked, or whose Name field is locked, is left alone.
- **Re-check task** (Dashboard > Scheduled Tasks > Episode Titles > *Re-check placeholder episode titles*, daily by
  default): refreshes placeholder-named episodes that have a file, so late titles arrive. Each episode is re-checked
  every seventh of its age (from its premiere date, or the date it was added), at least daily and at most every
  30 days, measured from its last refresh. A show whose episodes never get titles (many dramas list only
  "Episode 1, 2, ...") settles at monthly checks. The refresh queries every provider without replacing existing data
  (FullRefresh, ReplaceAllMetadata off).
- A provider that throws (a timeout, a ban) is skipped and logged; the next one is asked. Cancellation stops the work.
- Log lines start with `EpisodeTitles:`, for example
  `"Reincarnated as a Sword" S2E3 was named "Episode 3", a placeholder; "TheTVDB" has "The Undead Monster Was Tough as Nails"`.

## Boundaries

- If no provider has a real title yet, the placeholder stays until one does.
- Names that come from a local `.nfo` file are not placeholders unless they match the rules above. Jellyfin 12.2 always
  reads `.nfo` files (its `DisabledLocalMetadataReaders` library option is defined but not read), and an `.nfo`
  title wins over every online provider; fix or remove a wrong `.nfo` instead.
- Only episodes. Series, season and movie names are untouched.

## Install

Add the repository `https://raw.githubusercontent.com/rebel6969/jellyfin-plugins/main/manifest.json` in Jellyfin,
install **Episode Titles**, restart Jellyfin. There is nothing to configure. To fix existing placeholders at once, run
the *Re-check placeholder episode titles* task.

## Build and tests

.NET SDK 10; Jellyfin.Controller and Jellyfin.Model pinned to 12.1.0. Plugin ID `c63652fb-8ecd-4791-928b-d769b21b8b08`.

```sh
dotnet test EpisodeTitles.Tests/EpisodeTitles.Tests.csproj -c Release
dotnet format EpisodeTitles.Tests/EpisodeTitles.Tests.csproj --verify-no-changes
dotnet build EpisodeTitles/EpisodeTitles.csproj -c Release -p:ContinuousIntegrationBuild=true
```

## Verification (1.0.0.0)

- 64 tests, warnings as errors, `dotnet format` clean; 23 of 23 targeted mutants killed by their named tests.
- Live Jellyfin 12.2 from the production image, with production's AniDB 13 and TheTVDB 24 plugins, their
  configuration, the AniDB cache and the Anime library's options:
  - without the plugin, S02E01 of Reincarnated as a Sword imports as "The Floating Island" (AniDB);
  - with the plugin, S02E03 imports as "The Undead Monster Was Tough as Nails" (AniDB says "Episode 3") and S02E04 as
    "The Hopeless Battle Against the Undead King" (AniDB says "Episode 4");
  - S02E01 set to its production state ("Episode 1", last refreshed 3 days ago) is left alone by the library scan and
    renamed "The Floating Island" by the re-check task, which found exactly that one episode due.

## Rollback

Uninstall the plugin in Dashboard > Plugins and restart Jellyfin. Names it set stay until the episodes are refreshed.
