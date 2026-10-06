# Hydra Sync — Playnite extension

A Playnite 10 extension that synchronizes **playtime** and **achievements** between
[Hydra Launcher](https://github.com/hydralauncher/hydra) and Playnite — including feeding
achievement unlocks into the **Playnite Achievements** extension (justin-delano/PlayniteAchievements).

- Extension ID: `A76358E9-BFA2-4189-B6F3-2307EA4E217B`
- Type: Generic plugin · Target: Playnite 10.x (SDK 6.18) · .NET Framework 4.8
- Package: `dist/HydraSync-1.9.0.pext`

## What it does

| Feature | Details |
|---|---|
| Playtime sync | Reads Hydra's local LevelDB (`%APPDATA%\Hydra\hydra-db`) and applies a **replace-if-larger** rule to `Game.Playtime`: Hydra's cumulative total **replaces** Playnite's value only when it is strictly higher; otherwise Playnite's playtime is left untouched. Never adds on top, so nothing is double-counted. `LastActivity` moves forward from Hydra's last session (never backward). Includes an **Undo** that restores modified games to their pre-sync playtime. |
| Last activity | Moves `LastActivity` forward from Hydra's `lastTimePlayed` (never backward). |
| Achievement unlock sync | Reads achievement files on disk, the same file map Hydra itself uses. Works for **non-Steam Hydra games too** (shop=custom/launchbox): AppIDs are discovered from the game folder (`steam_appid.txt`, Goldberg `steam_settings\<id>\` dirs), and ID-independent game-dir formats are always scanned. |
| Achievement metadata | Real names/descriptions/icons from the game's local `steam_settings\achievements.json` definition file (bundled with Goldberg/GSE emulator files or Hydra-exported), an optional Steam Web API key for full schemas, or the public store API (legacy); cached 30 days in the plugin data dir. |
| Playnite Achievements hand-off | Writes per-game JSON to PA's `achievement_cache` folder inside its plugin data dir (`%APPDATA%\Playnite\ExtensionsData\e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b\achievement_cache\<game-guid>.json` — Playnite keys plugin data by the plugin's class GUID, not its manifest name; the folder is auto-detected with fallbacks). PA's legacy-cache importer picks these up **on the next Playnite start** and then deletes the file. Steam-game writes use ProviderKey `Steam` — PA shows a proper Steam source label (name + provider icon/color) while unlock state comes from Hydra; **non-Steam games use ProviderKey `Manual`** — that's PA's own manual-achievements provider key, so PA keeps its manual-tracking features available for those games instead of treating them as owned by another provider. Display in PA is provider-agnostic (data is read by game GUID), so unlocks show up regardless of how the game was added. |
| Matching | Strong: Playnite `GameId` == Hydra `objectId` (Steam AppID). Fallback: normalized title equality (case-insensitive, symbols stripped). |
| Auto-sync | **Off by default.** Enable it in the extension settings to sync automatically ~20 s after Playnite starts and then once per interval (default 15 min). Saving settings applies immediately — no restart needed. While disabled, syncing only runs on demand via **Sync now**. |
| Per-game sync | The game context menu syncs a single game (or a multi-selection) **immediately** — playtime, achievements, or both in one click, ignoring the global switches. Separate `Always sync: …` items store a per-game mode that narrower parts apply on their own. |
| Scope | **Sync existing games only** — Hydra-only games are not imported; unmatched Hydra entries are skipped. |
| Updates | Checks the repo's GitHub Releases ~2 minutes after Playnite starts (toggle) and offers a one-click **Download & install update** from the `@Hydra Sync` main menu — Playnite then runs its normal confirm + restart flow. |
| HowLongToBeat | Optional (off by default): after a sync raises a game's playtime, Hydra Sync asks the HowLongToBeat extension to submit the new total, the same call it makes when a game exits. Skips signed-out, ignored, and unlinked games. |

## Requirements

1. Playnite 10.x (tested against 10.62).
2. Hydra Launcher (any recent version — the plugin reads the local DB level-agnostically,
   so it tolerates Hydra schema changes; it just needs `playTimeInMilliseconds`, `objectId`,
   `shop` and `title` on game records).
3. Optional: **Playnite Achievements** extension (v4.x) for the achievement hand-off.
   Without it, playtime sync still works; achievement JSON writing is skipped silently.

## Install

**Option A — package:**
grab `HydraSync-1.9.0.pext` from this repo's **Releases** page (or build it yourself with
`scripts/package.sh`) and double-click it (Playnite must be installed; it registers the
`.pext` file association). Playnite verifies and installs the extension automatically.

**Option B — manual:**
extract the contents of the `HydraSync-1.9.0.pext` package (a plain zip) into

```
%APPDATA%\Playnite\Extensions\A76358E9-BFA2-4189-B6F3-2307EA4E217B\
```

so that `extension.yaml` sits directly in that folder. Restart Playnite.

> The package intentionally contains **no** `Playnite*.dll` (rejected by Playnite's
> package verifier). It ships a pinned `Newtonsoft.Json.dll` so the extension always
> binds to the version it was compiled against (extensions loaded from their own folder
> resolve their dependencies there first; Playnite's copy keeps serving Playnite itself).

## Settings

`Add-ons → Extensions settings → Generic → Hydra Sync`:

| Setting | Default | Meaning |
|---|---|---|
| Sync playtime | on | Master switch for playtime/activity updates. |
| Sync achievements | on | Master switch for achievement processing. |
| Write to Playnite Achievements | on | Write unlock JSON into PA's `achievement_cache`. |
| Fetch Steam schema (names/descriptions/icons) | on | Allow **network** schema lookups (only for games with unlocks; cached 30 days). Local definition files in the game folder are always used first, regardless of this toggle. |
| Steam Web API key (optional) | *(empty)* | Full achievement schemas (names, descriptions, icons) via `ISteamUserStats/GetSchemaForGame`. Get a key at `steamcommunity.com/dev/apikey`. When empty, metadata falls back to local definition files, then the public store API (which no longer returns achievements for newer games). |
| Auto-sync | **off** | Opt-in automatic syncing: one pass ~20 s after Playnite starts, then one every interval. While off, syncing only happens when you pick **Sync now**. Takes effect immediately when you save settings (no restart needed). |
| Sync interval (minutes) | 15 | Auto-sync timer period (1–1440); only used when auto-sync is enabled. |
| Hydra data directory | *(empty)* | Auto-detects `%APPDATA%\Hydra\hydra-db`, `%APPDATA%\hydralauncher\hydra-db` (older builds), `-staging` variants, or a direct-DB folder (accepted: the `hydra-db` folder itself or its parent). Set explicitly for portable Hydra installs. |
| Check for Hydra Sync updates at startup | on | Query GitHub Releases ~2 minutes after Playnite starts and notify when a newer version is available. |
| Push playtime to HowLongToBeat | **off** | After a sync raises a game's playtime, ask the [HowLongToBeat](https://github.com/Lacro59/playnite-howlongtobeat-plugin) extension to submit the new total — the same call it makes when a game exits. Needs HowLongToBeat installed and logged in. Only games the sync actually raised are pushed (max 25 per sync). |

**Sync now** and **Undo playtime changes…** are available from the extension's settings
panel; **Sync now** is also in the Playnite main menu (`@Hydra Sync`), and a game's context
menu (`Hydra Sync`) can sync that one game — see [Per-game sync](#per-game-sync). Automatic
syncing is **opt-in**: enable *Auto-sync* in the settings panel to also run a pass ~20 s after
Playnite starts and then once per configured interval.

**Diagnose achievement sync…** (game context menu) runs the achievement pipeline for that
one game and shows a report: Hydra match, AppIDs used, local definition files found,
whether a Steam Web API key is set, every achievement file found
(paths), unlocks parsed, whether a cache file is awaiting PA import, quarantine status,
the per-game sync mode, and PA folder availability. Use it whenever unlocks don't appear.

### HowLongToBeat

When *Push playtime to HowLongToBeat* is on, every game whose playtime the sync raised is
handed to the HowLongToBeat extension right after the sync notification, using the same
call HowLongToBeat makes itself when a game exits. It submits the playtime only — the game is
not marked as "Playing" — and every other option keeps HowLongToBeat's own defaults, so this
is equivalent to a normal game-exit update.

Details worth knowing:

- **Off by default.** It writes to a third-party account, so it stays opt-in.
- **Only raised games are pushed.** A game whose playtime Hydra did not raise is left alone;
  you get a notification reporting how many were updated, skipped, or failed.
- **Games are skipped, never guessed at.** If you are signed out of HowLongToBeat, the game
  carries its "ignore playtime sync" tag, or HowLongToBeat has no data linked for it, the push
  is skipped rather than creating an entry.
- **Capped at 25 games per sync**, because each push is a couple of HTTP round-trips.
- **HowLongToBeat must be installed**, and Hydra Sync talks to the copy Playnite has already
  loaded — no reference to HowLongToBeat.dll is shipped, so versions can't conflict. If the
  extension isn't available you get one notification explaining why and nothing else breaks.
- Two manual items sit on the game context menu: **Push playtime to HowLongToBeat now**
  (submits the current playtime for the selected games regardless of the setting) and
  **Diagnose HowLongToBeat playtime sync…** (reports whether the extension is reachable,
  logged in, has data for the game, and whether the game is excluded).

### Per-game sync

Right-click any game (or multi-select several) → `Hydra Sync`:

*Sync now (one-shot, runs immediately for the selected games):*

| Menu item | What it does |
|---|---|
| `Sync playtime now` | Reads Hydra for these games and updates playtime right away |
| `Sync achievements now` | Reads achievement files for these games and hands unlocks to Playnite Achievements right away |
| `Sync playtime & achievements now` | Both of the above in a single pass |

These one-shot actions work regardless of the global **Sync playtime** / **Sync
achievements** switches and regardless of any stored per-game mode — you asked for that
game, so it happens. They only touch the selected games, and a completion notification
reports what changed (or says when no Hydra match was found).

*Always sync (stored mode used by automatic and full-library passes):*

| Menu item | Stored mode |
|---|---|
| `Always sync: playtime and achievements` | Both (default; also clears an override) |
| `Always sync: playtime only` | Playtime only for this game |
| `Always sync: achievements only` | Achievements only for this game |

The active mode is labelled **`(current)`**. Stored modes are kept per game in
`sync_state.json` (they survive restarts) and apply to the automatic passes and to
`Sync now` from the main menu. The global switches remain master switches here: a stored
mode can only narrow what happens, never re-enable something switched off globally. The
main menu (`@Hydra Sync → Sync now`) still syncs the whole library.

**Undo playtime changes** (with confirmation): restores every game the plugin modified to
its original playtime — exact for games synced by v1.2+, recovered approximately for
games synced by the older additive builds (their recorded bookkeeping is subtracted).
Achievements are never touched, and the playtime bookkeeping is cleared so the next sync
re-applies the replace-if-larger rule from a clean slate.

## Updates

Hydra Sync checks this repo's **GitHub Releases** about two minutes after Playnite starts
(toggle: *Check for Hydra Sync updates at startup*). When a newer version exists you get a
notification, and **Main menu → @Hydra Sync → Download & install update (vX.Y.Z)…**
downloads the `.pext` and hands it to Playnite, which shows its usual
"update from X to Y?" confirmation followed by the restart prompt.

> **Official Add-ons catalog status:** Playnite's *Add-ons → Browse* / *Updates* listing
> only covers add-ons submitted to
> [JosefNemec/PlayniteAddonDatabase](https://github.com/JosefNemec/PlayniteAddonDatabase),
> and that repository currently has **new plugin submissions on hold** while the database
> is being rebuilt for Playnite 11. This repo is prepared for submission:
> `InstallerManifest.yaml` (kept current by CI on every release) and
> `PlayniteAddonDatabase-HydraSync.yaml` (the ready-to-file catalog entry). Until the hold
> lifts, the in-extension updater above is the update mechanism.

## How it works

1. **Read** — open Hydra's LevelDB read-only (`LevelDb.Managed`, pure managed incl. snappy).
   The DB folder is auto-detected: `%APPDATA%\Hydra\hydra-db`, older
   `%APPDATA%\hydralauncher\hydra-db`, their `-staging` variants, and a direct-DB layout
   (the `CURRENT` file at the folder root). If the DB is locked (Hydra running),
   snapshot-copy the folder to temp (excluding `LOCK`) and read the copy; retried up to 3×.
2. **Match** — build an index of Playnite games; strong match on AppID, weak match on
   normalized title (Steam-source games preferred, then most recent activity).
3. **Playtime** — per Hydra entry: `Playtime = max(Playtime, Hydra total in seconds)`.
   Hydra's value is used **only** when it exceeds Playnite's current playtime; otherwise
   nothing changes. Before the first modification of each game the plugin records the
   game's current playtime (`OriginalPlaytimeSecs` in state) so Undo can restore it.
   State lives in
   `sync_state.json` in the plugin's data folder:
   `%APPDATA%\Playnite\ExtensionsData\A76358E9-BFA2-4189-B6F3-2307EA4E217B\`.
4. **Achievements** — for matched games: locate achievement files on disk (Steam-AppID
   static paths when an AppID is known, plus ID-independent game-directory formats
   always) → parse (exact port of Hydra's parsers, incl. INI case-sensitive quirks and
   per-format timestamp formats) → discover AppIDs from the game folder for non-Steam
   entries (`steam_appid.txt`, `steam_settings\<digits>\`) → build real achievement
   definitions (names/descriptions/icons) in schema order: **local** `steam_settings\achievements.json`
   first (bundled with the game files or Hydra-exported; never read from digit subdirs, those are unlock
   state) → optional **Steam Web API key** lookup → public store API as legacy fallback →
   unlock-only entries fall back to prettified API names → fingerprint
   (SHA-256 of sorted `name|unlocktime`, namespaced by provider key) → if changed, write
   `achievement_cache\<Playnite-game-GUID>.json` with `ProviderKey: "Steam"` (Steam) or
   `"Manual"` (non-Steam).
5. **PA import** — Playnite Achievements imports these files on startup
   (`LegacyJsonCacheImporter`) and deletes them. A one-time notification reminds you to
   restart Playnite after the first achievement sync.
6. **HowLongToBeat (optional)** — the ids of the games whose playtime was raised are recorded
   in the run summary; when the setting is on they are pushed to the loaded HowLongToBeat
   extension after the sync notification (max 25 per sync), one game at a time.

Game DB updates are marshalled to Playnite's UI thread; syncs run off-thread with a
re-entrancy guard.

## Building from source

Requires the .NET SDK (any recent 8/9/10). The plugin targets `net48` via reference
assemblies, so it cross-compiles on macOS/Linux:

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"   # macOS brew dotnet
export PATH="$DOTNET_ROOT/bin:$PATH"
dotnet build src/HydraSync/HydraSync.csproj -v q
dotnet run --project tests/hydrasync-tests.csproj   # test harness
./scripts/package.sh        # → dist/HydraSync-<version>.pext
```

The `.pext` is a plain zip, so the output of `scripts/package.sh` can be installed by
extracting it (see [Install](#install)). Published releases are built by CI from a pushed tag,
so building locally is only needed to test changes or to package a specific version yourself.

Layout:

```
src/HydraSync/
  HydraSyncPlugin.cs        # plugin entry: lifecycle, menus, timer, notifications
  PluginSettings.cs         # ISettings model
  SettingsView.cs           # code-only WPF settings view
  extension.yaml            # manifest
  Hydra/                    # LevelDB reader, game model, achievement file locator + parsers
  Achievements/             # Steam schema client, PA cache writer
  Integrations/             # late-bound bridge to the HowLongToBeat extension
  Sync/                     # sync engine + persisted state
  Update/                   # GitHub release update checker
tests/                      # net10 test harness + LevelDB fixture (run in CI before release)
```

Unit/integration checks for the Hydra layer (LevelDB fixture + every achievement parser
format + game-dir discovery + version/update logic + the HowLongToBeat bridge) live in `tests/`
and run in CI before each release. The fixture DB is committed and was produced with Node
`classic-level` to mirror Hydra's real on-disk encoding; regenerate it with
`cd tests/fixtures && npm i && node make-fixture.js`.

## Troubleshooting

Start with the log: `%APPDATA%\Playnite\logs\Playnite.log`, search for `HydraSync`. Warnings
and errors explain where each game stopped. Two in-app reports cover most cases:

- `Hydra Sync → Diagnose achievement sync…` (per game) — match, AppIDs, definition files,
  achievement files found, unlocks parsed, pending PA import, quarantine, per-game sync mode.
- `Hydra Sync → Diagnose HowLongToBeat playtime sync…` (per game) — whether the extension is
  reachable, logged in, has data for the game, and whether the game is excluded.

### Playtime isn't syncing

| Symptom | Cause / fix |
|---|---|
| "Hydra database not found at …" | Wrong folder. Hydra Sync auto-detects `%APPDATA%\Hydra\hydra-db`, older `%APPDATA%\hydralauncher\hydra-db`, `-staging` variants and direct-DB layouts; if yours is elsewhere, set **Hydra data directory**. |
| Game's playtime unchanged | Playtime is only replaced when **Hydra's total is higher**. If Playnite's value is already larger, the game is left alone by design. |
| Playtime doubled or wrong after an old version | Older builds added to the existing value. **Undo playtime changes…** (settings panel) restores the pre-sync values, then the next sync applies the replace rule. |
| Wrong game matched | Matching is by AppID first, then normalized title. Only games already in your library are synced — Hydra-only titles are never added. |
| Nothing happens on its own | Automatic syncing is **off by default**. Enable **Auto-sync**; it takes effect when you save. |
| Hydra is running / game folders on a network drive | Expected: Hydra locks its database, so a temp snapshot copy is read instead. No action needed. |

### Achievements aren't showing up

Playnite itself has no achievement display — unlocks appear in **Playnite Achievements**, and
its importer only runs at Playnite startup. A sync always needs **one Playnite restart** before
new unlocks show up.

Right-click the game → `Hydra Sync → Diagnose achievement sync…` and read the report:

| Report says | Meaning / fix |
|---|---|
| `Hydra match: NONE` | The game isn't matched to a Hydra entry. Playtime won't sync either. |
| `Achievement files found: 0` | No achievement file was located on disk (including Steam cache files). Verify Hydra itself shows achievements for this game. |
| `files found: N` but `Unlocks parsed: 0` | Files exist but contain no unlocked achievements (or an unsupported variant) — open the listed paths manually. |
| `Cache file awaiting PA import: YES` | The file was written; restart Playnite to let Playnite Achievements import it. |
| `PA QUARANTINE` | Playnite Achievements' parser rejected our file — please report this with the file from `achievement_cache_quarantine`. |
| `PA plugin dir … NOT FOUND` | Playnite Achievements isn't installed (or never started) — exports are skipped with an error notification. |
| `Fingerprint recorded: yes` | Unlocks unchanged since the last write — restart Playnite to trigger the import. |

Also check `Add-ons → Extensions settings → Generic → Hydra Sync`: **Sync achievements** and
**Write to Playnite Achievements** must be on. Every sync notification includes scan stats
(`scanned …, files found for …, with unlocks in …`) — if those stay at 0, the problem is file
discovery (the first two rows above).

### Only some games are syncing

Per-game modes narrow what syncs for individual games, and the global switches stay master
switches (see [Per-game sync](#per-game-sync)). Right-click a game → `Hydra Sync` →
`Always sync: playtime and achievements` clears the override; the sync notification reports
how many games were skipped and why. The `Sync playtime now` / `Sync achievements now` /
`Sync playtime & achievements now` items act on the selected games only and ignore both the
switches and the stored mode.

### HowLongToBeat isn't updating

| Symptom | Cause / fix |
|---|---|
| No HowLongToBeat notification after a sync | **Push playtime to HowLongToBeat** is off by default — enable it in settings. |
| "HowLongToBeat is not available …" | The extension isn't installed or not loaded yet. Install it, then use **Push playtime to HowLongToBeat now** or re-sync; Hydra Sync doesn't ship or require its DLL. |
| "updated 0, skipped N" | Expected for signed-out accounts, games carrying HowLongToBeat's ignore tag, and games it has no data for. `Diagnose HowLongToBeat playtime sync…` says which. |
| No push after a sync | Only games whose playtime the sync actually **raised** are pushed — that's intentional, so nothing is submitted needlessly. |
| A very large sync pushed only some games | Capped at 25 games per sync; the notification says so. Sync again to continue. |

### Errors and odd behaviour elsewhere

- **Bad path in settings** — invalid folders produce an error notification, never a crash.
- **Notifications stop appearing** — Playnite collapses repeated notifications under one icon;
  click it to expand.
- **Menu items look stale** — restart Playnite after updating the extension.

## Known limitations

- Playtime from Hydra games that can't be matched to an existing Playnite game is not
  imported (sync-existing-only design). Non-Steam games match by normalized title.
- Replace-if-larger uses Hydra's cumulative total: if a game's Hydra total exceeds
  Playnite's value, Playnite's value is replaced outright — sessions played **only**
  outside Hydra are then not counted (Hydra's total doesn't contain them). This is the
  requested behavior: Hydra wins whenever it is higher.
- Undo for games first synced by the pre-1.2 additive builds recovers the original value
  approximately (it subtracts the recorded cumulative added amount; breaks only if the
  first sync had historical-import disabled or Hydra's playtime was reset mid-life).
- For non-Steam games without any discoverable AppID or game-dir achievement file,
  nothing is written (there's nothing to read). Achievement names fall back to
  prettified API names when no schema source works (local definition files absent, no
  Web API key, and the store API returns no achievement data for the app); icons are
  omitted in that case. Providing a Steam Web API key fixes metadata for any Steam app.
- PA's importer runs at startup only — new unlocks appear in Playnite Achievements after
  a Playnite restart (or whenever PA re-runs its importer/cache clear).
- v1 always overwrites PA's cached data for a matched game when unlocks change (PA keeps
  no per-provider merge in the legacy cache path).
