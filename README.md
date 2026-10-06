# Hydra Sync — Playnite extension

Brings **playtime** and **achievement** tracking from
[Hydra Launcher](https://github.com/hydralauncher/hydra) into
[Playnite](https://playnite.link) — including handing achievement unlocks to the
[Playnite Achievements](https://github.com/justin-delano/PlayniteAchievements) extension.

Playnite 10 · Windows · [Releases](https://github.com/cometmcisaac/hydra-playnite-sync/releases)

## Requirements

- Playnite 10.x (tested against 10.62).
- Hydra Launcher. Any recent version works — Hydra Sync only reads Hydra's local data, so it
  keeps working across Hydra updates.
- Optional: **Playnite Achievements** 4.x, if you want achievements to show up somewhere.
  Without it playtime sync still works and nothing else breaks.
- Optional: **[HowLongToBeat](https://github.com/Lacro59/playnite-howlongtobeat-plugin)**, if
  you want the playtime a sync raises submitted to your HowLongToBeat account as well. It has to
  be installed and signed in; Hydra Sync never ships or requires it, and nothing else changes if
  it isn't there. See [HowLongToBeat](#howlongtobeat).

## Install

1. Download the latest `HydraSync-<version>.pext` from the
   [Releases page](https://github.com/cometmcisaac/hydra-playnite-sync/releases).
2. Double-click it and confirm in Playnite — Playnite installs the extension itself.
   - Or extract it (it is a plain zip) into `%APPDATA%\Playnite\Extensions\HydraSync\` so
     that `extension.yaml` sits directly inside that folder.
3. Restart Playnite if it was already running.

Settings live under **Add-ons → Extensions settings → Generic → Hydra Sync**.

## What it does

| Feature | Details |
|---|---|
| Playtime | Hydra's cumulative total **replaces** Playnite's playtime when — and only when — Hydra's is higher. Nothing is ever added on top, so sessions can't be double-counted. **Undo playtime changes…** puts everything back. |
| Last played | Moves the game's *Last activity* forward to Hydra's last session, never backward. |
| Achievements | Reads the achievement files already sitting on your disk — the same files Hydra reads — so unlocks also work for non-Steam Hydra games. |
| Achievement names and icons | Real names, descriptions and icons, resolved from the definition file in the game folder, an optional Steam Web API key, or the public store API (older games only). Cached for 30 days. |
| Playnite Achievements hand-off | Unlocks are written where Playnite Achievements picks them up on its next start. Steam games are labelled **Steam** (proper source label and icon) while other-store games are labelled **Manual**, so Playnite Achievements keeps its manual-tracking features available for them. |
| Matching | By Steam AppID first, then by title (case-insensitive, symbols ignored). |
| Automatic syncing | **Off by default.** Turn it on in the settings to sync shortly after Playnite starts and then on an interval you choose (every 15 minutes by default). Changes apply as soon as you save. |
| Per-game control | Right-click any game (or a multi-selection) to sync just that game's playtime, achievements, or both immediately — or store a per-game mode that narrower passes apply on their own. See [Per-game sync](#per-game-sync). |
| Scope | **Existing games only.** Hydra titles that aren't in your Playnite library are never added. |
| Updates | Checks GitHub Releases shortly after Playnite starts and offers a one-click **Download & install update** from the `@Hydra Sync` main menu — Playnite then runs its usual confirm and restart flow. See [Updates](#updates). |
| HowLongToBeat | Optional (off by default): after a sync raises a game's playtime, Hydra Sync asks HowLongToBeat to submit the new total — exactly what HowLongToBeat does when a game exits. See [HowLongToBeat](#howlongtobeat). |

## Settings

`Add-ons → Extensions settings → Generic → Hydra Sync`

| Setting | Default | What it does |
|---|---|---|
| Sync playtime | on | Master switch for playtime and last-activity updates. |
| Sync achievements | on | Master switch for achievement syncing. |
| Write to Playnite Achievements | on | Write unlocks where Playnite Achievements will import them. |
| Fetch Steam schema | on | Allow online lookups for achievement names, descriptions and icons (cached 30 days). Definition files in the game folder are always preferred. |
| Steam Web API key | *(empty)* | Optional key from `steamcommunity.com/dev/apikey`. Gets full achievement data for newer games, where the public store API no longer returns it. |
| Auto-sync | **off** | Sync automatically shortly after Playnite starts and then on the interval below. While off, syncing only happens when you ask for it. |
| Sync interval (minutes) | 15 | How often automatic syncing runs (1–1440). Only used when auto-sync is on. |
| Hydra data directory | *(empty)* | Leave empty unless Hydra Sync can't find Hydra's database. It detects `%APPDATA%\Hydra\hydra-db` and the older `%APPDATA%\hydralauncher\hydra-db` on its own, plus `-staging` variants and portable layouts. |
| Check for Hydra Sync updates at startup | on | Look for a newer release shortly after Playnite starts and notify you. |
| Push playtime to HowLongToBeat | **off** | After a sync raises a game's playtime, ask HowLongToBeat to submit the new total. Needs HowLongToBeat installed and signed in. Only games the sync actually raised are pushed (max 25 per sync). |

Where the actions live:

- **Sync now** — settings panel, and Playnite's main menu under `@Hydra Sync` (syncs your whole
  library).
- **Undo playtime changes…** — settings panel, with a confirmation. Restores every game the
  extension modified to the playtime it had before, clears the bookkeeping so the next sync
  starts fresh, and leaves achievements alone.
- **Diagnose achievement sync…** — game context menu. Runs the achievement pipeline for one game
  and shows what it found: the Hydra match, the files it read, the unlocks it parsed, whether
  anything is waiting for a Playnite restart, and whether Playnite Achievements is reachable.
  This is the first place to look when unlocks don't appear.
- **Diagnose HowLongToBeat playtime sync…** — game context menu. Reports whether HowLongToBeat
  is reachable, signed in, has data for the game, and whether the game is excluded.

### Per-game sync

Right-click a game (or select several) → `Hydra Sync`:

*Sync it now — runs immediately for the selected games:*

| Menu item | What it does |
|---|---|
| `Sync playtime now` | Reads Hydra for these games and updates playtime right away |
| `Sync achievements now` | Reads their achievement files and hands unlocks to Playnite Achievements right away |
| `Sync playtime & achievements now` | Both of the above in a single pass |

These run whether or not the global switches are on and whatever mode is stored for the game —
you asked for that game, so it happens. They only touch the selected games, and a notification
reports what changed (or says when no Hydra match was found).

*Always sync — remember a mode for these games:*

| Menu item | Stored mode |
|---|---|
| `Always sync: playtime and achievements` | Both (the default; also clears an override) |
| `Always sync: playtime only` | Playtime only for this game |
| `Always sync: achievements only` | Achievements only for this game |

The active mode is marked **(current)**. Modes are remembered per game across restarts and apply
to automatic passes and to **Sync now** from the main menu. The global switches stay master
switches here: a stored mode can narrow what happens, but never switch something back on.

### HowLongToBeat

*Push playtime to HowLongToBeat* is **off by default**, because it writes to a third-party
account.

- **Only raised games are pushed**, so nothing is submitted needlessly. A notification reports
  how many were updated, skipped, or failed.
- **Games are skipped, never guessed at.** Signed-out accounts, games carrying HowLongToBeat's
  "ignore playtime sync" tag, and games HowLongToBeat has no data for are all left alone.
- **Playtime only.** The game is not marked as "Playing" and every other option is left at
  HowLongToBeat's own defaults, which makes this equivalent to a normal game-exit update.
- **Capped at 25 games per sync**, since each push is a couple of network round-trips. Sync
  again to continue with the rest.
- **No HowLongToBeat files are needed.** If the extension isn't installed or loaded yet you get
  one notification explaining why, and nothing else breaks.
- Two manual items sit on the game context menu: **Push playtime to HowLongToBeat now**
  (submits the selected games regardless of the setting) and **Diagnose HowLongToBeat playtime
  sync…**.

## Updates

Shortly after Playnite starts, Hydra Sync checks this repo's GitHub Releases (toggle: *Check for
Hydra Sync updates at startup*). If a newer version exists you get a notification, and
**Main menu → @Hydra Sync → Download & install update (vX.Y.Z)…** downloads it and hands it to
Playnite, which asks you to confirm the update and then restarts.

> Playnite's own *Add-ons → Browse* / *Updates* list only covers add-ons submitted to
> [JosefNemec/PlayniteAddonDatabase](https://github.com/JosefNemec/PlayniteAddonDatabase), which
> currently has new plugin submissions on hold while the database is rebuilt. The updater above
> is how updates reach you in the meantime.

## How it works

1. **Read** — Hydra's database is opened read-only. If Hydra is running and holding it locked, a
   temporary snapshot is read instead, so you can sync whenever you like. The folder is detected
   automatically; only an unusual or portable install needs **Hydra data directory** set.
2. **Match** — your Playnite games are indexed and matched by AppID, falling back to title.
3. **Playtime** — Hydra's total only replaces a game's playtime when it is higher, so Hydra wins
   but never inflates the number. The pre-sync value is remembered so **Undo** can restore it
   exactly.
4. **Achievements** — for each matched game, Hydra Sync looks for achievement files on disk (both
   the fixed per-store locations and the formats that live inside the game folder), parses them,
   resolves the real achievement names, descriptions and icons, and only rewrites what actually
   changed.
5. **Hand-off** — the result is written where Playnite Achievements imports it. PA reads it at
   Playnite startup and removes the file, so **new unlocks need one Playnite restart** before
   they show up (Hydra Sync reminds you the first time).
6. **HowLongToBeat (optional)** — the games whose playtime was raised are submitted to the
   already-loaded HowLongToBeat extension, one at a time, after the sync notification.

Syncing runs off the UI thread and database writes are handed back to Playnite's UI thread, so
the interface stays responsive. The extension's own files (playtime originals, per-game modes,
cached schema) live next to it in
`%APPDATA%\Playnite\ExtensionsData\A76358E9-BFA2-4189-B6F3-2307EA4E217B\`.

## Troubleshooting

Start with the log: `%APPDATA%\Playnite\logs\Playnite.log`, search for `HydraSync`. Warnings and
errors explain where each game stopped. Two in-app reports cover most cases:

- `Hydra Sync → Diagnose achievement sync…` (per game) — match, files found, unlocks parsed,
  pending Playnite Achievements import, quarantine, per-game sync mode.
- `Hydra Sync → Diagnose HowLongToBeat playtime sync…` (per game) — whether the extension is
  reachable, signed in, has data for the game, and whether the game is excluded.

### Playtime isn't syncing

| Symptom | Cause / fix |
|---|---|
| "Hydra database not found at …" | Hydra Sync couldn't find Hydra's data. It detects the usual locations automatically; if yours is elsewhere (portable install, for example), set **Hydra data directory**. |
| Game's playtime unchanged | Playtime is only replaced when **Hydra's total is higher**. If Playnite's value is already larger, the game is left alone by design. |
| Playtime looks wrong after a sync | **Undo playtime changes…** (settings panel) restores every game the extension modified to its pre-sync value, and the next sync applies the replace rule cleanly. |
| Wrong game matched | Matching is by AppID first, then title. Only games already in your library are synced — Hydra-only titles are never added. |
| Nothing happens on its own | Automatic syncing is **off by default**. Enable **Auto-sync**; it takes effect when you save. |
| Hydra is running, or the game lives on a network drive | Expected: Hydra locks its database, so a temporary snapshot is read instead. No action needed. |

### Achievements aren't showing up

Playnite itself has no achievement display — unlocks appear in **Playnite Achievements**, and it
only imports them at Playnite startup. A sync always needs **one Playnite restart** before new
unlocks show up.

Right-click the game → `Hydra Sync → Diagnose achievement sync…` and read the report:

| Report says | Meaning / fix |
|---|---|
| `Hydra match: NONE` | The game isn't matched to a Hydra entry. Playtime won't sync either. |
| `Achievement files found: 0` | No achievement file was located on disk. Check that Hydra itself shows achievements for this game. |
| Files found, but `Unlocks parsed: 0` | Files exist but contain no unlocked achievements (or an unsupported variant) — open the listed paths manually. |
| `Cache file awaiting PA import: YES` | Everything worked; restart Playnite so Playnite Achievements imports it. |
| `PA QUARANTINE` | Playnite Achievements' parser rejected our file — please report it with that file attached. |
| Playnite Achievements folder `NOT FOUND` | Playnite Achievements isn't installed, or has never been started. Exports are skipped with an error notification. |
| `Fingerprint recorded: yes` | Unlocks are unchanged since the last write — restart Playnite to trigger the import. |

Also check the settings: **Sync achievements** and **Write to Playnite Achievements** must be on.
Every sync notification includes scan stats (`scanned …, files found for …, with unlocks in …`) —
if those stay at 0, the problem is finding the files (the first two rows above).

### Only some games are syncing

Per-game modes narrow what syncs for individual games, and the global switches stay master
switches (see [Per-game sync](#per-game-sync)). Right-click a game → `Hydra Sync` → `Always
sync: playtime and achievements` clears the override, and the sync notification reports how many
games were skipped. The `Sync playtime now` / `Sync achievements now` / `Sync playtime &
achievements now` items act on the selected games only and ignore both the switches and the
stored mode.

### HowLongToBeat isn't updating

| Symptom | Cause / fix |
|---|---|
| No HowLongToBeat notification after a sync | **Push playtime to HowLongToBeat** is off by default — enable it in settings. |
| "HowLongToBeat is not available …" | The extension isn't installed or not loaded yet. Install it, then use **Push playtime to HowLongToBeat now** or sync again. |
| "updated 0, skipped N" | Expected for signed-out accounts, games carrying HowLongToBeat's ignore tag, and games it has no data for. `Diagnose HowLongToBeat playtime sync…` says which. |
| No push after a sync | Only games whose playtime the sync actually **raised** are pushed — that's intentional. |
| A very large sync pushed only some games | Capped at 25 games per sync; the notification says so. Sync again to continue. |

### Errors and odd behaviour elsewhere

- **Bad path in settings** — invalid folders produce an error notification, never a crash.
- **Notifications stop appearing** — Playnite collapses repeated notifications under one icon;
  click it to expand.
- **Menu items look stale** — restart Playnite after updating the extension.

## Known limitations

- Playtime from Hydra games that can't be matched to an existing Playnite game is not imported
  (sync-existing-only by design). Non-Steam games match by normalized title.
- Replace-if-larger uses Hydra's cumulative total: if a game's Hydra total exceeds Playnite's
  value, Playnite's value is replaced outright, so sessions played **only** outside Hydra are
  then not counted. That's the requested behavior — Hydra wins whenever it is higher.
- Undo puts back the value captured immediately before the extension first changed a game, so a
  game's original playtime comes back exactly. It only affects games the extension touched;
  anything Playnite recorded after the first sync is discarded by design. In the rare case where
  no original was recorded, the value is recovered from how much the extension added and the
  completion notification labels those games as approximate.
- For non-Steam games with no discoverable AppID and no achievement file in the game folder,
  nothing is written — there's nothing to read. Achievement names fall back to prettified names
  when no metadata source works, and icons are then omitted. Supplying a Steam Web API key fixes
  metadata for any Steam app.
- Playnite Achievements imports files at startup only — new unlocks appear after a Playnite
  restart (or whenever PA re-runs its import).
- When a game's unlocks change, Hydra Sync overwrites Playnite Achievements' stored data for that
  game rather than merging into it.

## Building from source

Needs the .NET SDK (8, 9 or 10). The extension targets .NET Framework 4.8 through reference
assemblies, so it also builds on macOS and Linux:

```bash
dotnet build src/HydraSync/HydraSync.csproj
dotnet run --project tests/hydrasync-tests.csproj   # test harness
./scripts/package.sh                                # → dist/HydraSync-<version>.pext
```

If `dotnet` isn't found on macOS with Homebrew, add
`export DOTNET_ROOT="$(brew --prefix dotnet)/libexec"` and that directory to your `PATH`.

The package is a plain zip, so its output can be installed by extracting it (see
[Install](#install)). Published releases are built by CI from a pushed tag, so building locally
is only needed to test changes or to package a specific version yourself.

Layout:

```
src/HydraSync/
  HydraSyncPlugin.cs        # plugin entry: lifecycle, menus, timer, notifications
  PluginSettings.cs         # settings model
  SettingsView.cs           # code-only WPF settings view
  extension.yaml            # manifest
  Hydra/                    # database reader, game model, achievement file locator + parsers
  Achievements/             # metadata lookups, Playnite Achievements writer
  Integrations/             # bridge to the HowLongToBeat extension
  Sync/                     # sync engine + persisted state
  Update/                   # release update checker
tests/                      # test harness + database fixture
```

The harness covers the Hydra layer end to end: a sample database, every achievement file
format, game-folder discovery, sync decisions, the release-check version logic and the
HowLongToBeat bridge. It runs in CI before each release, so a failing check blocks the published
build. The sample database is committed and mirrors Hydra's real on-disk encoding; regenerate it
with `cd tests/fixtures && npm i && node make-fixture.js`.