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

### Tests live in the repo (`tests/`)

105 checks in a net10 console harness (the shipped plugin targets net48, so the harness is net10):

```bash
dotnet run --project tests/hydrasync-tests.csproj   # must end with "RESULT: 105 passed, 0 failed"
```

CI runs this as the **Run tests** step before Build, so a failing check blocks the release.
Gotchas that will bite you:
- `tests/hydrasync-tests.csproj` pulls production sources in by **relative path** with
  `EnableDefaultCompileItems=false`. **Adding or renaming any testable source file means
  editing that csproj** or the new code silently isn't tested.
- Only Playnite-independent files compile: `Hydra/*.cs`, `Sync/PlaytimeSyncLogic.cs`,
  `Sync/GameSyncMode.cs`, `Achievements/LocalAchievementDefinitions.cs`,
  `Achievements/SteamSchemaClient.cs`, `Update/UpdateChecker.cs`,
  `Integrations/HowLongToBeatBridge.cs`. `HydraSyncPlugin.cs`, `SyncEngine.cs` and
  `SyncState.cs` need the Playnite runtime → keep new decision logic in a pure static helper
  (see `PlaytimeSyncLogic`, `GameSyncModeLogic`) and add a harness section for it.
  `tests/PlayniteStubs.cs` supplies minimal `ILogger`/`LogManager`;
  `tests/FakeHowLongToBeat.cs` is the stand-in for the other extension (its
  `PluginDatabase` must stay a **property**, not a field — the bridge looks it up that way).
- The LevelDB fixture DB is **committed** at `tests/fixtures/hydra-db-fixture` (binary
  snappy-compressed data produced by Node `classic-level`, mirroring Hydra's on-disk
  encoding). Regenerate with `cd tests/fixtures && npm i && node make-fixture.js`.
- Fixtures are written under `%TEMP%/hydrasync-test-<guid>` only, so the harness is safe to
  run from any working directory (it locates the repo root from `AppContext.BaseDirectory`).

## Version numbering: bump the smallest amount that fits

| Change | Bump | Example |
|---|---|---|
| New feature | **+0.1** (minor) | 1.8.0 → **1.9.0** |
| Bug fix only | **+0.0.1** (patch) | 1.9.0 → **1.9.1** |
| A batch of features / genuinely major | major | 1.9.0 → **2.0.0** |

Don't inflate the version: features take a minor bump, fixes a patch, and a major version is
reserved for a large batch of work.

## Ship it: releases are fully automated, the manifest is the gate

```bash
# 1. bump Version: in src/HydraSync/extension.yaml (see numbering above)
# 2. author .github/release-notes/vX.Y.Z.md from _template.md (CI uses it as the release body)
#    (the README links to Releases instead of naming a version, so nothing to bump there)
rm -f dist/HydraSync-<old-version>.pext          # package.sh only removes the current version
./scripts/package.sh
git add -A && git commit -m "..." && git push origin main
git tag vX.Y.Z && git push origin vX.Y.Z          # tag is the ONLY workflow trigger
gh run watch $(gh run list --limit 1 --json databaseId -q '.[0].databaseId') --exit-status
gh release view vX.Y.Z                            # confirm the .pext asset exists
git pull --rebase                                 # pick up CI's InstallerManifest commit
```

CI (`.github/workflows/release.yml`, `windows-latest`) runs the harness, enforces
**tag == manifest version**, then builds, packages, publishes the release, and commits the
new entry into `InstallerManifest.yaml` on `main` — which is why your local main is behind
after a tag push until you `git pull --rebase`. Don't hand-edit `InstallerManifest.yaml`.
Missing release-notes file → CI warns and falls back to a generated body (it still releases).
Workflow changes only take effect on the next tag push — there is no PR CI.

## Manifest + packaging rules (Playnite rejects violations)

- `src/HydraSync/extension.yaml`: `Type: GenericPlugin`, `Module: HydraSync.dll`, `Id` must
  equal the class `Id` GUID in `HydraSyncPlugin.cs`, `Version` must parse as `System.Version`.
