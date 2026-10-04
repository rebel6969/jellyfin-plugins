# Jellyfin plugins

My Jellyfin plugins and customizations in one place, built and tested against **Jellyfin 12.1**.

## Install

Add one plugin repository in Dashboard › Plugins › Repositories:

```
https://raw.githubusercontent.com/rebel6969/jellyfin-plugins/main/manifest.json
```

Every plugin below then appears in the catalog. Install the ones you want and restart Jellyfin. Each zip is
attached to a release of this repository, and the manifest carries its MD5 checksum.

## Plugins

| Plugin | What it does | Version | Folder | License |
|---|---|---|---|---|
| **Max Quality** | Direct plays every video at its original quality; a quality you pick during playback is respected for that video | 1.0.1.0 | [`plugins/max-quality`](plugins/max-quality) | GPL-3.0 |
| **Simkl Mark Sync** | Sends movies and episodes you mark played or unplayed to Simkl, using the official Simkl plugin's login | 1.0.0.0 | [`plugins/simkl-mark-sync`](plugins/simkl-mark-sync) | GPL-3.0 |
| **English SDH Preference** | Opt-in: selects English SDH/HI/CC subtitles when present, otherwise full English; audio untouched | 1.0.0.1 | [`plugins/english-sdh`](plugins/english-sdh) | MIT |

Each folder has its own README (behaviour, settings, how to check it is active), its source, tests and license.

## Customizations

[`customizations`](customizations) holds the CSS and the Media Bar patch I use with the Jellyfish theme on
Jellyfin 12, with notes on provenance and how they were tested. They are not plugins and are not in the manifest.

## History

This repository combines four earlier repositories (`jellyfin-plugin-max-quality`, `jellyfin-plugin-simkl-mark-sync`,
`jellyfin-plugin-english-sdh` and `modules-repo`). Each was merged in with `git subtree`, so every earlier commit is
kept here under its folder.
