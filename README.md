# Hydra Sync — Playnite extension

A Playnite 10 extension that synchronizes **playtime** and **achievements** between
[Hydra Launcher](https://github.com/hydralauncher/hydra) and Playnite — including feeding
achievement unlocks into the **Playnite Achievements** extension (justin-delano/PlayniteAchievements).

- Extension ID: `A76358E9-BFA2-4189-B6F3-2307EA4E217B`
- Type: Generic plugin · Target: Playnite 10.x (SDK 6.18) · .NET Framework 4.8
- Package: `dist/HydraSync-1.4.0.pext`

## What it does

| Feature | Details |
|---|---|
| Playtime sync | Reads Hydra's local LevelDB (`%APPDATA%\Hydra\hydra-db`) and applies a **replace-if-larger** rule to `Game.Playtime`: Hydra's cumulative total **replaces** Playnite's value only when it is strictly higher; otherwise Playnite's playtime is left untouched. Never adds on top, so nothing is double-counted. `LastActivity` moves forward from Hydra's last session (never backward). Includes an **Undo** that restores modified games to their pre-sync playtime. |
| Last activity | Moves `LastActivity` forward from Hydra's `lastTimePlayed` (never backward). |
| Achievement unlock sync | Reads cracker/emulator achievement files on disk (CODEX, RUNE, OnlineFix, Goldberg/GSE, RLD!, EMPRESS, Skidrow, CreamAPI, SmartSteamEmu, razor1911, userstats, 3DM, ali213, Steam userdata cache) — the same file map Hydra itself uses. Works for **non-Steam Hydra games too** (shop=custom/launchbox): AppIDs are discovered from the game folder (`steam_appid.txt`, Goldberg `steam_settings\<id>\` dirs), and ID-independent game-dir formats are always scanned. |
| Achievement metadata | Real names/descriptions/icons from the game's local `steam_settings\achievements.json` definition file (shipped with Goldberg/GSE cracks or Hydra-exported), an optional Steam Web API key for full schemas, or the public store API (legacy); cached 30 days in the plugin data dir. |
| Playnite Achievements hand-off | Writes per-game JSON to PA's `achievement_cache` folder inside its plugin data dir (`%APPDATA%\Playnite\ExtensionsData\e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b\achievement_cache\<game-guid>.json` — Playnite keys plugin data by the plugin's class GUID, not its manifest name; the folder is auto-detected with fallbacks). PA's legacy-cache importer picks these up **on the next Playnite start** and then deletes the file. Steam-game writes use ProviderKey `Steam` — PA shows a proper Steam source label (name + provider icon/color) while unlock state comes from Hydra; **non-Steam games use ProviderKey `Manual`** — that's PA's own manual-achievements provider key, so PA keeps its manual-tracking features available for those games instead of treating them as owned by another provider. Display in PA is provider-agnostic (data is read by game GUID), so unlocks show up regardless of how the game was added. |
| Matching | Strong: Playnite `GameId` == Hydra `objectId` (Steam AppID). Fallback: normalized title equality (case-insensitive, symbols stripped). |
| Scope | **Sync existing games only** — Hydra-only games are not imported; unmatched Hydra entries are skipped. |

## Requirements

1. Playnite 10.x (tested against 10.62).
2. Hydra Launcher (any recent version — the plugin reads the local DB level-agnostically,
   so it tolerates Hydra schema changes; it just needs `playTimeInMilliseconds`, `objectId`,
   `shop` and `title` on game records).
3. Optional: **Playnite Achievements** extension (v4.x) for the achievement hand-off.
   Without it, playtime sync still works; achievement JSON writing is skipped silently.

## Install

**Option A — package:**
grab `HydraSync-1.4.0.pext` from this repo's **Releases** page (or build it yourself with
`scripts/package.sh`) and double-click it (Playnite must be installed; it registers the
`.pext` file association). Playnite verifies and installs the extension automatically.

**Option B — manual:**
extract the contents of the `HydraSync-1.4.0.pext` package (a plain zip) into

```
%APPDATA%\Playnite\Extensions\A76358E9-BFA2-4189-B6F3-2307EA4E217B\
```

so that `extension.yaml` sits directly in that folder. Restart Playnite.

> The package intentionally contains **no** `Playnite*.dll` (rejected by Playnite's
> package verifier). It ships a pinned `Newtonsoft.Json.dll` so the extension always
> binds to the version it was compiled against (extensions loaded from their own folder
> resolve their dependencies there first; Playnite's copy keeps serving Playnite itself).

## Settings

`Settings → Extensions → Hydra Sync`:

| Setting | Default | Meaning |
|---|---|---|
| Sync playtime | on | Master switch for playtime/activity updates. |
| Sync achievements | on | Master switch for achievement processing. |
| Write to Playnite Achievements | on | Write unlock JSON into PA's `achievement_cache`. |
| Fetch Steam schema (names/descriptions/icons) | on | Allow **network** schema lookups (only for games with unlocks; cached 30 days). Local definition files in the game folder are always used first, regardless of this toggle. |
| Steam Web API key (optional) | *(empty)* | Full achievement schemas (names, descriptions, icons) via `ISteamUserStats/GetSchemaForGame`. Get a key at `steamcommunity.com/dev/apikey`. When empty, metadata falls back to local definition files, then the public store API (which no longer returns achievements for newer games). |
| Sync interval (minutes) | 15 | Background timer period (1–1440). |
| Hydra data directory | *(empty)* | Auto-detects `%APPDATA%\Hydra\hydra-db`, `%APPDATA%\hydralauncher\hydra-db` (older builds), `-staging` variants, or a direct-DB folder (accepted: the `hydra-db` folder itself or its parent). Set explicitly for portable Hydra installs. |

**Sync now** and **Undo playtime changes…** are available from the extension's settings
panel; **Sync now** is also in the Playnite main menu (`@Hydra Sync`) and on a game's
context menu (`Hydra Sync → Sync Hydra playtime & achievements`). The background timer
also runs 20 s after Playnite starts, then every interval.

**Diagnose achievement sync…** (game context menu) runs the achievement pipeline for that
one game and shows a report: Hydra match, AppIDs used, local definition files found,
whether a Steam Web API key is set, every achievement file found
(paths), unlocks parsed, whether a cache file is awaiting PA import, quarantine status,
and PA folder availability. Use it whenever unlocks don't appear.

**Undo playtime changes** (with confirmation): restores every game the plugin modified to
its original playtime — exact for games synced by v1.2+, recovered approximately for
games synced by the older additive builds (their recorded bookkeeping is subtracted).
Achievements are never touched, and the playtime bookkeeping is cleared so the next sync
re-applies the replace-if-larger rule from a clean slate.

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
4. **Achievements** — for matched games: locate cracker achievement files (Steam-AppID
   static paths when an AppID is known, plus ID-independent game-directory formats
   always) → parse (exact port of Hydra's parsers, incl. INI case-sensitive quirks and
   per-cracker timestamp formats) → discover AppIDs from the game folder for non-Steam
   entries (`steam_appid.txt`, `steam_settings\<digits>\`) → build real achievement
   definitions (names/descriptions/icons) in schema order: **local** `steam_settings\achievements.json`
   first (crack-shipped or Hydra-exported; never read from digit subdirs, those are unlock
   state) → optional **Steam Web API key** lookup → public store API as legacy fallback →
   unlock-only entries fall back to prettified API names → fingerprint
   (SHA-256 of sorted `name|unlocktime`, namespaced by provider key) → if changed, write
   `achievement_cache\<Playnite-game-GUID>.json` with `ProviderKey: "Steam"` (Steam) or
   `"Manual"` (non-Steam).
5. **PA import** — Playnite Achievements imports these files on startup
   (`LegacyJsonCacheImporter`) and deletes them. A one-time notification reminds you to
   restart Playnite after the first achievement sync.

Game DB updates are marshalled to Playnite's UI thread; syncs run off-thread with a
re-entrancy guard.

## Building from source

Requires the .NET SDK (any recent 8/9/10). The plugin targets `net48` via reference
assemblies, so it cross-compiles on macOS/Linux:

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"   # macOS brew dotnet
export PATH="$DOTNET_ROOT/bin:$PATH"
dotnet build src/HydraSync/HydraSync.csproj -v q
./scripts/package.sh        # → dist/HydraSync-<version>.pext
```

Layout:

```
src/HydraSync/
  HydraSyncPlugin.cs        # plugin entry: lifecycle, menus, timer, notifications
  PluginSettings.cs         # ISettings model
  SettingsView.cs           # code-only WPF settings view
  extension.yaml            # manifest
  Hydra/                    # LevelDB reader, game model, achievement file locator + parsers
  Achievements/             # Steam schema client, PA cache writer
  Sync/                     # sync engine + persisted state
```

Unit/integration checks for the Hydra layer (LevelDB fixture + every achievement parser
format + game-dir discovery) live in the dev harness used during development; the fixture
DB is produced with Node `classic-level` to mirror Hydra's real on-disk encoding.

## Troubleshooting achievements

Right-click the game → `Hydra Sync → Diagnose achievement sync…` and check the report:

| Report says | Meaning / fix |
|---|---|
| `Hydra match: NONE` | The game isn't matched to a Hydra entry (title/GameId differ). Playtime won't sync either for this game. |
| `Achievement files found: 0` | No cracker/emulator/Steam cache file was located. Verify Hydra itself shows achievements for this game. For real-Steam games the `userdata\<user>\config\librarycache\<appid>.json` file must exist. |
| `files found: N` but `Unlocks parsed: 0` | Files exist but contain no unlocked achievements (or an unsupported variant) — check the paths listed, open them manually. |
| `Cache file awaiting PA import: YES` | We wrote the file; Playnite Achievements imports it **when Playnite next starts** — restart Playnite. |
| `PA QUARANTINE` | PA's parser rejected our file — please report this with the file from `achievement_cache_quarantine`. |
| `PA plugin dir … NOT FOUND` | Playnite Achievements isn't installed (or never started) — the sync skips exports and shows an error notification. |
| `Fingerprint recorded: yes` | Unlocks unchanged since the last write — PA should already have them; if not, restart Playnite to trigger the import. |

Also check `Settings → Extensions → Hydra Sync`: **Sync achievements** and
**Write to Playnite Achievements** must be on. Every sync's notification now includes
scan stats (`scanned …, files found for …, with unlocks in …`) — if those stay at 0,
the issue is file discovery (first two rows above). Detailed per-game reasons are logged
at Debug level in `%APPDATA%\Playnite\logs\Playnite.log` (search `HydraSync`).

## Windows test checklist

1. **Load** — install the `.pext`, start Playnite. Check
   `Settings → Extensions` shows *Hydra Sync* (v1.4.0) with no error banner, and
   `%APPDATA%\Playnite\logs\Playnite.log` contains no `HydraSync` errors
   (search for `HydraSync`).
2. **Settings** — open the extension's settings; verify all toggles/fields render and
   edits persist after OK + reopening, and the **Undo playtime changes…** button is present.
3. **Manual sync** — main menu `@Hydra Sync → Sync now`. Expect a completion notification
   (`Hydra Sync: … matched …, raised playtime on N game(s) (+X min), …`) or a "Hydra
   database not found" notification when Hydra has never run. Verify detection works with
   the DB in **either** `%APPDATA%\Hydra\hydra-db` **or** `%APPDATA%\hydralauncher\hydra-db`
   (older Hydra installs) — no need to set the data folder in settings.
4. **Playtime replace-if-larger (Hydra closed)** — pick a game where Hydra's total is
   larger than Playnite's: note both values, sync → Playnite's value must **equal Hydra's
   total exactly** (replaced, not added). Sync again → unchanged (idempotent). Pick a
   game where Playnite's value is already larger → sync → untouched.
5. **Undo** — after raising several games, open settings → **Undo playtime changes…** →
   confirm. Every modified game must return to its pre-sync playtime (games previously
   synced by the old additive build are restored approximately and labelled as such in
   the notification). Sync again afterwards → the replace rule re-applies cleanly.
6. **Playtime (Hydra running)** — repeat while Hydra is open (exercises the snapshot-copy
   fallback). Play something in Hydra, wait for its playtime counter to tick, sync, verify
   Hydra's new total lands in Playnite.
7. **Achievements** — for a Steam game cracked with a supported cracker, ensure an
   achievement file exists (e.g. Goldberg:
   `%APPDATA%\Goldberg SteamEmu Saves\<appid>\achievements.json`). Sync → verify
   `%APPDATA%\Playnite\ExtensionsData\e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b\achievement_cache\<guid>.json`
   appears (then disappears after PA import), get the "restart Playnite" notification,
   restart, and confirm unlocks show in Playnite Achievements (provider *Hydra*).
8. **Idempotence** — sync twice more: the cache JSON must not be rewritten when nothing
   changed (fingerprint match).
9. **Non-Steam game** — a non-Steam game matched to Playnite by title with cracker
   achievement files in its folder (e.g. `SteamData\user_stats.ini`, or
   `steam_settings\<appid>\achievements.json` + `steam_appid.txt`): sync → cache JSON
   written with `"ProviderKey": "Manual"` → restart → unlocks visible in PA, **and**
   PA's manual-achievement editing features for that game are still offered (an "Manual"
   key does not lock the game as owned by another provider).
10. **No PA installed** — with PA absent, achievements sync should still run without
    errors and only report "PA unavailable" internally; playtime unaffected.
11. **Menu/notifications** — game context menu item works; error paths (bad Hydra dir in
    settings) produce an error notification instead of a crash.

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