- `.pext` is a plain zip with `extension.yaml` **at the archive root** (flat, no wrapping dir).
- **Never ship `Playnite*.dll`** (Playnite's `VerifyExtensionPackage` rejects the package) and
  skip `.pdb`. Dependencies (`LevelDb.Managed`, `Snappier`, `Newtonsoft.Json`, `System.*`
  shims) *are* shipped — Playnite probes the extension folder first.
- `dist/` and `*.pext` are gitignored; releases come from CI, not from local artifacts.

## The sync pipeline (one pass)

Kept here rather than in the README, which is user-facing and would only repeat the feature
table. Entry points: `StartSync` (whole library), `SyncSelected` (per-game one-shot), plus the
auto-sync timer and the update check. All of them share the `_syncRunning` guard.

1. **Read** — `HydraDbReader.ReadGames` opens Hydra's LevelDB read-only (`LevelDb.Managed`);
   on failure it copies the folder to a GUID-named temp dir (excluding `LOCK`, 3 attempts) and
   reads that. Path resolution is `HydraSyncPlugin.ResolveHydraDbPath`.
2. **Match** — `MatchIndex` over `Database.Games`: strong `GameId` == `objectId`, weak
   `NormalizeTitle`; Steam-source games preferred, then most recent activity.
3. **Playtime** — `ApplyPlaytime`: `ShouldRaise` → `Playtime` = Hydra total (seconds), plus
   `PlaytimeRaisedCount` / `PlaytimeAddedSeconds` / `PlaytimeRaisedGameIds`; `LastActivity`
   forward-only; `OriginalPlaytimeSecs` captured before the first change.
4. **Achievements** — `ProcessAchievementsAsync`: `BuildAppIds` → `AchievementFileLocator.Find`
   → `CollectUnlocks` → schema (local definitions → Web API key → store API) → details →
   provider-namespaced fingerprint → `PaCacheWriter.Write` with ProviderKey `Steam`/`Manual`.
5. **Hand-off** — PA's `LegacyJsonCacheImporter` imports `achievement_cache/<guid>.json` at
   Playnite startup and deletes it, so every new unlock batch needs one Playnite restart.
6. **HowLongToBeat (optional)** — `HowLongToBeatBridge.PushPlaytime` over
   `summary.PlaytimeRaisedGameIds`, cap 25 per sync, after the sync notification.

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
- **Talking to another extension stays reflection-only** (`Integrations/HowLongToBeatBridge.cs`):
  never reference a third-party extension's DLL. Find the already-loaded assembly in the app
  domain, late-bind the one method you need, and fill that method's unknown parameters with
  their declared defaults so a signature change can't throw into the sync path. Resolution
  failures are retried (not latched) and degrade to a single "unavailable" notification.
  `AssemblyResolver` / `PluginTypeResolver` / `ResetForTests` exist so the harness can exercise
  every branch — keep them `internal`.

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

## Reference sources (not vendored, not in the repo)

Most of this plugin's correctness comes from matching two external codebases exactly. Keep
shallow clones around for greps instead of re-researching:

```bash
git clone --depth 1 https://github.com/JosefNemec/Playnite            # SDK + app internals
git clone --depth 1 https://github.com/justin-delano/PlayniteAchievements   # PA import path
```

The questions they answer: Playnite — SDK signatures, extension discovery/installer rules,
how settings views are filtered, what `Game`/`IItemCollection` allow. Playnite Achievements —
the `achievement_cache` legacy import and its `GameAchievementData`/`AchievementDetail` schema.
Local clones have been living in the macOS temp dir; **re-clone if they're gone**. Note
`gh_grep` misses these repos — local `grep -rn` is faster and reliable.

## Other references

- `README.md` — **user-facing only**: features, settings, troubleshooting, limitations,
  contributor build commands. Everything internal (SDK quirks, PA import internals, packaging
  rationale, state-file layout, version bump references, and the sync pipeline described above)
  belongs here, not there. Keep README free of class names, GUIDs of other extensions and file
  paths that only matter to development.
- `tests/` — harness (`Program.cs` sections 1-9), `PlayniteStubs.cs`, `fixtures/`.
- `.github/release-notes/README.md` — how to cut a release, notes tone rules.