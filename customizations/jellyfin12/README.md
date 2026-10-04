# Jellyfin 12 customizations

Recovery/reference files for the customizations tested on Jellyfin Server 12.0.0 with Jellyfish/Jellyflix. Existing root `fix_v17.css` is intentionally unchanged.

## CSS: `local-overrides.css`

Contains two local additions, intended to follow `fix_v17.css`:

- A narrow-desktop detail-page fallback below 50em. Native mobile layout is not targeted by those rules.
- Position the native Jellyfin 12 user-menu button before the server brand using its stable `aria-controls="app-user-menu"` selector and the MUI toolbar structure.

The deployed server currently stores these rules directly in Branding → Custom CSS. This repository upload **does not change deployment**. Do not import this file while retaining duplicate pasted rules. If switching to a CDN import later, back up branding first, replace only the matching pasted rules, keep the existing theme imports, and use a pinned commit URL rather than a mutable branch URL.

The user-menu change is CSS visual ordering, not DOM reordering: keyboard tab order remains Jellyfin's original order. No positive tabindex or React DOM mutation is introduced. Upstream header structure changes may require revisiting the selector.

## Media Bar: `mediabar-homepage.patch`

Apply to IAmParadox27/jellyfin-plugin-media-bar at the exact `media-bar` commit in `source-refs.json`. This is a patch to the plugin's embedded frontend source, **not** a standalone JavaScript injector or branding stylesheet.

It:

- waits for the homepage container before initial slideshow setup;
- retains one slideshow container across React homepage unmount/remount;
- mounts that container in `homeTab` instead of below the viewport-sized React root;
- uses real layout space rather than offsetting homepage rows;
- adjusts the logo position for the tested theme;
- versions embedded JS/CSS URLs to avoid stale client assets.

The `65vh` height, `5vh` logo offset, and query suffix are local choices, not universal upstream defaults. Do not blindly apply this to a newer Media Bar release. Upstream MakD/Jellyfin-Media-Bar PR #107 already implements broader Jellyfin 12 layout support; prefer that when it reaches the plugin and passes the same checks.

## Source provenance

`source-refs.json` records the official upstream revisions used. File Transformation, Media Bar, and Plugin Pages were built from their `v12` branches. Custom Tabs was inspected at `main` (no v12 branch existed at that audit); its published 0.2.10.0 binary was retained, not rebuilt. Only Media Bar needed this local source patch.

Local source-built 3.0.0.0 plugins must not be mistaken for published release binaries with the same version. Compare source/build provenance when migrating to an official release.

## Reproduction / validation

1. Obtain the Media Bar repository at the pinned commit.
2. Apply `mediabar-homepage.patch` using `git apply --check` then `git apply`, or another unified-diff tool.
3. Run `node --check slideshowpure.js`.
4. Build `src/Jellyfin.Plugin.MediaBar/Jellyfin.Plugin.MediaBar.csproj` with .NET SDK 10: `dotnet build ... -c Release`.
5. Test first in an isolated Jellyfin 12 instance, without production media or integration credentials.
6. Before replacing a production DLL, stop Jellyfin and preserve the existing DLL, manifest, and plugin configurations. Restart and verify the actual browser-delivered versioned assets.

Observed checks on the installed customization:

- Builds: 0 warnings and 0 errors.
- Visible single homepage hero; muted trailer time advances; slide selection and pause/resume work.
- Details → Back restores exactly one hero; logos clear metadata; media rows start beneath the hero.
- 1366px desktop and 390px narrow viewport checked; no narrow horizontal overflow.
- User icon appears before brand, and its menu opens on desktop/narrow viewports.

These checks do not guarantee every layout, client, route variant, or future release. Branding/theme settings and media were preserved. Do not run destructive cleanup jobs as a generic validation step.

## Upstream reports

- Media Bar report with exact patch/evidence: https://github.com/IAmParadox27/jellyfin-plugin-media-bar/issues/181
- File Transformation v12 validation: https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/issues/81#issuecomment-5583352611
- Helper checksum correction verified: https://github.com/JellyPlugins/jellyfin-helper/issues/233#issuecomment-5583352896

No credentials, server database, user records, media, production backups, or compiled binaries are included here. Original Media Bar code is by MakD and plugin integration by IAmParadox27 and contributors; their repository licenses continue to apply. These modifications were AI-assisted and tested as described above.
