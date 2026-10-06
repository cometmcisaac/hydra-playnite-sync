# AGENTS.md — Hydra Sync (Playnite extension)

Playnite 10 extension that syncs playtime + achievements from Hydra Launcher into Playnite
and Playnite Achievements. C# / .NET Framework 4.8, no XAML (all UI is built in code).

## Environment: dotnet is not on PATH by default

Every shell must export these before any `dotnet` command (macOS + Homebrew dotnet):

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
export PATH="$DOTNET_ROOT/bin:$PATH"
```

## Commands

```bash
dotnet build src/HydraSync/HydraSync.csproj -v q   # 0 errors AND 0 warnings is the bar
./scripts/package.sh                                # builds + zips dist/HydraSync-<version>.pext
```

`package.sh` is the only packaging path. It hardcodes the macOS `DOTNET_ROOT` and reads the
version out of `extension.yaml`, so **bumping `extension.yaml` `Version:` is what names the package**.

### Tests live OUTSIDE the repo

84 checks in a separate net10 console harness:

```
/private/var/folders/mv/bp6yrdr91h30rmm_g38j575h0000gn/T/opencode/ldb-fixture/tests
```

```bash
cd /private/var/folders/mv/bp6yrdr91h30rmm_g38j575h0000gn/T/opencode/ldb-fixture/tests && dotnet run
```

Gotchas that will bite you:
- The harness `.csproj` pulls production sources in by **absolute path** with
  `EnableDefaultCompileItems=false`. **Adding or renaming any testable source file means
  editing that csproj** or the new code silently isn't tested.
- Only Playnite-independent files compile: `Hydra/*.cs`, `Sync/PlaytimeSyncLogic.cs`,
  `Sync/GameSyncMode.cs`, `Achievements/LocalAchievementDefinitions.cs`,
  `Achievements/SteamSchemaClient.cs`, `Update/UpdateChecker.cs`. `HydraSyncPlugin.cs`,
  `SyncEngine.cs` and `SyncState.cs` need the Playnite runtime → keep new decision logic in
  a pure static helper (see `PlaytimeSyncLogic`, `GameSyncModeLogic`) and add a harness
  section for it. `PlayniteStubs.cs` supplies minimal `ILogger`/`LogManager`.
- Fixture DB is produced by Node `classic-level` (forces real snappy-compressed `.ldb` +
  WAL), mirroring Hydra's on-disk encoding.

## Ship it: releases are fully automated, the manifest is the gate

```bash
# 1. bump Version: in src/HydraSync/extension.yaml   2. bump README version refs
# 3. author .github/release-notes/vX.Y.Z.md from _template.md (CI uses it as the release body)
git add -A && git commit -m "..." && git push origin main
git tag vX.Y.Z && git push origin v1.9.0   # tag is the ONLY workflow trigger
gh run watch $(gh run list --limit 1 --json databaseId -q '.[0].databaseId')
```

CI (`.github/workflows/release.yml`, `windows-latest`) enforces **tag == manifest version**
and then builds, packages, publishes the release, and commits the new entry into
`InstallerManifest.yaml` on `main` — so after a tag push your local main is behind until you
`git pull --rebase`. Don't hand-edit `InstallerManifest.yaml`. Missing release-notes file →
CI warns and falls back to a generated body (it still releases).

## Manifest + packaging rules (Playnite rejects violations)

- `src/HydraSync/extension.yaml`: `Type: GenericPlugin`, `Module: HydraSync.dll`, `Id` must
  equal the class `Id` GUID in `HydraSyncPlugin.cs`, `Version` must parse as `System.Version`.
- `.pext` is a plain zip with `extension.yaml` **at the archive root** (flat, no wrapping dir).
- **Never ship `Playnite*.dll`** (Playnite's `VerifyExtensionPackage` rejects the package) and
  skip `.pdb`. Dependencies (`LevelDb.Managed`, `Snappier`, `Newtonsoft.Json`, `System.*`
  shims) *are* shipped — Playnite probes the extension folder first.
- `dist/` and `*.pext` are gitignored; releases come from CI, not from local artifacts.

## Conventions and gotchas that differ from defaults

- **Generic plugins are invisible in settings unless** the ctor sets
  `Properties = new GenericPluginProperties { HasSettings = true }` — Playnite filters
  `Addons → Extensions settings → Generic` on it (this was a real v1.4.1 bug).
- **The Playnite 10 SDK has no sub-menus and no `ShowDialog(content)`** — `GameMenuItem` only
  has `{Description, MenuSection, Action}`, and `IDialogsFactory` only does `ShowMessage`.
  Menus must be flat items; "dialogs" are message boxes. This forced the current
  one-shot-vs-`Always sync:` menu layout.
- `NotificationType` has only `Info` and `Error` (no `Warning`).
- **Long-running work goes off the UI thread, DB writes come back via `RunOnUi`**
  (SynchronizationContext captured in `OnApplicationStarted`). All sync entry points share the
  `_syncRunning` Interlocked guard.
- Plugin-owned state lives in `GetPluginUserDataPath()`: `sync_state.json` (playtime originals,
  per-game `Always sync:` modes, achievement fingerprints) and `steam_schema_cache/`.
  Writes are tmp-file + `File.Replace` — keep it that way.
- Achievements reach Playnite Achievements by writing
  `…\ExtensionsData\PlayniteAchievements\achievement_cache\{game-guid}.json`; PA imports and
  deletes those files **on Playnite startup**, so a new unlock needs one restart to appear.
  The folder name is PA's *class GUID* dir, resolved dynamically in `PaCacheWriter`.
- Hydra's LevelDB is locked while Hydra runs → `HydraDbReader` falls back to a GUID-named temp
  snapshot copy. Object IDs from untrusted data are only used in file paths when all-digits.

## Style rules the maintainer enforces

- **Never name emulator/crack brands in README, release notes, or commit/release copy.**
  Say "achievement files read from disk". `.github/release-notes/README.md` holds the rule;
  verify with `grep -ci "crack\|emulator\|Goldberg\|CODEX\|RUNE\|GSE" README.md .github/release-notes/*.md`
  (the convention doc itself is the one allowed exception).
- Release notes are user-facing per version (`What's new` / `Upgrade notes` / `Install`),
  including explicit **upgrade notes for behavior changes** (e.g. auto-sync became opt-in in
  1.6.0) and the settings path reminder: *Add-ons → Extensions settings → Generic → Hydra Sync*.
- `InstallerManifest.yaml` / `PlayniteAddonDatabase-HydraSync.yaml` exist for future submission
  to Playnite's official add-on database (currently not accepting new plugins); the in-app
  updater is the live mechanism.

## Other references

- `README.md` — user-facing feature/how-it-works/test-checklist documentation.
- `.github/release-notes/README.md` — how to cut a release, notes tone rules.