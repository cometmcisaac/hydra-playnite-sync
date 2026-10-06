using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HydraSync.Sync;
using HydraSync.Hydra;
using HydraSync.Achievements;
using HltbResult = HydraSync.Integrations.HltbPushResult;

class Program
{
    static int _pass;
    static int _fail;

    static void Check(bool cond, string name, string detail = "")
    {
        if (cond) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {name} {detail}"); }
    }

    static string WriteFile(string dir, string rel, string content)
    {
        var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
        return path;
    }

    // Anchors on the repo root so the harness works from any working directory
    // (bin/, repo root, or an absolute path in CI).
    static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "HydraSync")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new DirectoryNotFoundException("Could not locate the repo root (no src/HydraSync above " + AppContext.BaseDirectory + ")");
        }

        return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    static int Main()
    {
        var fixture = RepoPath("tests/fixtures/hydra-db-fixture");
        var tmp = Path.Combine(Path.GetTempPath(), "hydrasync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        try
        {
            Console.WriteLine("== 1. LevelDB fixture read (classic-level, snappy SST + WAL) ==");
            var games = HydraDbReader.ReadGames(fixture, null);
            Check(games.Count == 62, "reads 62 game records", $"(got {games.Count})");
            Check(games.All(g => g.ObjectId != "auth"), "ignores non-game records");
            Check(games.All(g => g.ObjectId != "999999"), "skips isDeleted records");

            var cp = games.FirstOrDefault(g => g.ObjectId == "1091500");
            Check(cp != null && cp.Shop == "steam" && cp.PlayTimeInMilliseconds == 73842500,
                "parses steam game fields", cp == null ? "(missing)" : $"(shop={cp.Shop} ms={cp.PlayTimeInMilliseconds})");
            Check(cp?.LastTimePlayed != null, "parses ISO lastTimePlayed", $"({cp?.LastTimePlayed})");
            Check(cp?.LastTimePlayed?.Kind == DateTimeKind.Local, "lastTimePlayed converted to local",
                $"({cp?.LastTimePlayed?.Kind})");
            Check(cp?.UnlockedAchievementCount == 17 && cp?.AchievementCount == 44,
                "parses achievement counts");

            var custom = games.FirstOrDefault(g => g.ObjectId == "my-custom-game");
            Check(custom != null && custom.Shop == "custom" && custom.LastTimePlayed == null,
                "parses custom game with null lastTimePlayed");

            Console.WriteLine("== 2. Achievement parsers (per-format fixtures) ==");

            // codex (INI, Achieved/UnlockTime per section)
            var p = WriteFile(tmp, "codex.ini",
                "[Achievement_One]\r\nAchieved = 1\r\nUnlockTime = 1700000000\r\n" +
                "[Achievement_Two]\r\nAchieved = 0\r\nUnlockTime = 1600000000\r\n");
            var r = AchievementParsers.ParseFile("codex", p);
            Check(r.Count == 1 && r[0].Name == "Achievement_One" && r[0].UnlockTimeMs == 1700000000000L,
                "codex: only Achieved=1 parsed", $"(got {r.Count}: {string.Join(",", r.Select(x => x.Name + "@" + x.UnlockTimeMs))})");

            // rune uses same format - spot check dispatch
            r = AchievementParsers.ParseFile("rune", p);
            Check(r.Count == 1, "rune: dispatches to default parser");

            // goldberg (JSON array)
            p = WriteFile(tmp, "goldberg.json",
                "[{\"name\":\"ACH_A\",\"earned\":true,\"earned_time\":1710000000},{\"name\":\"ACH_B\",\"earned\":false,\"earned_time\":0}]");
            r = AchievementParsers.ParseFile("goldberg", p);
            Check(r.Count == 1 && r[0].Name == "ACH_A" && r[0].UnlockTimeMs == 1710000000000L,
                "goldberg array: earned only");

            // goldberg (JSON map)
            p = WriteFile(tmp, "goldberg2.json", "{\"ACH_M\":{\"earned\":true,\"earned_time\":1710000111}}");
            r = AchievementParsers.ParseFile("goldberg", p);
            Check(r.Count == 1 && r[0].Name == "ACH_M", "goldberg map variant");

            // empress → goldberg parser
            p = WriteFile(tmp, "empress.json", "[{\"name\":\"EMP\",\"earned\":1,\"earned_time\":1710000222}]");
            r = AchievementParsers.ParseFile("empress", p);
            Check(r.Count == 1 && r[0].Name == "EMP", "empress: json goldberg format");

            // skidrow (INI, [Achievements] name=1@time)
            p = WriteFile(tmp, "skidrow.ini",
                "[Achievements]\r\nSK_ONE=1@1700000123\r\nSK_TWO=0@1700000000\r\n");
            r = AchievementParsers.ParseFile("skidrow", p);
            Check(r.Count == 1 && r[0].Name == "SK_ONE" && r[0].UnlockTimeMs == 1700000123000L,
                "skidrow: 1@timestamp format");

            // 3DM (INI State/Time hex LE)
            p = WriteFile(tmp, "3dm.ini",
                "[State]\r\nACH1=0101\r\nACH2=0001\r\n[Time]\r\nACH1=00F15365\r\nACH2=00F15365\r\n");
            r = AchievementParsers.ParseFile("3dm", p);
            Check(r.Count == 1 && r[0].Name == "ACH1" && r[0].UnlockTimeMs == 1700000000000L,
                "3dm: state 0101 + LE hex time", $"(got {r.Count}: {string.Join(",", r.Select(x => x.Name + "@" + x.UnlockTimeMs))})");

            // ali213 (HaveAchieved / HaveAchievedTime)
            p = WriteFile(tmp, "ali213.ini",
                "[ACH_X]\r\nHaveAchieved=1\r\nHaveAchievedTime=1700000000\r\n");
            r = AchievementParsers.ParseFile("ali213", p);
            Check(r.Count == 1 && r[0].Name == "ACH_X" && r[0].UnlockTimeMs == 1700000000000L,
                "ali213: HaveAchieved format");

            // userstats (unlocked=true, time=)
            p = WriteFile(tmp, "userstats.ini",
                "[ACHIEVEMENTS]\r\n\"US_A\"=\"unlocked=true, time=1700000500\"\r\nUS_B=\"unlocked=false, time=1700000600\"\r\n");
            r = AchievementParsers.ParseFile("userstats", p);
            Check(r.Count == 1 && r[0].Name == "US_A" && r[0].UnlockTimeMs == 1700000500000L,
                "userstats: unlocked=true regex", $"(got {r.Count}: {string.Join(",", r.Select(x => x.Name))})");

            // rld (INI State/Time hex LE, skip [Steam])
            p = WriteFile(tmp, "rld.ini",
                "[ACH_R]\r\nState=01000000\r\nTime=00F15365\r\n[Steam]\r\nState=01000000\r\nTime=00F15365\r\n");
            r = AchievementParsers.ParseFile("rld", p);
            Check(r.Count == 1 && r[0].Name == "ACH_R" && r[0].UnlockTimeMs == 1700000000000L,
                "rld: state=1, Steam section skipped", $"(got {r.Count}: {string.Join(",", r.Select(x => x.Name))})");

            // onlinefix (achieved=true/timestamp + Achieved/TimeUnlocked)
            p = WriteFile(tmp, "of1.ini",
                "[ACH_O]\r\nachieved=true\r\ntimestamp=1700000000\r\n");
            r = AchievementParsers.ParseFile("onlinefix", p);
            Check(r.Count == 1 && r[0].UnlockTimeMs == 1700000000000L, "onlinefix: achieved/timestamp");

            p = WriteFile(tmp, "of2.ini",
                "[ACH_O2]\r\nAchieved=true\r\nTimeUnlocked=1700000123\r\n");
            r = AchievementParsers.ParseFile("onlinefix", p);
            Check(r.Count == 1 && r[0].UnlockTimeMs == 1700000123000L, "onlinefix: Achieved/TimeUnlocked");

            // 7-digit microsecond TimeUnlocked (Hydra: length==7 → value * 1000 * 1000)
            p = WriteFile(tmp, "of3.ini",
                "[ACH_O3]\r\nAchieved=true\r\nTimeUnlocked=1700000\r\n");
            r = AchievementParsers.ParseFile("onlinefix", p);
            Check(r.Count == 1 && r[0].UnlockTimeMs == 1700000000000L, "onlinefix: 7-digit µs time", $"({r.FirstOrDefault()?.UnlockTimeMs})");

            // creamapi
            p = WriteFile(tmp, "cream.cfg",
                "[ACH_C]\r\nachieved=true\r\nunlocktime=1700000200\r\n");
            r = AchievementParsers.ParseFile("creamapi", p);
            Check(r.Count == 1 && r[0].UnlockTimeMs == 1700000200000L, "creamapi: achieved/unlocktime");

            // razor1911 (raw lines "name 1 time")
            p = WriteFile(tmp, "razor", "ACH_Z 1 1700000300\r\nACH_Y 0 1700000400\r\n");
            r = AchievementParsers.ParseFile("razor1911", p);
            Check(r.Count == 1 && r[0].Name == "ACH_Z" && r[0].UnlockTimeMs == 1700000300000L,
                "razor1911: line format");

            // steam librarycache (vecHighlight)
            p = WriteFile(tmp, "steam.json",
                "[[\"achievements\",{\"data\":{\"vecHighlight\":[{\"bAchieved\":true,\"strID\":\"ST_A\",\"rtUnlocked\":1700000400},{\"bAchieved\":false,\"strID\":\"ST_B\",\"rtUnlocked\":0}]}}],[\"other\",1]]");
            r = AchievementParsers.ParseFile("steam", p);
            Check(r.Count == 1 && r[0].Name == "ST_A" && r[0].UnlockTimeMs == 1700000400000L,
                "steam cache: vecHighlight");

            // garbage file → no crash, empty result
            p = WriteFile(tmp, "garbage.ini", "[\r\nnot=an=ini\r\n=emptyname\r\n");
            r = AchievementParsers.ParseFile("codex", p);
            Check(r != null, "malformed file handled gracefully");

            Console.WriteLine("== 3. Game-directory file discovery ==");
            var gdir = Path.Combine(tmp, "mygame");
            WriteFile(gdir, "SteamData/user_stats.ini",
                "[ACHIEVEMENTS]\r\nG_DIR_A=\"unlocked=true, time=1700000700\"\r\n");
            WriteFile(gdir, "3DMGAME/SomeProfile/stats/achievements.ini",
                "[State]\r\nG_3DM=0101\r\n[Time]\r\nG_3DM=00F15365\r\n");
            WriteFile(gdir, "Profile/P1/Stats/Achievements.Bin",
                "[ACH_A213]\r\nHaveAchieved=1\r\nHaveAchievedTime=1700000800\r\n");
            WriteFile(gdir, "steam_settings/1091500/achievements.json",
                "[{\"name\":\"GOLD\",\"earned\":true,\"earned_time\":1700000900}]");

            var exePath = Path.Combine(gdir, "bin", "x64", "game.exe"); // nested exe dir
            WriteFile(gdir, "bin/x64/game.exe", ""); // file itself (existence not required, but harmless)
            WriteFile(gdir, "steam_api64.dll", "");

            var found = AchievementFileLocator.Find("1091500", exePath, gdir);
            var types = found.Select(f => f.Type).Distinct().OrderBy(t => t).ToArray();
            Check(types.Contains("userstats"), "finds SteamData/user_stats.ini", $"(types: {string.Join(",", types)})");
            Check(types.Contains("3dm"), "finds 3DMGAME profile stats");
            Check(types.Contains("ali213"), "finds Profile Stats/Achievements.Bin");
            Check(types.Contains("goldberg"), "finds steam_settings/<id>/achievements.json");

            // every located file parses without error
            var totalUnlocks = found.Sum(f => AchievementParsers.ParseFile(f.Type, f.Path).Count);
            Check(totalUnlocks == 4, "all located files parse to 1 unlock each", $"(total {totalUnlocks})");

            // Dishonored alt-ID mapping still returns the primary id at minimum
            var alts = AchievementFileLocator.AlternativeObjectIds("205100").ToArray();
            Check(alts.Length == 3 && alts.Contains("217980"), "Dishonored alt AppIDs");

            // ---- multi-ID / no-ID Find (non-steam support) ----
            var noId = AchievementFileLocator.Find((string)null, exePath, gdir);
            var noIdTypes = noId.Select(f => f.Type).Distinct().ToArray();
            Check(noIdTypes.Contains("userstats") && noIdTypes.Contains("3dm") &&
                  noIdTypes.Contains("ali213") && noIdTypes.Contains("goldberg"),
                "Find(null) still scans game-dir formats", $"({string.Join(",", noIdTypes)})");

            var nonNumeric = AchievementFileLocator.Find("launchbox-42", exePath, gdir);
            Check(nonNumeric.Count == 4, "non-numeric id → game-dir files only, no static junk",
                $"({nonNumeric.Count})");

            var multi = AchievementFileLocator.Find(new[] { "1091500", "999999999" }, exePath, gdir);
            Check(multi.Count == 4, "multi-id Find dedupes game-dir scan", $"({multi.Count})");

            // ---- AppID discovery from the game directory ----
            WriteFile(gdir, "steam_appid.txt", "480\r\n");
            var ids = AchievementFileLocator.DiscoverAppIds(exePath, gdir);
            Check(ids.Contains("480"), "discovers steam_appid.txt at base", $"({string.Join(",", ids)})");
            Check(ids.Contains("1091500"), "discovers numeric steam_settings/<id> dir", $"({string.Join(",", ids)})");
            Check(!ids.Contains(""), "no empty ids discovered");

            Console.WriteLine("== 4. Hydra date edge cases ==");
            var d1 = HydraGame.ParseDate(Newtonsoft.Json.Linq.JToken.Parse("\"2026-09-20T18:30:00.000Z\""));
            Check(d1 != null && d1.Value.Kind == DateTimeKind.Local, "ISO Z → local", $"({d1}, {d1?.Kind})");
            var d2 = HydraGame.ParseDate(Newtonsoft.Json.Linq.JToken.Parse("1712345678000"));
            Check(d2 != null, "epoch ms number → local", $"({d2})");
            var d3 = HydraGame.ParseDate(Newtonsoft.Json.Linq.JValue.CreateNull());
            Check(d3 == null, "null → null");

            Console.WriteLine("== 5. Playtime replace-if-larger + undo ==");
            Check(HydraSync.Sync.PlaytimeSyncLogic.HydraSeconds(1500) == 1, "HydraSeconds floors to whole seconds");
            Check(HydraSync.Sync.PlaytimeSyncLogic.HydraSeconds(999) == 0, "HydraSeconds sub-second → 0");
            Check(HydraSync.Sync.PlaytimeSyncLogic.HydraSeconds(-5) == 0, "HydraSeconds negative → 0");

            Check(PlaytimeSyncLogic.ShouldRaise(100, 101), "hydra > playnite → replace");
            Check(!PlaytimeSyncLogic.ShouldRaise(100, 100), "hydra == playnite → keep playnite");
            Check(!PlaytimeSyncLogic.ShouldRaise(100, 99), "hydra < playnite → keep playnite");
            Check(PlaytimeSyncLogic.ShouldRaise(0, 1), "hydra > 0 on empty playtime → replace");
            Check(!PlaytimeSyncLogic.ShouldRaise(100, 0), "hydra 0 → never replace");

            Check(PlaytimeSyncLogic.CaptureOriginal(500, 0) == 500, "capture original (fresh entry) = current");
            Check(PlaytimeSyncLogic.CaptureOriginal(1300, 800_000) == 500, "capture original (legacy additive) subtracts 800s added");
            Check(PlaytimeSyncLogic.CaptureOriginal(500, 800_000) == 500, "capture original: impossible recovery falls back to current");

            ulong rest = PlaytimeSyncLogic.Restore(9999, 123, 0, out var approx);
            Check(rest == 123 && !approx, "undo: exact original wins");
            rest = PlaytimeSyncLogic.Restore(1300, null, 800_000, out approx);
            Check(rest == 500 && approx, "undo: legacy additive recovered approximately", $"(got {rest}, approx={approx})");
            rest = PlaytimeSyncLogic.Restore(800, null, 800_000, out approx);
            Check(rest == 0 && approx, "undo: playtime fully from plugin → restored to 0");
            rest = PlaytimeSyncLogic.Restore(900, null, 0, out approx);
            Check(rest == 900 && !approx, "undo: no bookkeeping → unchanged");
            rest = PlaytimeSyncLogic.Restore(500, null, 800_000, out approx);
            Check(rest == 500 && !approx, "undo: legacy added > playtime → unchanged (no negative)");

            Console.WriteLine();
            Console.WriteLine("== 6. Local achievement definitions (Steam schema from game dir) ==");

            // Hydra metadata exporter format: array w/ displayName, relative icons, hidden 0|1.
            var defPath = WriteFile(tmp, "ss_ach/achievements.json",
                "[{\"name\":\"ACH_ONE\",\"displayName\":\"First Blood\",\"description\":\"Kill someone\",\"hidden\":0," +
                "\"icon\":\"images/1.jpg\",\"icongray\":\"images/1_gray.jpg\"}," +
                "{\"name\":\"ACH_TWO\",\"displayName\":\"Double Kill\",\"description\":\"\",\"hidden\":1," +
                "\"icon\":\"https://cdn.example/i2.jpg\"}]");
            var schema = LocalAchievementDefinitions.Parse(defPath, 2215200);
            Check(schema != null && schema.HasAchievements && schema.Achievements.Count == 2,
                "hydra array format parses 2 entries");
            var a1 = schema?.Achievements?.FirstOrDefault(x => x.Name == "ACH_ONE");
            Check(a1 != null && a1.DisplayName == "First Blood" && a1.Description == "Kill someone" && !a1.Hidden,
                "entry: display name + description + hidden=0", $"({a1?.DisplayName}, hidden={a1?.Hidden})");
            Check(a1 != null && a1.IconUrl != null && Path.IsPathRooted(a1.IconUrl) &&
                    a1.IconUrl.EndsWith(Path.Combine("ss_ach", "images", "1.jpg")),
                "relative icon resolved to absolute path under steam_settings", $"({a1?.IconUrl})");
            var a2 = schema?.Achievements?.FirstOrDefault(x => x.Name == "ACH_TWO");
            Check(a2 != null && a2.Hidden, "hidden=1 (numeric) parsed as hidden");
            Check(a2 != null && a2.IconUrl == "https://cdn.example/i2.jpg", "http icon passes through unchanged");

            // Goldberg per-user unlock-state file must NOT be read as definitions.
            var usPath = WriteFile(tmp, "ss_ach/123/achievements.json",
                "[{\"name\":\"ACH_ONE\",\"earned\":1,\"earned_time\":1700000000}]");
            Check(LocalAchievementDefinitions.Parse(usPath, 2215200) == null,
                "unlock-state array (earned/earned_time) → null");

            // Keyed object format: api name comes from the property key.
            var koPath = WriteFile(tmp, "ko.json",
                "{\"ACH_K\":{\"displayName\":\"Keyed Name\",\"description\":\"Keyed desc\",\"hidden\":false}}");
            var s3 = LocalAchievementDefinitions.Parse(koPath, 1);
            Check(s3 != null && s3.Achievements.Count == 1 && s3.Achievements[0].Name == "ACH_K" &&
                    s3.Achievements[0].DisplayName == "Keyed Name" && s3.Achievements[0].Description == "Keyed desc",
                "keyed object format → api name from key");

            // Array entry without displayName falls back to the raw name.
            var ndPath = WriteFile(tmp, "nd.json",
                "[{\"name\":\"RAW_NAME\",\"description\":\"d\",\"icon\":\"x.jpg\"}]");
            var s5 = LocalAchievementDefinitions.Parse(ndPath, 1);
            Check(s5 != null && s5.Achievements[0].DisplayName == "RAW_NAME",
                "missing displayName → falls back to name");

            // FindFiles locates only <steam_settings>/achievements.json, never digit subdirs.
            var gameDir = Path.Combine(tmp, "game");
            Directory.CreateDirectory(Path.Combine(gameDir, "steam_settings", "999"));
            File.WriteAllText(Path.Combine(gameDir, "steam_settings", "achievements.json"), "[]");
            File.WriteAllText(Path.Combine(gameDir, "steam_settings", "999", "achievements.json"),
                "[{\"name\":\"X\",\"earned\":1}]");
            var defFiles = LocalAchievementDefinitions.FindFiles(Path.Combine(gameDir, "game.exe"), gameDir);
            Check(defFiles.Any(f => f.EndsWith("achievements.json") && !f.Contains("999")),
                "FindFiles → steam_settings root only", $"({defFiles.Count})");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("== 7. Update checker (version parsing/comparison) ==");
        {
            var t1 = HydraSync.Update.UpdateChecker.ParseTag("v1.5.0");
            Check(t1 != null && t1.Major == 1 && t1.Minor == 5 && t1.Build == 0, "ParseTag strips v prefix");
            var t2 = HydraSync.Update.UpdateChecker.ParseTag("1.4.1");
            Check(t2 != null && t2.ToString() == "1.4.1", "ParseTag handles bare version");
            Check(HydraSync.Update.UpdateChecker.ParseTag("not-a-version") == null, "ParseTag junk -> null");
            Check(HydraSync.Update.UpdateChecker.ParseTag("") == null, "ParseTag empty -> null");

            var m1 = HydraSync.Update.UpdateChecker.ParseManifestVersion(
                "Id: X\nName: Hydra Sync\nVersion: 1.4.1\nModule: HydraSync.dll\n");
            Check(m1 != null && m1.ToString() == "1.4.1", "ParseManifestVersion reads Version line");
            Check(HydraSync.Update.UpdateChecker.ParseManifestVersion("no version here") == null,
                "ParseManifestVersion missing line -> null");

            var latest = new Version(1, 5, 0);
            var installed = new Version(1, 4, 1);
            Check(latest > installed, "1.5.0 > 1.4.1 -> update available");
            Check(!(installed > latest), "1.4.1 not > 1.5.0 -> no update");

            // GetLocalVersion must not throw when extension.yaml is absent next to the test assembly.
            try
            {
                var lv = HydraSync.Update.UpdateChecker.GetLocalVersion();
                Check(true, "GetLocalVersion null-safe", $"(got {lv})");
            }
            catch (Exception ex)
            {
                Check(false, "GetLocalVersion null-safe", $"({ex.GetType().Name})");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== 8. Per-game sync modes ==");

        {
            bool pt, ach;

            HydraSync.Sync.GameSyncModeLogic.Resolve(true, true, HydraSync.Sync.GameSyncMode.Both, out pt, out ach);
            Check(pt && ach, "Both + globals on -> playtime+achievements");

            HydraSync.Sync.GameSyncModeLogic.Resolve(true, true, HydraSync.Sync.GameSyncMode.PlaytimeOnly, out pt, out ach);
            Check(pt && !ach, "PlaytimeOnly + globals on -> playtime only");

            HydraSync.Sync.GameSyncModeLogic.Resolve(true, true, HydraSync.Sync.GameSyncMode.AchievementsOnly, out pt, out ach);
            Check(!pt && ach, "AchievementsOnly + globals on -> achievements only");

            // Global switches remain master switches: a per-game mode can only narrow.
            HydraSync.Sync.GameSyncModeLogic.Resolve(false, true, HydraSync.Sync.GameSyncMode.Both, out pt, out ach);
            Check(!pt && ach, "global playtime off -> playtime stays off in Both mode");

            HydraSync.Sync.GameSyncModeLogic.Resolve(true, false, HydraSync.Sync.GameSyncMode.PlaytimeOnly, out pt, out ach);
            Check(pt && !ach, "global achievements off -> achievements stay off in PlaytimeOnly mode");

            HydraSync.Sync.GameSyncModeLogic.Resolve(false, false, HydraSync.Sync.GameSyncMode.AchievementsOnly, out pt, out ach);
            Check(!pt && !ach, "both globals off -> nothing syncs even with AchievementsOnly override");

            Check(
                HydraSync.Sync.GameSyncModeLogic.Describe(HydraSync.Sync.GameSyncMode.Both) == "playtime and achievements" &&
                HydraSync.Sync.GameSyncModeLogic.Describe(HydraSync.Sync.GameSyncMode.PlaytimeOnly) == "playtime only" &&
                HydraSync.Sync.GameSyncModeLogic.Describe(HydraSync.Sync.GameSyncMode.AchievementsOnly) == "achievements only",
                "Describe() labels all three modes");
        }

        Console.WriteLine();
        Console.WriteLine("== 9. Forced one-shot per-game sync ==");

        HydraSync.Sync.GameSyncModeLogic.ResolveForced(HydraSync.Sync.GameSyncMode.Both, out var pt9, out var ach9);
        Check(pt9 && ach9, "forced Both -> playtime and achievements");

        HydraSync.Sync.GameSyncModeLogic.ResolveForced(HydraSync.Sync.GameSyncMode.PlaytimeOnly, out pt9, out ach9);
        Check(pt9 && !ach9, "forced PlaytimeOnly -> playtime only");

        HydraSync.Sync.GameSyncModeLogic.ResolveForced(HydraSync.Sync.GameSyncMode.AchievementsOnly, out pt9, out ach9);
        Check(!pt9 && ach9, "forced AchievementsOnly -> achievements only");

        Console.WriteLine();
        Console.WriteLine("== 10. HowLongToBeat bridge ==");

        var bridge = typeof(HydraSync.Integrations.HowLongToBeatBridge);
        var db = HowLongToBeat.HowLongToBeat.PluginDatabase;

        void Reset()
        {
            db.LoggedIn = true;
            db.ThrowOnPush = false;
            db.PushReturnsFalse = false;
            db.LinkedDataMissing = false;
            db.Ignored.Clear();
            HowLongToBeat.FakeHltbCustomPlugin.PluginDatabase.Calls = 0;
            bridge.GetField("AssemblyResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.SetValue(null, new System.Func<string, System.Reflection.Assembly>(
                    _ => typeof(HowLongToBeat.HowLongToBeat).Assembly));
            bridge.GetField("PluginTypeResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.SetValue(null, new System.Func<System.Reflection.Assembly, Type>(
                    a => a.GetType("HowLongToBeat.HowLongToBeat", false)));
            bridge.GetMethod("ResetForTests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, null);
        }

        var game = new HowLongToBeat.FakeGame { Id = Guid.NewGuid(), Name = "Test Game" };

        // Not installed at all.
        Reset();
        bridge.GetField("AssemblyResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, new System.Func<string, System.Reflection.Assembly>(_ => null));
        // With an `out` parameter, reflection Invoke returns the result value itself and
        // copies the out value back into the argument array.
        var push = bridge.GetMethod(
            "PushPlaytime",
            new[] { typeof(Guid), typeof(object), typeof(string).MakeByRefType(), typeof(bool), typeof(Playnite.SDK.ILogger) });

        HltbResult Call(object target)
        {
            var args = new object[] { game.Id, target, null, true, null };
            var result = (HltbResult)push.Invoke(null, args);
            args[2] = null;
            return result;
        }

        Check((bridge.GetProperty("IsAvailable").GetValue(null) as bool?) == false, "unavailable when the extension is not loaded");
        Check(Call(game) == HydraSync.Integrations.HltbPushResult.Unavailable,
            "push reports Unavailable when the extension is missing");

        // Installed and logged in.
        Reset();
        bridge.GetField("AssemblyResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, new System.Func<string, System.Reflection.Assembly>(_ => typeof(HowLongToBeat.HowLongToBeat).Assembly));
        Check((bridge.GetProperty("IsAvailable").GetValue(null) as bool?) == true, "available once the assembly is loaded");
        Check((bridge.GetProperty("IsLoggedIn").GetValue(null) as bool?) == true, "login state read from the extension");

        HltbResult Result() => Call(game);

        Check(Result() == HydraSync.Integrations.HltbPushResult.Updated, "logged-in push updates");
        Check(db.PushCount == 1 && db.LastGame == game, "the game object is handed to HowLongToBeat");
        Check(db.LastNoPlaying, "noPlaying is sent so the game is not marked as playing");
        Check(!db.LastIsCompleted, "other flags keep the extension's own defaults");

        Check((bridge.GetMethod("HasData").Invoke(null, new object[] { game.Id }) as bool?) == true, "HasData true when linked");
        Check((bridge.GetMethod("IsIgnored", new[] { typeof(object) }).Invoke(null, new object[] { game }) as bool?) == false, "not ignored by default");

        // Signed out.
        db.LoggedIn = false;
        Check(Result() == HydraSync.Integrations.HltbPushResult.NotLoggedIn, "signed-out push is skipped");
        db.LoggedIn = true;

        // Ignored by HowLongToBeat's own ignore tag.
        db.Ignored.Add(game.Name);
        Check(Result() == HydraSync.Integrations.HltbPushResult.Ignored, "ignored game is skipped");
        Check((bridge.GetMethod("IsIgnored", new[] { typeof(object) }).Invoke(null, new object[] { game }) as bool?) == true, "IsIgnored reflects the tag");
        db.Ignored.Clear();

        // No linked HLTB data.
        db.LinkedDataMissing = true;
        Check(Result() == HydraSync.Integrations.HltbPushResult.NoData, "game without HLTB data is skipped");
        db.LinkedDataMissing = false;

        // Extension refuses.
        db.PushReturnsFalse = true;
        Check(Result() == HydraSync.Integrations.HltbPushResult.Failed, "refused push is a failure");
        db.PushReturnsFalse = false;

        // Extension throws.
        db.ThrowOnPush = true;
        Check(Result() == HydraSync.Integrations.HltbPushResult.Failed, "throwing push is a failure, not an exception");
        db.ThrowOnPush = false;

        // Bulk push with cap + skipping.
        var g2 = new HowLongToBeat.FakeGame { Id = Guid.NewGuid(), Name = "Second" };
        var g3 = new HowLongToBeat.FakeGame { Id = Guid.NewGuid(), Name = "Third" };
        db.Ignored.Add(g2.Name);
        var bulk = (HydraSync.Integrations.HltbPushReport)bridge
            .GetMethod("PushPlaytime", new[] { typeof(IEnumerable<(Guid, object)>), typeof(int), typeof(Playnite.SDK.ILogger) })
            .Invoke(null, new object[] { new[] { (game.Id, (object)game), (g2.Id, (object)g2), (g3.Id, (object)g3) }, 2, null });
        // game -> updated, g2 -> ignored (skipped), g3 -> over the cap (skipped).
        Check(bulk.Updated == 1 && bulk.Skipped == 2 && bulk.Failed == 0, "bulk push reports updated + skipped");
        Check(bulk.Details.Count == 3, "bulk push records a line per game");

        // A changed signature (extra required parameter, no noPlaying flag) still binds and
        // fills the unknown parameter with its type default instead of guessing.
        Reset();
        bridge.GetField("AssemblyResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, new System.Func<string, System.Reflection.Assembly>(_ => typeof(HowLongToBeat.FakeHltbCustomPlugin).Assembly));
        bridge.GetField("PluginTypeResolver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, new System.Func<System.Reflection.Assembly, Type>(_ => typeof(HowLongToBeat.FakeHltbCustomPlugin)));
        var customDb = HowLongToBeat.FakeHltbCustomPlugin.PluginDatabase;
        var customPush = bridge.GetMethod(
            "PushPlaytime",
            new[] { typeof(Guid), typeof(object), typeof(string).MakeByRefType(), typeof(bool), typeof(Playnite.SDK.ILogger) });
        var customArgs = new object[] { game.Id, game, null, true, null };
        var customResult = (HltbResult)customPush.Invoke(null, customArgs);
        Check(customResult == HydraSync.Integrations.HltbPushResult.Updated && customDb.Calls == 1 && customDb.LastOptions == 0,
            "a changed HowLongToBeat signature still binds (unknown params defaulted)");

        // Report description.
        Check(
            HydraSync.Integrations.HowLongToBeatBridge.Describe(new HydraSync.Integrations.HltbPushReport
            {
                Updated = 2,
                Skipped = 1,
                Failed = 0,
            }) == "updated 2, skipped 1",
            "Describe() summarizes the report");
        Check(
            HydraSync.Integrations.HowLongToBeatBridge.Describe(new HydraSync.Integrations.HltbPushReport
            {
                Unavailable = true,
                UnavailableReason = "not installed",
            }) == "not installed",
            "Describe() surfaces the unavailable reason");

        Console.WriteLine();
        Console.WriteLine($"RESULT: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
