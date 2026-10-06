using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using HydraSync.Achievements;
using HydraSync.Hydra;
using HydraSync.Integrations;
using HydraSync.Sync;
using HydraSync.Update;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace HydraSync
{
    /// <summary>
    /// "Hydra Sync" - syncs playtime and achievements between Hydra Launcher and Playnite.
    ///
    /// Reads Hydra's local LevelDB (%APPDATA%\Hydra\hydra-db or older
    /// %APPDATA%\hydralauncher\hydra-db) and cracker achievement files, applies playtime
    /// deltas to matched Playnite games, and exports achievement unlocks into
    /// PlayniteAchievements' import queue.
    /// </summary>
    public class HydraSyncPlugin : GenericPlugin
    {
        public static HydraSyncPlugin Instance { get; private set; }

        public override Guid Id => new Guid("A76358E9-BFA2-4189-B6F3-2307EA4E217B");

        public PluginSettings Settings { get; private set; }

        private SyncState _state;
        private SynchronizationContext _uiContext;
        private Timer _timer;
        private int _syncRunning;
        private bool _paWarned;
        private bool _hltbWarned;
        private ILogger _log;

        /// <summary>HowLongToBeat pushes are rate-limited per sync - a large first sync would
        /// otherwise fire dozens of submission round-trips at their API.</summary>
        private const int HltbPushCap = 25;
        private UpdateCheckResult _availableUpdate;
        private CancellationTokenSource _updateCts;

        private string StatePath => Path.Combine(GetPluginUserDataPath(), "sync_state.json");
        private string SchemaCacheDir => Path.Combine(GetPluginUserDataPath(), "steam_schema_cache");

        public HydraSyncPlugin(IPlayniteAPI api) : base(api)
        {
            // Playnite only lists a generic plugin in Settings → Extensions when
            // Properties.HasSettings is true (AddonsViewModel GenericPlugins filter).
            Properties = new GenericPluginProperties { HasSettings = true };
            Instance = this;
            _log = LogManager.GetLogger("HydraSync");
            Settings = LoadPluginSettings<PluginSettings>() ?? new PluginSettings();
            Settings.AttachPlugin(this);
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            _uiContext = SynchronizationContext.Current;
            _state = SyncState.Load(StatePath);
            ScheduleTimer();

            _updateCts = new CancellationTokenSource();
            if (Settings == null || Settings.CheckForUpdates)
            {
                ScheduleUpdateCheck(_updateCts.Token);
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            _timer?.Dispose();
            _timer = null;
            _updateCts?.Cancel();
            _updateCts = null;
        }

        public override ISettings GetSettings(bool firstRunSettings) => Settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new SettingsView();

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Sync now",
                MenuSection = "@Hydra Sync",
                Icon = "sync",
                Action = _ => StartSync(true),
            };

            var update = _availableUpdate;
            if (update != null && update.UpdateAvailable)
            {
                yield return new MainMenuItem
                {
                    Description = "Download & install update (" + update.LatestVersion + ")…",
                    MenuSection = "@Hydra Sync",
                    Icon = "download",
                    Action = _ => InstallUpdate(update),
                };
            }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var selected = args != null && args.Games != null ? args.Games : new List<Game>();
            var selectionLabel = selected.Count == 1
                ? $"\"{selected[0].Name}\""
                : $"{selected.Count} selected games";

            // Immediate one-shot syncs: clicking these syncs right now, for the selected
            // game(s) only, with exactly the requested scope.
            yield return new GameMenuItem
            {
                Description = "Sync playtime now",
                MenuSection = "Hydra Sync",
                Icon = "sync",
                Action = a => SyncSelected(a.Games, GameSyncMode.PlaytimeOnly, selectionLabel),
            };
            yield return new GameMenuItem
            {
                Description = "Sync achievements now",
                MenuSection = "Hydra Sync",
                Icon = "sync",
                Action = a => SyncSelected(a.Games, GameSyncMode.AchievementsOnly, selectionLabel),
            };
            yield return new GameMenuItem
            {
                Description = "Sync playtime & achievements now",
                MenuSection = "Hydra Sync",
                Icon = "sync",
                Action = a => SyncSelected(a.Games, GameSyncMode.Both, selectionLabel),
            };

            // Persistent per-game mode: what future full/auto syncs do with this game.
            // Playnite's SDK has no sub-menu support, so the three modes are separate
            // items and the active one is labelled "(current)".
            var currentMode = SelectedMode(selected);
            yield return new GameMenuItem
            {
                Description = ModeDescription("Always sync: playtime and achievements", GameSyncMode.Both, currentMode),
                MenuSection = "Hydra Sync",
                Action = a => SetGameMode(a.Games, GameSyncMode.Both),
            };
            yield return new GameMenuItem
            {
                Description = ModeDescription("Always sync: playtime only", GameSyncMode.PlaytimeOnly, currentMode),
                MenuSection = "Hydra Sync",
                Action = a => SetGameMode(a.Games, GameSyncMode.PlaytimeOnly),
            };
            yield return new GameMenuItem
            {
                Description = ModeDescription("Always sync: achievements only", GameSyncMode.AchievementsOnly, currentMode),
                MenuSection = "Hydra Sync",
                Action = a => SetGameMode(a.Games, GameSyncMode.AchievementsOnly),
            };

            yield return new GameMenuItem
            {
                Description = "Push playtime to HowLongToBeat now",
                MenuSection = "Hydra Sync",
                Action = a => PushPlaytimeToHltbNow(a.Games, selectionLabel),
            };

            yield return new GameMenuItem
            {
                Description = "Diagnose achievement sync…",
                MenuSection = "Hydra Sync",
                Action = a =>
                {
                    var game = a.Games != null && a.Games.Count > 0 ? a.Games[0] : null;
                    if (game != null) DiagnoseAchievements(game);
                },
            };

            yield return new GameMenuItem
            {
                Description = "Diagnose HowLongToBeat playtime sync…",
                MenuSection = "Hydra Sync",
                Action = a =>
                {
                    var game = a.Games != null && a.Games.Count > 0 ? a.Games[0] : null;
                    if (game != null) DiagnoseHowLongToBeat(game);
                },
            };
        }

        /// <summary>
        /// Runs a one-shot sync for just the selected games with the requested scope.
        /// Explicit requests bypass the global switches and the stored per-game mode,
        /// because the user clicked this exact action.
        /// </summary>
        internal void SyncSelected(List<Game> games, GameSyncMode mode, string selectionLabel)
        {
            var ids = new List<Guid>();
            if (games != null)
            {
                foreach (var game in games)
                {
                    if (game != null) ids.Add(game.Id);
                }
            }

            if (ids.Count == 0) return;

            if (Interlocked.CompareExchange(ref _syncRunning, 1, 0) != 0)
            {
                Notify(
                    "hydrasync-busy",
                    "Hydra Sync: a sync is already running - try again in a moment.",
                    NotificationType.Error);
                return;
            }

            var label = GameSyncModeLogic.Describe(mode);
            Task.Run(() => RunSelectedSyncAsync(ids, mode, label, selectionLabel));
        }

        private async Task RunSelectedSyncAsync(
            List<Guid> ids,
            GameSyncMode mode,
            string label,
            string selectionLabel)
        {
            try
            {
                if (_state == null) _state = SyncState.Load(StatePath);

                var engine = new SyncEngine(
                    PlayniteApi, Settings, _state, StatePath, SchemaCacheDir,
                    ResolveHydraDbPath, _log, RunOnUi);
                var summary = await engine.RunAsync(ids, mode);

                if (summary.Error != null)
                {
                    Notify("hydrasync-error", "Hydra Sync failed: " + summary.Error, NotificationType.Error);
                }
                else if (!summary.HydraDbFound)
                {
                    Notify(
                        "hydrasync-nodb",
                        $"Hydra database not found at \"{ResolveHydraDbPath()}\". Is Hydra installed? " +
                        "You can point Hydra Sync at a custom location in extension settings.",
                        NotificationType.Error);
                }
                else if (summary.Matched == 0)
                {
                    Notify(
                        "hydrasync-nomatch",
                        $"Hydra Sync: no Hydra match for {selectionLabel} ({label}) - nothing was synced. " +
                        "Use Diagnose achievement sync on the game to see what was found.",
                        NotificationType.Error);
                }
                else
                {
                    Notify(
                        "hydrasync-done-selected",
                        $"Hydra Sync ({label}): {selectionLabel} - raised playtime on " +
                        $"{summary.PlaytimeRaisedCount} game(s) (+{FormatMinutes(summary.PlaytimeAddedSeconds)}), " +
                        $"{summary.AchievementsWritten} achievement set(s) updated.",
                        NotificationType.Info);
                }

                if (Settings.PushPlaytimeToHowLongToBeat && summary.PlaytimeRaisedGameIds.Count > 0)
                {
                    await PushPlaytimeToHltbAsync(summary.PlaytimeRaisedGameIds);
                }
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "HydraSync: selected sync failed");
                Notify("hydrasync-error", "Hydra Sync failed: " + ex.Message, NotificationType.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _syncRunning, 0);
            }
        }

        private static string ModeDescription(string text, GameSyncMode mode, GameSyncMode current)
        {
            return mode == current ? text + " (current)" : text;
        }

        /// <summary>
        /// Mode shown for the current selection: the first non-default override among
        /// the selected games, otherwise "both". With a mixed selection the menu shows
        /// whichever override comes first rather than claiming something is current.
        /// </summary>
        private GameSyncMode SelectedMode(List<Game> games)
        {
            if (games == null || _state == null) return GameSyncMode.Both;

            foreach (var game in games)
            {
                if (game == null) continue;
                var mode = _state.GetGameMode(game.Id);
                if (mode != GameSyncMode.Both) return mode;
            }

            return GameSyncMode.Both;
        }

        private void SetGameMode(List<Game> games, GameSyncMode mode)
        {
            try
            {
                if (_state == null) _state = SyncState.Load(StatePath);

                var count = 0;
                foreach (var game in games ?? new List<Game>())
                {
                    if (game == null) continue;
                    _state.SetGameMode(game.Id, mode);
                    count++;
                }

                if (count == 0) return;

                _state.Save(StatePath);
                Notify(
                    "hydrasync-mode",
                    "Hydra Sync: " + count + (count == 1 ? " game set to " : " games set to ") +
                    GameSyncModeLogic.Describe(mode) + ".",
                    NotificationType.Info);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "HydraSync: failed to store per-game sync mode");
                Notify("hydrasync-mode-error", "Hydra Sync could not save the per-game mode.", NotificationType.Error);
            }
        }

        // ---------- HowLongToBeat ----------

        /// <summary>
        /// Hands the games whose playtime this run raised to the HowLongToBeat extension, one
        /// game at a time. Runs after the sync notification on purpose: each push is a couple of
        /// HTTP round-trips inside the other extension, so the sync result must not wait for it.
        /// </summary>
        private async Task PushPlaytimeToHltbAsync(List<Guid> gameIds)
        {
            if (gameIds == null || gameIds.Count == 0) return;

            if (!HowLongToBeatBridge.IsAvailable)
            {
                WarnHltbUnavailableOnce();
                return;
            }

            var targets = new List<(Guid, object)>();
            foreach (var id in gameIds)
            {
                var game = PlayniteApi.Database.Games.Get(id);
                if (game != null) targets.Add((id, game));
            }

            if (targets.Count == 0) return;

            var report = await Task.Run(
                () => HowLongToBeatBridge.PushPlaytime(targets, HltbPushCap, _log));

            if (report.Unavailable)
            {
                WarnHltbUnavailableOnce();
                return;
            }

            if (report.Updated == 0 && report.Skipped == 0 && report.Failed == 0) return;

            var text = $"Hydra Sync → HowLongToBeat: {HowLongToBeatBridge.Describe(report)}";
            var capNote = gameIds.Count > HltbPushCap ? $" (capped at {HltbPushCap} per sync)" : string.Empty;
            Notify(
                "hydrasync-hltb",
                text + capNote + ".",
                report.Failed > 0 ? NotificationType.Error : NotificationType.Info);
        }

        /// <summary>
        /// One-shot push for the selected games, independent of the sync settings (the user
        /// clicked this exact action). Nothing is raised here - it submits the playtime the
        /// game already has in Playnite.
        /// </summary>
        private void PushPlaytimeToHltbNow(List<Game> games, string selectionLabel)
        {
            var ids = new List<Guid>();
            foreach (var game in games ?? new List<Game>())
            {
                if (game != null) ids.Add(game.Id);
            }

            if (ids.Count == 0) return;

            Task.Run(async () =>
            {
                try
                {
                    await PushPlaytimeToHltbAsync(ids);
                }
                catch (Exception ex)
                {
                    _log?.Error(ex, "HydraSync: HowLongToBeat push failed");
                    Notify("hydrasync-hltb-error", "Hydra Sync could not push playtime to HowLongToBeat: " + ex.Message, NotificationType.Error);
                }
            });
        }

        private void WarnHltbUnavailableOnce()
        {
            _log?.Warn("HydraSync: HowLongToBeat unavailable - playtime push skipped");
            if (_hltbWarned) return;

            _hltbWarned = true;
            Notify(
                "hydrasync-nohlb",
                "Hydra Sync: HowLongToBeat is not available (" +
                (HowLongToBeatBridge.UnavailableReason ?? "extension not loaded") +
                "), so the playtime push was skipped. Install and log into the HowLongToBeat " +
                "extension, then use \"Push playtime to HowLongToBeat now\" on a game.",
                NotificationType.Error);
        }

        /// <summary>
        /// Shows what a HowLongToBeat push would do for one game right now: whether the
        /// extension is reachable, logged in, has data for the game, and whether the game is
        /// excluded from playtime sync.
        /// </summary>
        private void DiagnoseHowLongToBeat(Game game)
        {
            Task.Run(() =>
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Hydra Sync - HowLongToBeat playtime diagnostics");
                    sb.AppendLine("Game: " + game.Name);
                    sb.AppendLine("Playtime in Playnite: " +
                        TimeSpan.FromSeconds((long)game.Playtime).ToString(@"d\.hh\:mm\:ss"));
                    sb.AppendLine();
                    sb.AppendLine("Setting 'push playtime to HowLongToBeat': " +
                        (Settings.PushPlaytimeToHowLongToBeat ? "on" : "off") +
                        " (only games a sync actually raised are pushed automatically)");

                    var available = HowLongToBeatBridge.IsAvailable;
                    sb.AppendLine("HowLongToBeat extension: " +
                        (available ? "available" : "NOT AVAILABLE - " + HowLongToBeatBridge.UnavailableReason));

                    if (available)
                    {
                        var loggedIn = HowLongToBeatBridge.IsLoggedIn;
                        sb.AppendLine("Logged in: " +
                            (loggedIn == null ? "unknown" : loggedIn.Value ? "yes" : "no"));
                        sb.AppendLine("HowLongToBeat data linked: " +
                            (HowLongToBeatBridge.HasData(game.Id) ? "yes" : "no"));
                        sb.AppendLine("Excluded from playtime sync: " +
                            (HowLongToBeatBridge.IsIgnored(game) ? "yes" : "no"));
                    }

                    sb.AppendLine();
                    sb.AppendLine(available
                        ? "Use \"Push playtime to HowLongToBeat now\" to submit the value above right now."
                        : "Install the HowLongToBeat extension and log in, then run this again.");

                    var report = sb.ToString();
                    RunOnUi(() => PlayniteApi.Dialogs.ShowMessage(report, "Hydra Sync - HowLongToBeat diagnostics"));
                }
                catch (Exception ex)
                {
                    _log?.Error(ex, "HydraSync: HowLongToBeat diagnostics failed");
                    Notify("hydrasync-hltb-diag-error", "Hydra Sync could not read the HowLongToBeat state.", NotificationType.Error);
                }
            });
        }

        /// <summary>
        /// Runs the achievement pipeline head (match → IDs → files → unlocks) for one game
        /// and shows a human-readable report, so users can see exactly where sync stops.
        /// </summary>
        private void DiagnoseAchievements(Game game)
        {
            Task.Run(() =>
            {
                try
                {
                    if (_state == null) _state = SyncState.Load(StatePath);

                    var sb = new StringBuilder();
                    sb.AppendLine("Hydra Sync - achievement diagnostics");
                    sb.AppendLine("Game: " + game.Name);
                    sb.AppendLine("GameId: " + (string.IsNullOrEmpty(game.GameId) ? "(none)" : game.GameId));
                    sb.AppendLine("InstallDirectory: " +
                        (string.IsNullOrEmpty(game.InstallDirectory) ? "(none)" : game.InstallDirectory));
                    sb.AppendLine();
                    sb.AppendLine("Settings: SyncAchievements=" + Settings.SyncAchievements +
                        ", WriteToPA=" + Settings.WriteToPlayniteAchievements +
                        ", FetchSchema=" + Settings.FetchSteamSchema);
                    sb.AppendLine("Per-game sync mode: " + GameSyncModeLogic.Describe(_state.GetGameMode(game.Id)));

                    var dbPath = ResolveHydraDbPath();
                    var dbOk = Directory.Exists(dbPath);
                    sb.AppendLine("Hydra DB: " + dbPath + (dbOk ? " (found)" : " (NOT FOUND)"));

                    HydraGame h = null;
                    IReadOnlyList<HydraGame> hydraGames = new List<HydraGame>();
                    if (dbOk)
                    {
                        hydraGames = HydraDbReader.ReadGames(dbPath, _log);
                        h = SyncEngine.FindMatchForGame(hydraGames, game);
                    }
                    sb.AppendLine("Hydra games in DB: " + hydraGames.Count);

                    if (h == null)
                    {
                        sb.AppendLine("Hydra match: NONE - achievement sync only runs for matched games.");
                    }
                    else
                    {
                        sb.AppendLine("Hydra match: shop=" + h.Shop + ", objectId=" + h.ObjectId +
                            ", exe=" + (string.IsNullOrEmpty(h.ExecutablePath) ? "(none)" : h.ExecutablePath));
                        sb.AppendLine("Hydra playtime: " +
                            (h.PlayTimeInMilliseconds / 1000).ToString() + " s, last played: " +
                            (h.LastTimePlayed?.ToString() ?? "(never)"));

                        var exe = string.IsNullOrEmpty(h.ExecutablePath) ? null : h.ExecutablePath;
                        var ids = SyncEngine.BuildAppIds(h, exe, game.InstallDirectory);
                        sb.AppendLine("AppIDs used: " + (ids.Count > 0 ? string.Join(", ", ids) : "(none)"));

                        var localDefs = LocalAchievementDefinitions.FindFiles(exe, game.InstallDirectory);
                        sb.AppendLine("Local definition files: " +
                            (localDefs.Count > 0 ? string.Join(", ", localDefs) : "none"));
                        sb.AppendLine("Steam Web API key set: " +
                            (string.IsNullOrEmpty(Settings.SteamWebApiKey) ? "no" : "yes"));

                        var files = AchievementFileLocator.Find(ids, exe, game.InstallDirectory);
                        sb.AppendLine("Achievement files found: " + files.Count);
                        foreach (var f in files) sb.AppendLine("  [" + f.Type + "] " + f.Path);

                        var unlocks = SyncEngine.CollectUnlocks(files);
                        sb.AppendLine("Unlocks parsed: " + unlocks.Count);
                        var shown = 0;
                        foreach (var kv in unlocks)
                        {
                            if (shown++ >= 25) { sb.AppendLine("  ..."); break; }
                            string when;
                            try
                            {
                                when = kv.Value.HasValue
                                    ? DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.Value)
                                        .LocalDateTime.ToString("g")
                                    : "?";
                            }
                            catch { when = "?"; }
                            sb.AppendLine("  " + kv.Key + " @ " + when);
                        }

                        if (files.Count > 0 && unlocks.Count > 0)
                        {
                            var pending = Path.Combine(PaCacheWriter.CacheDir, game.Id.ToString("D") + ".json");
                            sb.AppendLine("Cache file awaiting PA import: " +
                                (File.Exists(pending)
                                    ? "YES - restart Playnite so Playnite Achievements can import it"
                                    : "no (already imported, or not written yet)"));

                            var quarDir = Path.Combine(PaCacheWriter.PaPluginDir, "achievement_cache_quarantine");
                            var quarantined = Directory.Exists(quarDir) &&
                                Directory.GetFiles(quarDir, game.Id.ToString("D") + "*.json").Length > 0;
                            if (quarantined)
                            {
                                sb.AppendLine("PA QUARANTINE: PA failed to parse our file for this game! " +
                                    "Check " + quarDir);
                            }

                            _state.Games.TryGetValue(h.Key, out var st);
                            sb.AppendLine("Fingerprint recorded: " +
                                (st?.AchievementFingerprint != null
                                    ? "yes (sync skips this game until unlocks change)"
                                    : "no"));
                        }
                    }

                    sb.AppendLine();
                    sb.AppendLine("PA plugin dir: " + PaCacheWriter.PaPluginDir +
                        (PaCacheWriter.IsAvailable ? "" : "  (NOT FOUND - is Playnite Achievements installed?)"));
                    sb.AppendLine();
                    sb.AppendLine("Note: Playnite itself has no achievement UI. Unlocks appear in " +
                        "Playnite Achievements (game view / theme panel) only after PA imports the " +
                        "cache file, which happens when Playnite next starts.");

                    var report = sb.ToString();
                    RunOnUi(() => PlayniteApi.Dialogs.ShowMessage(report, "Hydra Sync - Achievement diagnostics"));
                }
                catch (Exception ex)
                {
                    _log?.Error(ex, "HydraSync: diagnostics failed");
                    Notify("hydrasync-diag-error", "Diagnosis failed: " + ex.Message, NotificationType.Error);
                }
            });
        }

        /// <summary>Kicks off an async sync pass (safe to call from any thread).</summary>
        public void StartSync(bool notifyUser)
        {
            if (Interlocked.CompareExchange(ref _syncRunning, 1, 0) != 0) return;
            Task.Run(() => RunSyncAsync(notifyUser));
        }

        /// <summary>Called from settings EndEdit after the interval or auto-sync setting changed.</summary>
        internal void RescheduleTimer()
        {
            if (_uiContext != null) ScheduleTimer();
        }

        private void ScheduleTimer()
        {
            _timer?.Dispose();
            _timer = null;

            // Automatic syncing is opt-in; when disabled only manual "Sync now" runs.
            if (Settings == null || !Settings.AutoSync)
            {
                return;
            }

            var minutes = Math.Max(1, Settings?.SyncIntervalMinutes ?? 15);
            // First pass shortly after Playnite starts, then every interval.
            _timer = new Timer(_ => StartSync(false), null,
                TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(minutes));
        }

        /// <summary>
        /// Checks GitHub for a newer release a couple of minutes after startup so a
        /// network hiccup at launch never blocks anything. One call per session.
        /// </summary>
        private void ScheduleUpdateCheck(CancellationToken token)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), token);
                    if (token.IsCancellationRequested) return;

                    var result = await UpdateChecker.CheckAsync();
                    if (token.IsCancellationRequested || !result.UpdateAvailable) return;

                    _availableUpdate = result;
                    Notify("hydrasync-update",
                        $"Hydra Sync {result.LatestVersion} is available (installed: {result.CurrentVersion}). " +
                        "Use Main menu → @Hydra Sync → Download & install update to get it.",
                        NotificationType.Info);
                }
                catch (OperationCanceledException)
                {
                    // Plugin stopped while the check was pending.
                }
                catch (Exception ex)
                {
                    _log?.Debug("HydraSync: update check failed: " + ex.Message);
                }
            }, token);
        }

        /// <summary>
        /// Downloads the release .pext and opens it. Playnite's .pext file association
        /// routes the file to the running instance, which asks the user to confirm the
        /// update and queues it for the normal restart flow.
        /// </summary>
        private void InstallUpdate(UpdateCheckResult update)
        {
            var latest = update.LatestVersion;
            Task.Run(async () =>
            {
                try
                {
                    var tempFile = Path.Combine(Path.GetTempPath(), $"HydraSync-{latest}.pext");
                    await UpdateChecker.DownloadAsync(update.DownloadUrl, tempFile);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tempFile,
                        UseShellExecute = true,
                    });

                    Notify("hydrasync-update-run",
                        $"Downloaded Hydra Sync {latest}. Playnite will ask to confirm the update - " +
                        "answer Yes, then restart when prompted so the new version is installed.",
                        NotificationType.Info);
                }
                catch (Exception ex)
                {
                    _log?.Error(ex, "HydraSync: update download failed");
                    Notify("hydrasync-update-error",
                        "Hydra Sync update download failed: " + ex.Message, NotificationType.Error);
                }
            });
        }

        private async Task RunSyncAsync(bool notifyUser)
        {
            try
            {
                if (_state == null) _state = SyncState.Load(StatePath);

                var engine = new SyncEngine(
                    PlayniteApi, Settings, _state, StatePath, SchemaCacheDir,
                    ResolveHydraDbPath, _log, RunOnUi);

                var summary = await engine.RunAsync();

                if (summary.Error != null)
                {
                    Notify("hydrasync-error", "Hydra Sync failed: " + summary.Error, NotificationType.Error);
                }
                else if (!summary.HydraDbFound)
                {
                    if (notifyUser)
                    {
                        Notify("hydrasync-nodb",
                            $"Hydra database not found at \"{ResolveHydraDbPath()}\". Is Hydra installed? " +
                            "You can point Hydra Sync at a custom location in extension settings.",
                            NotificationType.Error);
                    }
                }
                else if (notifyUser)
                {
                    Notify("hydrasync-done",
                        $"Hydra Sync: matched {summary.Matched} of {summary.HydraGamesRead} Hydra games, " +
                        $"raised playtime on {summary.PlaytimeRaisedCount} game(s) (+{FormatMinutes(summary.PlaytimeAddedSeconds)}), " +
                        $"{summary.AchievementsWritten} achievement set(s) updated " +
                        $"(scanned {summary.AchievementGamesScanned}, files found for {summary.AchievementGamesWithFiles}, " +
                        $"with unlocks in {summary.AchievementGamesWithUnlocks})" +
                        PerGameOverrideSuffix(summary) + ".",
                        NotificationType.Info);
                }

                if (summary.PaUnavailable)
                {
                    _log?.Warn("HydraSync: PlayniteAchievements not installed - achievement export skipped");
                    if (!_paWarned)
                    {
                        _paWarned = true;
                        Notify("hydrasync-nopa",
                            "Hydra Sync: the Playnite Achievements data folder was not found, so achievement " +
                            "export was skipped. If you want the unlocks there, install the \"Playnite Achievements\" " +
                            "extension and restart Playnite once, then sync again.",
                            NotificationType.Error);
                    }
                }

                if (summary.AchievementsWritten > 0 && !_state.NotifiedPaImport)
                {
                    _state.NotifiedPaImport = true;
                    _state.Save(StatePath);
                    Notify("hydrasync-paimport",
                        "Hydra Sync wrote achievements for Playnite Achievements. " +
                        "Restart Playnite once so the extension imports them.",
                        NotificationType.Info);
                }

                // After the sync result is reported - a HowLongToBeat push is a few network
                // round-trips and must not delay the sync notification.
                if (Settings.PushPlaytimeToHowLongToBeat && summary.PlaytimeRaisedGameIds.Count > 0)
                {
                    await PushPlaytimeToHltbAsync(summary.PlaytimeRaisedGameIds);
                }
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "HydraSync: sync failed");
                Notify("hydrasync-error", "Hydra Sync failed: " + ex.Message, NotificationType.Error);
            }
            finally
            {
                Interlocked.Exchange(ref _syncRunning, 0);
            }
        }

        private string ResolveHydraDbPath()
        {
            var configured = Settings?.HydraDataDir?.Trim();
            if (string.IsNullOrEmpty(configured))
            {
                // Electron userData folder name changed across Hydra builds: newer releases
                // use %APPDATA%\Hydra, older ones use %APPDATA%\hydralauncher (from the
                // package name). Probe both, plus the staging DB and a direct-DB layout
                // (CURRENT file at the folder root).
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                foreach (var dir in DefaultHydraDbCandidates(appData))
                {
                    if (IsLevelDbDir(dir)) return dir;
                }

                return Path.Combine(appData, "Hydra", "hydra-db");
            }

            configured = Environment.ExpandEnvironmentVariables(configured);
            try
            {
                if (IsLevelDbDir(configured)) return configured;
                var sub = Path.Combine(configured, "hydra-db");
                if (IsLevelDbDir(sub)) return sub;
                var staging = Path.Combine(configured, "hydra-db-staging");
                if (IsLevelDbDir(staging)) return staging;
            }
            catch
            {
                // Invalid path - report as not found below.
            }

            return configured;
        }

        private static IEnumerable<string> DefaultHydraDbCandidates(string appData)
        {
            foreach (var name in new[] { "Hydra", "hydralauncher" })
            {
                yield return Path.Combine(appData, name, "hydra-db");
                yield return Path.Combine(appData, name, "hydra-db-staging");
                yield return Path.Combine(appData, name); // direct-DB layout
            }
        }

        private static bool IsLevelDbDir(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "CURRENT"));
        }

        /// <summary>
        /// Restores every game's playtime to the value it had before Hydra Sync first
        /// modified it (exact for entries captured by v1.2+, approximate recovery for
        /// entries written by the additive v1.x builds), then clears the playtime
        /// bookkeeping so the next sync re-applies replace-if-larger from a clean slate.
        /// Achievement state is untouched.
        /// </summary>
        internal void UndoPlaytimeChanges()
        {
            try
            {
                var confirm = PlayniteApi.Dialogs.ShowMessage(
                    "Restore Playnite playtime to what it was before Hydra Sync changed it?\n\n" +
                    "Only playtime is affected - achievements stay as they are. The next sync " +
                    "applies the replace-if-larger rule again from a clean slate.",
                    "Hydra Sync",
                    System.Windows.MessageBoxButton.YesNo);
                if (confirm != System.Windows.MessageBoxResult.Yes) return;

                if (_state == null) _state = SyncState.Load(StatePath);

                var updates = new List<Game>();
                var approximate = 0;

                foreach (var kv in _state.Games)
                {
                    var st = kv.Value;
                    if (st == null || string.IsNullOrEmpty(st.PlayniteGameId) ||
                        !Guid.TryParse(st.PlayniteGameId, out var gid))
                    {
                        continue;
                    }

                    var game = PlayniteApi.Database.Games.Get(gid);
                    if (game == null) continue;

                    var recordedAddedMs = st.LastHydraMs > 0
                        ? st.LastHydraMs + Math.Max(0, st.MsCarry)
                        : 0;
                    var restored = PlaytimeSyncLogic.Restore(
                        game.Playtime, st.OriginalPlaytimeSecs, recordedAddedMs, out var wasApprox);

                    if (restored != game.Playtime)
                    {
                        game.Playtime = restored;
                        updates.Add(game);
                        if (wasApprox) approximate++;
                    }

                    // Clear playtime bookkeeping; keep AchievementFingerprint.
                    st.OriginalPlaytimeSecs = null;
                    st.LastHydraMs = 0;
                    st.MsCarry = 0;
                }

                if (updates.Count > 0)
                {
                    PlayniteApi.Database.Games.Update(updates);
                }

                _state.Save(StatePath);

                Notify("hydrasync-undo", updates.Count == 0
                        ? "Hydra Sync: no playtime changes to undo."
                        : $"Hydra Sync: playtime restored for {updates.Count} game(s)" +
                          (approximate > 0 ? $" ({approximate} recovered approximately)" : "") +
                          ". The next sync applies the replace-if-larger rule again.",
                        NotificationType.Info);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "HydraSync: undo failed");
                Notify("hydrasync-undo-error", "Hydra Sync undo failed: " + ex.Message, NotificationType.Error);
            }
        }

        /// <summary>
        /// Appends a short note to the sync notification when per-game sync modes
        /// caused games to be skipped, so a partial sync does not look broken.
        /// </summary>
        private static string PerGameOverrideSuffix(SyncSummary summary)
        {
            if (summary.PlaytimeSkipped <= 0 && summary.AchievementsSkipped <= 0) return string.Empty;

            var parts = new List<string>();
            if (summary.PlaytimeSkipped > 0)
            {
                parts.Add("playtime skipped on " + summary.PlaytimeSkipped);
            }

            if (summary.AchievementsSkipped > 0)
            {
                parts.Add("achievements skipped on " + summary.AchievementsSkipped);
            }

            return "; per-game sync mode: " + string.Join(", ", parts) + " game(s)";
        }

        private void Notify(string id, string text, NotificationType type)
        {
            RunOnUi(() => PlayniteApi.Notifications.Add(id, text, type));
        }

        private void RunOnUi(Action action)
        {
            var ctx = _uiContext;
            if (ctx == null)
            {
                action();
                return;
            }

            ctx.Post(_ => action(), null);
        }

        private static string FormatMinutes(long seconds)
        {
            var minutes = seconds / 60;
            if (minutes >= 60)
            {
                return $"{minutes / 60}h {minutes % 60}m";
            }

            return $"{minutes}m";
        }
    }
}
