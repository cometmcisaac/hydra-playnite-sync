Playnite extension that syncs **playtime** and **achievements** between
[Hydra Launcher](https://github.com/hydralauncher/hydra) and Playnite — including
handing achievement unlocks to the
[Playnite Achievements](https://github.com/justin-delano/PlayniteAchievements) extension.

## Install

1. Download `HydraSync-<version>.pext` from the assets below.
2. Double-click it (Playnite registers the `.pext` file type) — or extract it into
   `%APPDATA%\Playnite\Extensions\A76358E9-BFA2-4189-B6F3-2307EA4E217B\`.
3. Restart Playnite.
4. Settings: **Add-ons → Extensions settings → Generic → Hydra Sync**.

## Highlights

- **Playtime** — Hydra's total replaces Playnite's value only when it is larger:
  never added on top, so nothing is double-counted. Includes an
  **Undo playtime changes** button that restores your pre-sync values.
- **Achievements** — reads achievement files on disk (the same locations Hydra uses),
  then enriches them with Steam-sourced metadata: local `steam_settings` definitions
  first, an optional Steam Web API key for full schemas, store API as a last resort.
  Unlocks are handed to Playnite Achievements under provider **Steam**
  (or **Manual** for non-Steam entries).
- **Auto-detects the Hydra database** (`%APPDATA%\Hydra` and `%APPDATA%\hydralauncher`).
- **Built-in update checks** — Hydra Sync checks GitHub Releases a couple of minutes after
  startup (toggleable) and offers a one-click **Download & install update** from the
  `@Hydra Sync` main menu; Playnite then runs its normal confirm-and-restart flow.
- **Opt-in auto-sync** — off by default; enable it to sync once shortly after Playnite
  starts and then on a configurable interval. Changes apply immediately, no restart needed.
- Main-menu **Sync now** and a per-game **Diagnose achievement sync…** tool for
  troubleshooting.

See `README.md` for full documentation, settings reference, and the test checklist.

> **Note:** Playnite itself has no built-in achievement display — after the first
> achievement sync, restart Playnite once so Playnite Achievements can import the data.
