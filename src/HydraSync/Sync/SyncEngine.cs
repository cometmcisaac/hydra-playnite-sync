using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HydraSync.Achievements;
using HydraSync.Hydra;
using HydraSync.Integrations;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace HydraSync.Sync
{
    public class SyncSummary
    {
        public bool HydraDbFound;
        public int HydraGamesRead;
        public int Matched;
        public long PlaytimeAddedSeconds;
        public int PlaytimeRaisedCount;
        public int AchievementsWritten;
        public int AchievementGamesScanned;
        public int AchievementGamesWithFiles;
        public int AchievementGamesWithUnlocks;
        public bool PaUnavailable;
        public string Error;

        /// <summary>Achievement sets handed to Playnite Achievements without a restart.</summary>
        public int AchievementsApplied;

        /// <summary>Achievement sets that were merged with data Playnite Achievements had.</summary>
        public int AchievementsMerged;

        /// <summary>
        /// True when data was written but Playnite Achievements could not import it right away,
        /// so it will only be picked up on the next Playnite start.
        /// </summary>
        public bool PaImportPending;

        /// <summary>Games that only recorded an additive-playtime baseline this run.</summary>
        public int PlaytimeBaselinesSet;

        /// <summary>Matched games whose playtime was skipped due to a per-game override.</summary>
        public int PlaytimeSkipped;

        /// <summary>Matched games whose achievements were skipped due to a per-game override.</summary>
        public int AchievementsSkipped;

        /// <summary>
        /// Ids of the games whose playtime this run actually raised. The caller decides what
        /// to do with them (see <c>HowLongToBeatBridge</c> pushes) - the engine only reports.
        /// </summary>
        public readonly List<Guid> PlaytimeRaisedGameIds = new List<Guid>();
    }

    /// <summary>
    /// One sync pass: read Hydra DB → match against the Playnite library →
    /// apply replace-if-larger playtime → export achievement unlocks to PlayniteAchievements.
    /// Library scope is "sync existing games only": Hydra-only titles are never imported.
    /// </summary>
    public class SyncEngine
    {
        private readonly IPlayniteAPI _api;
        private readonly PluginSettings _settings;
        private readonly SyncState _state;
        private readonly string _statePath;
        private readonly string _schemaCacheDir;
        private readonly Func<string> _hydraDbPath;
        private readonly ILogger _log;
        private readonly Action<Action> _runOnUi;

        public SyncEngine(
            IPlayniteAPI api,
            PluginSettings settings,
            SyncState state,
            string statePath,
            string schemaCacheDir,
            Func<string> hydraDbPath,
            ILogger log,
            Action<Action> runOnUi)
        {
            _api = api;
            _settings = settings;
            _state = state;
            _statePath = statePath;
            _schemaCacheDir = schemaCacheDir;
            _hydraDbPath = hydraDbPath;
            _log = log;
            _runOnUi = runOnUi;
        }

        /// <param name="restrictToGames">
        /// When set, only these Playnite games are synced (one-shot "Sync … now" actions
        /// from a game's context menu). Full syncs pass null.
        /// </param>
        /// <param name="forcedMode">
        /// When set, the requested scope runs regardless of the global switches and the
        /// stored per-game override, and the skip counters stay at zero (nothing was
        /// skipped - the user asked for exactly this). Null = normal per-game mode handling.
        /// </param>
        public async Task<SyncSummary> RunAsync(
            ICollection<Guid> restrictToGames = null,
            GameSyncMode? forcedMode = null)
        {
            var summary = new SyncSummary();

            try
            {
                var dbPath = _hydraDbPath();
                summary.HydraDbFound = !string.IsNullOrEmpty(dbPath) && Directory.Exists(dbPath);
                if (!summary.HydraDbFound) return summary;

                var hydraGames = HydraDbReader.ReadGames(dbPath, _log);
                summary.HydraGamesRead = hydraGames.Count;
                if (hydraGames.Count == 0) return summary;

                var playniteGames = _api.Database.Games.ToList();
                var index = new MatchIndex(playniteGames);

                var changed = new Dictionary<Guid, Game>();
                var matched = new List<KeyValuePair<HydraGame, Game>>();

                foreach (var h in hydraGames)
                {
                    var game = index.Match(h);
                    if (game == null) continue;
                    if (restrictToGames != null && !restrictToGames.Contains(game.Id)) continue;

                    summary.Matched++;
                    matched.Add(new KeyValuePair<HydraGame, Game>(h, game));

                    bool doPlaytime;
                    bool doAchievements;
                    if (forcedMode.HasValue)
                    {
                        GameSyncModeLogic.ResolveForced(
                            forcedMode.Value, out doPlaytime, out doAchievements);
                    }
                    else
                    {
                        var mode = _state.GetGameMode(game.Id);
                        GameSyncModeLogic.Resolve(
                            _settings.SyncPlaytime, _settings.SyncAchievements, mode,
                            out doPlaytime, out doAchievements);

                        if (!doPlaytime && _settings.SyncPlaytime)
                        {
                            // Global switch is on, this game is set to achievements-only.
                            summary.PlaytimeSkipped++;
                        }

                        if (!doAchievements && _settings.SyncAchievements)
                        {
                            summary.AchievementsSkipped++;
                        }
                    }

                    if (doPlaytime)
                    {
                        ApplyPlaytime(h, game, changed, summary);
                    }
                }

                if (changed.Count > 0)
                {
                    var list = changed.Values.ToList();
                    _runOnUi(() => _api.Database.Games.Update(list));
                }

                if (matched.Count > 0)
                {
                    foreach (var pair in matched)
                    {
                        bool doAchievements;
                        if (forcedMode.HasValue)
                        {
                            GameSyncModeLogic.ResolveForced(
                                forcedMode.Value, out _, out doAchievements);
                        }
                        else
                        {
                            GameSyncModeLogic.Resolve(
                                _settings.SyncPlaytime, _settings.SyncAchievements,
                                _state.GetGameMode(pair.Value.Id),
                                out _, out doAchievements);
                        }

                        if (!doAchievements) continue;

                        await ProcessAchievementsAsync(pair.Key, pair.Value, summary);
                    }
                }

                _state.LastSyncUtc = DateTime.UtcNow;
                _state.Save(_statePath);
            }
            catch (Exception ex)
            {
                summary.Error = ex.Message;
                _log?.Error(ex, "HydraSync: sync run failed");
            }

            return summary;
        }

        // ---------- playtime ----------

        private void ApplyPlaytime(HydraGame h, Game g, Dictionary<Guid, Game> changed, SyncSummary summary)
        {
            var key = h.Key;
            _state.Games.TryGetValue(key, out var st);

            var hydraSecs = PlaytimeSyncLogic.HydraSeconds(h.PlayTimeInMilliseconds);
            var dirty = false;
            ulong newPlaytime = 0;

            if (_settings.PlaytimeMode == PlaytimeMode.AddHydraIncrements)
            {
                // Additive: only Hydra's gain since the last sync of this game is added, so an
                // already-synced game is never doubled. The first pass records a baseline.
                var plan = PlaytimeSyncLogic.PlanAdditive(
                    g.Playtime, h.PlayTimeInMilliseconds,
                    st?.PlaytimeBaselineHydraMs != null, st?.PlaytimeBaselineHydraMs ?? 0);

                if (plan.BaselineOnly)
                {
                    summary.PlaytimeBaselinesSet++;
                }

                if (plan.ShouldApply)
                {
                    newPlaytime = plan.NewPlaytime;
                    summary.PlaytimeAddedSeconds += plan.AddedSeconds;
                }
            }
            else if (PlaytimeSyncLogic.ShouldRaise(g.Playtime, hydraSecs))
            {
                // Replace-if-larger: Hydra's cumulative total replaces Playnite's playtime only
                // when it is strictly greater; otherwise Playnite's value stays untouched.
                newPlaytime = (ulong)hydraSecs;
                summary.PlaytimeAddedSeconds += hydraSecs - (long)g.Playtime;
            }

            if (newPlaytime > 0)
            {
                if (st == null || st.OriginalPlaytimeSecs == null)
                {
                    st = st ?? new HydraGameState();
                    // First modification. When no original was recorded but the entry does
                    // carry how much was added, recover the pre-plugin value from that.
                    var recordedAddedMs = st.LastHydraMs > 0 ? st.LastHydraMs + Math.Max(0, st.MsCarry) : 0;
                    st.OriginalPlaytimeSecs = PlaytimeSyncLogic.CaptureOriginal(g.Playtime, recordedAddedMs);
                }

                summary.PlaytimeRaisedCount++;
                summary.PlaytimeRaisedGameIds.Add(g.Id);
                st.LastAppliedPlaytimeSecs = newPlaytime;
                g.Playtime = newPlaytime;
                dirty = true;
            }

            // LastActivity moves forward to Hydra's last session (never backward).
            if (h.LastTimePlayed.HasValue &&
                (!g.LastActivity.HasValue || h.LastTimePlayed.Value > g.LastActivity.Value))
            {
                g.LastActivity = h.LastTimePlayed.Value;
                dirty = true;
            }

            st = st ?? new HydraGameState();
            st.PlayniteGameId = g.Id.ToString("D");
            // Additive mode needs Hydra's total as of this sync to compute the next delta.
            st.PlaytimeBaselineHydraMs = h.PlayTimeInMilliseconds;
            // LastHydraMs/MsCarry are no longer written; they are only read as a recorded
            // added amount so an entry without an original can still be undone.
            _state.Games[key] = st;

            if (dirty) changed[g.Id] = g;
        }

        // ---------- achievements ----------

        private async Task ProcessAchievementsAsync(HydraGame h, Game g, SyncSummary summary)
        {
            summary.AchievementGamesScanned++;

            // Candidate AppIDs: the Hydra objectId when it's a real Steam AppID, plus IDs
            // discovered from the game's own directory (steam_appid.txt / Goldberg dirs).
            // This lets shop=custom/launchbox entries sync achievements too. With no IDs the
            // game-dir formats (userstats/3DM/ali213) are still scanned — they're ID-independent.
            var executablePath = FirstNonEmpty(h.ExecutablePath, null);
            var ids = BuildAppIds(h, executablePath, g.InstallDirectory);

            var files = AchievementFileLocator.Find(ids, executablePath, g.InstallDirectory);
            if (files.Count == 0)
            {
                _log?.Debug($"HydraSync: {g.Name}: no achievement files " +
                    $"(ids: {(ids.Count > 0 ? string.Join(",", ids) : "none")}, " +
                    $"exe: {executablePath ?? "none"}, install: {g.InstallDirectory ?? "none"})");
                return;
            }
            summary.AchievementGamesWithFiles++;
            _log?.Debug($"HydraSync: {g.Name}: {files.Count} achievement file(s): " +
                string.Join("; ", files.Select(f => f.Type + "=" + f.Path)));

            // Union of unlocks across all found files (earliest time wins per achievement).
            var unlocks = CollectUnlocks(files);
            if (unlocks.Count == 0)
            {
                _log?.Debug($"HydraSync: {g.Name}: files found but no unlocks parsed");
                return;
            }
            summary.AchievementGamesWithUnlocks++;

            // AppID (for schema sources) — for non-steam Hydra entries the discovered ID
            // *is* the cracked game's real Steam AppID, so the schema is valid.
            var appId = 0;
            if (ids.Count > 0) int.TryParse(ids[0], out appId);

            // Schema chain: (1) local game-dir definitions (steam_settings/achievements.json —
            // exact API names matching the unlock file, offline), (2) Steam Web API when the
            // user provided a key, (3) store appdetails (legacy; now returns a highlighted-only
            // object for most apps, so usually a no-op).
            var schema = ResolveLocalSchema(executablePath, g.InstallDirectory, appId);
            if ((schema == null || !schema.HasAchievements) && _settings.FetchSteamSchema && appId <= 0)
            {
                // No AppID anywhere (a non-Steam Hydra entry with no steam_appid.txt in its
                // folder). Searching the store by title recovers names/icons for the games that
                // are on Steam; anything not close enough to the title is ignored.
                var byTitle = await SteamTitleSearchClient.FindAsync(
                    FirstNonEmpty(g.Name, h.Title), _schemaCacheDir, _log);
                if (byTitle != null && byTitle.AppId > 0)
                {
                    appId = byTitle.AppId;
                    schema = await FetchSchemaAsync(appId);
                }
            }
            else if ((schema == null || !schema.HasAchievements) && _settings.FetchSteamSchema)
            {
                schema = await FetchSchemaAsync(appId);
            }

            // Build the payload: schema order first (canonical names), then unlock-only extras.
            var details = new List<PaAchievement>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (schema != null && schema.HasAchievements)
            {
                foreach (var sa in schema.Achievements)
                {
                    if (!seen.Add(sa.Name)) continue;
                    unlocks.TryGetValue(sa.Name, out var timeMs);
                    details.Add(new PaAchievement
                    {
                        ApiName = sa.Name,
                        DisplayName = string.IsNullOrEmpty(sa.DisplayName) ? Prettify(sa.Name) : sa.DisplayName,
                        Description = sa.Description ?? "",
                        Hidden = sa.Hidden,
                        UnlockedIconPath = sa.IconUrl,
                        LockedIconPath = sa.IconGrayUrl,
                        Unlocked = unlocks.ContainsKey(sa.Name),
                        UnlockTimeUtc = ToUtc(timeMs),
                    });
                }
            }

            foreach (var kv in unlocks)
            {
                if (!seen.Add(kv.Key)) continue;
                details.Add(new PaAchievement
                {
                    ApiName = kv.Key,
                    DisplayName = Prettify(kv.Key),
                    Description = "",
                    Unlocked = true,
                    UnlockTimeUtc = ToUtc(kv.Value),
                });
            }

            if (details.Count == 0) return;

            // Provider key: "Steam" for steam-shop games so Playnite Achievements labels and
            // styles the data with its Steam source (unlock state still comes from Hydra);
            // "Manual" for everything else so PA keeps its manual-tracking features available
            // (any non-"Manual" key counts as "another provider owns this game" in PA).
            var isSteam = string.Equals(h.Shop, "steam", StringComparison.OrdinalIgnoreCase);
            var providerKey = isSteam ? "Steam" : "Manual";

            // Fingerprint is namespaced by provider key so a key change (e.g. legacy "Hydra"
            // data from v1.3) forces a one-time rewrite with the new classification.
            var fingerprint = providerKey + ":" + Fingerprint(details);

            // Unchanged since last write?
            _state.Games.TryGetValue(h.Key, out var st);
            if (st != null && st.AchievementFingerprint == fingerprint)
            {
                _log?.Debug($"HydraSync: {g.Name}: unlocks unchanged since last sync - skip");
                return;
            }

            if (!_settings.WriteToPlayniteAchievements)
            {
                _log?.Debug($"HydraSync: {g.Name}: 'Write to Playnite Achievements' is off - skip");
                return;
            }

            if (!PaCacheWriter.IsAvailable)
            {
                summary.PaUnavailable = true;
                return;
            }

            var providerGameKey = appId > 0 ? appId.ToString() : (FirstNonEmpty(h.ObjectId, h.Key) ?? "");

            // Merge with whatever Playnite Achievements already has for this game so a write
            // never throws away another provider's entries or an unlock it knows about.
            JObject existing = null;
            if (_settings.MergeWithPlayniteAchievements)
            {
                existing = PlayniteAchievementsBridge.TryReadGameData(g.Id, _log);
            }

            try
            {
                PaCacheWriter.Write(g, appId, providerGameKey, details, providerKey, existing, _log);
            }
            catch (Exception ex)
            {
                _log?.Error(ex, $"HydraSync: failed writing PA cache for {g.Name}");
                return;
            }

            if (existing != null)
            {
                summary.AchievementsMerged++;
            }

            // Import it right away instead of waiting for the next Playnite start. Falls back
            // silently: without the bridge the file is picked up at the next start, exactly as
            // before.
            if (_settings.ImportAchievementsImmediately &&
                PlayniteAchievementsBridge.ImportNow(g.Id, _runOnUi, _log) == PaImportResult.Imported)
            {
                summary.AchievementsApplied++;
            }
            else
            {
                summary.PaImportPending = true;
            }

            st = st ?? _state.GetOrAdd(h.Key);
            st.AchievementFingerprint = fingerprint;
            summary.AchievementsWritten++;
        }

        // ---------- helpers ----------

        /// <summary>
        /// Candidate AppIDs for achievement-file lookup: the Hydra objectId for steam-shop
        /// games, plus IDs discovered from the game directory. Shared by sync and diagnostics.
        /// </summary>
        internal static List<string> BuildAppIds(HydraGame h, string executablePath, string installDirectory)
        {
            var ids = new List<string>();
            if (string.Equals(h.Shop, "steam", StringComparison.OrdinalIgnoreCase) && h.HasNumericId)
                ids.Add(h.ObjectId);
            foreach (var id in AchievementFileLocator.DiscoverAppIds(executablePath, installDirectory))
            {
                if (!ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>Union of unlocks across achievement files (earliest unlock time wins).</summary>
        internal static Dictionary<string, long?> CollectUnlocks(IReadOnlyList<AchievementFile> files)
        {
            var unlocks = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                foreach (var a in AchievementParsers.ParseFile(file.Type, file.Path))
                {
                    if (!unlocks.TryGetValue(a.Name, out var existing))
                    {
                        unlocks[a.Name] = a.UnlockTimeMs;
                    }
                    else if (a.UnlockTimeMs.HasValue &&
                             (!existing.HasValue || a.UnlockTimeMs.Value < existing.Value))
                    {
                        unlocks[a.Name] = a.UnlockTimeMs;
                    }
                }
            }
            return unlocks;
        }

        /// <summary>
        /// Network schema sources for an AppID: the Steam Web API when the user provided a key
        /// (full schema), then the store appdetails API as a fallback.
        /// </summary>
        private async Task<SteamSchema> FetchSchemaAsync(int appId)
        {
            if (appId <= 0) return null;

            var schema = await SteamSchemaClient.GetFromWebApiAsync(
                appId, _settings.SteamWebApiKey, _schemaCacheDir, _log);
            if (schema == null || !schema.HasAchievements)
            {
                schema = await SteamSchemaClient.GetAsync(appId, _schemaCacheDir, _log);
            }

            return schema;
        }

        /// <summary>First parseable local definitions file (game-dir steam_settings), or null.</summary>
        private SteamSchema ResolveLocalSchema(string executablePath, string installDirectory, int appId)
        {
            foreach (var file in LocalAchievementDefinitions.FindFiles(executablePath, installDirectory))
            {
                var parsed = LocalAchievementDefinitions.Parse(file, appId);
                if (parsed != null)
                {
                    _log?.Debug($"HydraSync: local achievement definitions from {file} " +
                                $"({parsed.Achievements.Count} entries)");
                    return parsed;
                }
            }
            return null;
        }

        /// <summary>Playnite game → Hydra entry lookup (strong GameId match, then title).</summary>
        internal static HydraGame FindMatchForGame(IReadOnlyList<HydraGame> hydraGames, Game g)
        {
            foreach (var h in hydraGames)
            {
                if (h.HasNumericId && !string.IsNullOrEmpty(g.GameId) &&
                    string.Equals(h.ObjectId, g.GameId, StringComparison.Ordinal))
                {
                    return h;
                }
            }

            var target = MatchIndex.NormalizeTitle(g.Name);
            if (string.IsNullOrEmpty(target)) return null;
            foreach (var h in hydraGames)
            {
                if (!string.IsNullOrEmpty(h.Title) &&
                    string.Equals(MatchIndex.NormalizeTitle(h.Title), target, StringComparison.Ordinal))
                {
                    return h;
                }
            }
            return null;
        }

        private static DateTime? ToUtc(long? epochMs)
        {
            if (!epochMs.HasValue || epochMs.Value <= 0) return null;
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs.Value).UtcDateTime;
            }
            catch
            {
                return null;
            }
        }

        private static string Fingerprint(List<PaAchievement> details)
        {
            var parts = details
                .Select(d => d.ApiName + "|" + (d.Unlocked ? (d.UnlockTimeUtc?.Ticks.ToString() ?? "-") : "-"))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>"KILL_100_ENEMIES" → "Kill 100 Enemies" (fallback when no schema name exists).</summary>
        private static string Prettify(string apiName)
        {
            if (string.IsNullOrEmpty(apiName)) return apiName;
            var words = apiName.Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                var w = words[i];
                if (w.All(char.IsUpper) && w.Any(char.IsLetter))
                {
                    words[i] = char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant();
                }
                else
                {
                    words[i] = char.ToUpperInvariant(w[0]) + w.Substring(1);
                }
            }

            return string.Join(" ", words);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }

            return null;
        }

        /// <summary>
        /// Match strategy: numeric GameId == Hydra objectId first (strong), then
        /// normalized-title equality (weaker; prefers Steam-sourced, then most recent activity).
        /// </summary>
        internal class MatchIndex
        {
            private readonly Dictionary<string, List<Game>> _byGameId =
                new Dictionary<string, List<Game>>(StringComparer.Ordinal);

            private readonly Dictionary<string, List<Game>> _byTitle =
                new Dictionary<string, List<Game>>(StringComparer.OrdinalIgnoreCase);

            public MatchIndex(List<Game> games)
            {
                foreach (var g in games)
                {
                    if (!string.IsNullOrEmpty(g.GameId) && IsDigits(g.GameId))
                    {
                        if (!_byGameId.TryGetValue(g.GameId, out var list))
                        {
                            list = new List<Game>();
                            _byGameId[g.GameId] = list;
                        }

                        list.Add(g);
                    }

                    var title = NormalizeTitle(g.Name);
                    if (title.Length == 0) continue;
                    if (!_byTitle.TryGetValue(title, out var tlist))
                    {
                        tlist = new List<Game>();
                        _byTitle[title] = tlist;
                    }

                    tlist.Add(g);
                }
            }

            public Game Match(HydraGame h)
            {
                if (h.HasNumericId && _byGameId.TryGetValue(h.ObjectId, out var idMatches))
                {
                    return Best(idMatches);
                }

                var title = NormalizeTitle(h.Title);
                if (title.Length > 0 && _byTitle.TryGetValue(title, out var titleMatches))
                {
                    return Best(titleMatches);
                }

                return null;
            }

            private static Game Best(List<Game> candidates)
            {
                if (candidates.Count == 1) return candidates[0];

                return candidates
                    .OrderByDescending(g => string.Equals(g.Source?.Name, "Steam", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(g => g.LastActivity ?? DateTime.MinValue)
                    .First();
            }

            internal static string NormalizeTitle(string title)
            {
                if (string.IsNullOrEmpty(title)) return "";
                var s = title.ToLowerInvariant()
                    .Replace("®", "")
                    .Replace("™", "")
                    .Replace("©", "");

                var sb = new StringBuilder(s.Length);
                var lastWasSpace = true;
                foreach (var c in s)
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        sb.Append(c);
                        lastWasSpace = false;
                    }
                    else if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }

                return sb.ToString().Trim();
            }

            private static bool IsDigits(string s)
            {
                foreach (var c in s)
                {
                    if (!char.IsDigit(c)) return false;
                }

                return s.Length > 0;
            }
        }
    }
}
