using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace HydraSync.Hydra
{
    /// <summary>A discoverable achievement/unlock file.</summary>
    public class AchievementFile
    {
        /// <summary>Cracker type key matching AchievementParsers dispatch (e.g. "codex").</summary>
        public string Type { get; set; }
        public string Path { get; set; }
    }

    /// <summary>
    /// Port of Hydra's find-achievement-files.ts (static cracker paths),
    /// scan-game-directory-achievement-files.ts (in-game-dir formats) and
    /// game-directory.ts (game root / emulator directory resolution).
    /// </summary>
    public static class AchievementFileLocator
    {
        private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string PublicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);

        private class FileSpec
        {
            public string Type;
            public string Folder;
            public string[] Segments;
        }

        private static readonly FileSpec[] StaticSpecs =
        {
            new FileSpec { Type = "codex", Folder = ProgramData, Segments = new[] { "Steam", "CODEX", "<objectId>", "achievements.ini" } },
            new FileSpec { Type = "codex", Folder = AppData, Segments = new[] { "Steam", "CODEX", "<objectId>", "achievements.ini" } },
            new FileSpec { Type = "rune", Folder = ProgramData, Segments = new[] { "Steam", "RUNE", "<objectId>", "achievements.ini" } },
            new FileSpec { Type = "onlinefix", Folder = ProgramData, Segments = new[] { "OnlineFix", "<objectId>", "Stats", "Achievements.ini" } },
            new FileSpec { Type = "onlinefix", Folder = ProgramData, Segments = new[] { "OnlineFix", "<objectId>", "Achievements.ini" } },
            new FileSpec { Type = "goldberg", Folder = AppData, Segments = new[] { "Goldberg SteamEmu Saves", "<objectId>", "achievements.json" } },
            new FileSpec { Type = "goldberg", Folder = AppData, Segments = new[] { "GSE Saves", "<objectId>", "achievements.json" } },
            new FileSpec { Type = "rld", Folder = ProgramData, Segments = new[] { "RLD!", "<objectId>", "achievements.ini" } },
            new FileSpec { Type = "rld", Folder = ProgramData, Segments = new[] { "Steam", "Player", "<objectId>", "stats", "achievements.ini" } },
            new FileSpec { Type = "rld", Folder = ProgramData, Segments = new[] { "Steam", "RLD!", "<objectId>", "stats", "achievements.ini" } },
            new FileSpec { Type = "rld", Folder = ProgramData, Segments = new[] { "Steam", "dodi", "<objectId>", "stats", "achievements.ini" } },
            new FileSpec { Type = "empress", Folder = AppData, Segments = new[] { "EMPRESS", "remote", "<objectId>", "achievements.json" } },
            new FileSpec { Type = "empress", Folder = PublicDocuments, Segments = new[] { "EMPRESS", "<objectId>", "remote", "<objectId>", "achievements.json" } },
            new FileSpec { Type = "skidrow", Folder = Documents, Segments = new[] { "SKIDROW", "<objectId>", "SteamEmu", "UserStats", "achiev.ini" } },
            new FileSpec { Type = "skidrow", Folder = Documents, Segments = new[] { "Player", "<objectId>", "SteamEmu", "UserStats", "achiev.ini" } },
            new FileSpec { Type = "skidrow", Folder = LocalAppData, Segments = new[] { "SKIDROW", "<objectId>", "SteamEmu", "UserStats", "achiev.ini" } },
            new FileSpec { Type = "creamapi", Folder = AppData, Segments = new[] { "CreamAPI", "<objectId>", "stats", "CreamAPI.Achievements.cfg" } },
            new FileSpec { Type = "sm", Folder = AppData, Segments = new[] { "SmartSteamEmu", "<objectId>", "User", "Achievements.ini" } },
            new FileSpec { Type = "razor1911", Folder = AppData, Segments = new[] { ".1911", "<objectId>", "achievement" } },
        };

        private static readonly HashSet<string> NestedExecutableDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin32", "bin64", "binaries", "win32", "win64", "x64", "x86", "game", "runtime"
        };

        private static readonly HashSet<string> GameRootMarkerDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "engine", "binaries", "content", "plugins"
        };

        private static readonly HashSet<string> GameRootMarkerFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam_api.dll", "steam_api64.dll", "steam_appid.txt"
        };

        private static readonly string[] WindowsBinaryDirNames = { "Win64", "Win32", "WinGDK" };
        private static readonly string[] UnityPluginDirNames = { "x86_64", "x86" };

        private static readonly HashSet<string> SteamSettingsIgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "audio", "config", "configs", "content", "data", "intermediate", "localization",
            "logs", "movies", "music", "pak", "paks", "saved", "saves", "shaders", "sound", "sounds",
            "textures", "videos"
        };

        /// <summary>
        /// Returns all achievement files Hydra would consider for this game.
        /// </summary>
        public static List<AchievementFile> Find(string objectId, string executablePath, string installDirectory)
        {
            return Find(
                string.IsNullOrEmpty(objectId) ? new string[0] : new[] { objectId },
                executablePath,
                installDirectory);
        }

        /// <summary>
        /// Finds achievement files for one or more candidate AppIDs. Static cracker paths and the
        /// Steam librarycache lookup run per numeric ID; game-directory formats (userstats/3DM/ali213/
        /// Goldberg steam_settings) are ID-independent and always run exactly once — so custom or
        /// launchbox Hydra entries (no/invalid objectId) still surface their game-dir files.
        /// </summary>
        public static List<AchievementFile> Find(IEnumerable<string> objectIds, string executablePath, string installDirectory)
        {
            var results = new List<AchievementFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string type, string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var key = type + ":" + path;
                if (seen.Add(key)) results.Add(new AchievementFile { Type = type, Path = path });
            }

            foreach (var objectId in objectIds ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(objectId)) continue;

                // Static cracker paths are keyed by Steam AppID; only numeric IDs may reach them
                // (a launchbox/custom objectId could otherwise collide with some other game's files).
                if (objectId.All(char.IsDigit))
                {
                    foreach (var id in AlternativeObjectIds(objectId))
                    {
                        foreach (var spec in StaticSpecs)
                        {
                            var parts = spec.Segments.Select(s => s.Replace("<objectId>", id)).ToArray();
                            Add(spec.Type, Path.Combine(new[] { spec.Folder }.Concat(parts).ToArray()));
                        }
                    }

                    // Real Steam userdata cache (librarycache\<appid>.json), all local Steam users.
                    foreach (var steamPath in GetSteamPaths())
                    {
                        var userdata = Path.Combine(steamPath, "userdata");
                        if (!Directory.Exists(userdata)) continue;
                        foreach (var userDirSafe in SafeGetDirectories(userdata))
                        {
                            var name = Path.GetFileName(userDirSafe);
                            if (!name.All(char.IsDigit)) continue;
                            Add("steam", Path.Combine(userDirSafe, "config", "librarycache", objectId + ".json"));
                        }
                    }
                }
            }

            // Game-directory formats (userstats / 3DM / ali213 / Goldberg steam_settings).
            foreach (var baseDir in ResolveBaseDirectories(executablePath, installDirectory))
            {
                // userstats: <base>\SteamData\user_stats.ini
                Add("userstats", Path.Combine(baseDir, "SteamData", "user_stats.ini"));

                // 3DM: <base>\3DMGAME\<profile>\stats\achievements.ini
                var dir3dm = Path.Combine(baseDir, "3DMGAME");
                Add("3dm", Path.Combine(dir3dm, "stats", "achievements.ini"));
                foreach (var profile in SafeGetDirectories(dir3dm))
                {
                    Add("3dm", Path.Combine(profile, "stats", "achievements.ini"));
                }

                // ali213: <base>\Profile\<profile>\Stats\Achievements.Bin
                var profileRoot = Path.Combine(baseDir, "Profile");
                Add("ali213", Path.Combine(profileRoot, "Stats", "Achievements.Bin"));
                foreach (var profile in SafeGetDirectories(profileRoot))
                {
                    Add("ali213", Path.Combine(profile, "Stats", "Achievements.Bin"));
                }
            }

            // Goldberg in-game schema dir: <steam_settings>\<numericId>\achievements.json
            foreach (var ssDir in FindSteamSettingsDirectories(executablePath, installDirectory))
            {
                foreach (var sub in SafeGetDirectories(ssDir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Length > 0 && name.All(char.IsDigit))
                    {
                        Add("goldberg", Path.Combine(sub, "achievements.json"));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Discovers candidate Steam AppIDs from the game's own directory: steam_appid.txt in the
        /// base dirs / steam_settings / coldclient\steam_settings, plus numeric Goldberg-style
        /// achievement directories under steam_settings. Used to sync achievements for Hydra games
        /// whose objectId isn't a Steam AppID (shop=custom/launchbox).
        /// </summary>
        public static List<string> DiscoverAppIds(string executablePath, string installDirectory)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void ConsiderFile(string path)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    var text = File.ReadAllText(path).Trim();
                    var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
                    if (digits.Length > 0 && seen.Add(digits)) found.Add(digits);
                }
                catch
                {
                    // Unreadable file - skip.
                }
            }

            foreach (var baseDir in ResolveBaseDirectories(executablePath, installDirectory))
            {
                ConsiderFile(Path.Combine(baseDir, "steam_appid.txt"));
                ConsiderFile(Path.Combine(baseDir, "steam_settings", "steam_appid.txt"));
                ConsiderFile(Path.Combine(baseDir, "coldclient", "steam_settings", "steam_appid.txt"));
            }

            foreach (var ssDir in FindSteamSettingsDirectories(executablePath, installDirectory))
            {
                foreach (var sub in SafeGetDirectories(ssDir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Length > 0 && name.All(char.IsDigit) && seen.Add(name)) found.Add(name);
                }
            }

            return found;
        }

        public static IEnumerable<string> AlternativeObjectIds(string objectId)
        {
            // Dishonored alt AppIDs (same as Hydra).
            if (objectId == "205100") return new[] { "205100", "217980", "31292" };
            return new[] { objectId };
        }

        // ---------- game directory resolution (game-directory.ts port) ----------

        private static IEnumerable<string> ResolveBaseDirectories(string executablePath, string installDirectory)
        {
            var bases = new List<string>();

            if (!string.IsNullOrEmpty(executablePath))
            {
                try
                {
                    var exeDir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        var searchRoot = ResolveGameSearchRoot(exeDir);
                        bases.Add(exeDir);
                        if (!string.IsNullOrEmpty(searchRoot)) bases.Add(searchRoot);
                        if (!string.IsNullOrEmpty(searchRoot)) bases.AddRange(CollectLayoutDirectories(searchRoot));
                    }
                }
                catch
                {
                    // malformed path - fall through to install directory
                }
            }

            if (bases.Count == 0 && !string.IsNullOrEmpty(installDirectory) && Directory.Exists(installDirectory))
            {
                bases.Add(installDirectory);
                bases.AddRange(SafeGetDirectories(installDirectory));
            }

            return bases
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ResolveGameSearchRoot(string exeDirectory)
        {
            var current = exeDirectory;
            for (var level = 0; level < 3; level++)
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) break;

                string[] entries;
                try
                {
                    if (!Directory.Exists(parent)) break;
                    entries = Directory.GetFileSystemEntries(parent);
                }
                catch
                {
                    break;
                }

                var currentIsNested = NestedExecutableDirs.Contains(Path.GetFileName(current));
                if (!currentIsNested && !HasGameRootMarker(entries)) break;
                current = parent;
            }

            return current;
        }

        private static bool HasGameRootMarker(string[] entries)
        {
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var isDir = Directory.Exists(entry);
                if (isDir)
                {
                    if (GameRootMarkerDirs.Contains(name) ||
                        name.EndsWith("_data", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "steam_settings", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (GameRootMarkerFiles.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> CollectLayoutDirectories(string searchRoot)
        {
            var dirs = new List<string>();
            foreach (var dir in SafeGetDirectories(searchRoot))
            {
                var name = Path.GetFileName(dir);

                if (NestedExecutableDirs.Contains(name)) dirs.Add(dir);
                foreach (var arch in WindowsBinaryDirNames) dirs.Add(Path.Combine(dir, "Binaries", arch));
                if (name.EndsWith("_data", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var arch in UnityPluginDirNames) dirs.Add(Path.Combine(dir, "Plugins", arch));
                }
            }

            // Engine/Binaries/ThirdParty/Steamworks/<version>/<arch>
            var steamworks = Path.Combine(searchRoot, "Engine", "Binaries", "ThirdParty", "Steamworks");
            foreach (var entry in SafeGetDirectories(steamworks))
            {
                foreach (var arch in WindowsBinaryDirNames) dirs.Add(Path.Combine(entry, arch));
            }

            return dirs.Where(Directory.Exists).ToList();
        }

        // ---------- steam_settings discovery (find-steam-settings-directories.ts port) ----------

        /// <summary>Public view of the steam_settings directory discovery (game roots + coldclient
        /// + bounded BFS fallback) — used to locate definition files (achievements.json roots).</summary>
        public static IEnumerable<string> FindSteamSettingsDirs(string executablePath, string installDirectory)
            => FindSteamSettingsDirectories(executablePath, installDirectory);

        private static IEnumerable<string> FindSteamSettingsDirectories(string executablePath, string installDirectory)
        {
            var found = new List<string>();
            var bases = ResolveBaseDirectories(executablePath, installDirectory);

            foreach (var baseDir in bases)
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(baseDir, "steam_settings"),
                    Path.Combine(baseDir, "coldclient", "steam_settings")
                })
                {
                    if (Directory.Exists(candidate)) found.Add(candidate);
                }
            }

            if (found.Count > 0) return found.Distinct(StringComparer.OrdinalIgnoreCase);

            // Breadth-first fallback from the search root (depth ≤ 8, ≤ 2000 dirs).
            var root = !string.IsNullOrEmpty(executablePath)
                ? ResolveGameSearchRoot(Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? "")
                : installDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;

            var visited = 0;
            var level = new List<string> { root };
            for (var depth = 0; depth < 8 && level.Count > 0 && found.Count == 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in level)
                {
                    if (visited++ >= 2000) break;
                    foreach (var sub in SafeGetDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (string.Equals(name, "steam_settings", StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(sub);
                        }
                        else if (!SteamSettingsIgnoredDirs.Contains(name))
                        {
                            next.Add(sub);
                        }
                    }
                }

                level = next;
            }

            return found;
        }

        // ---------- steam install discovery ----------

        private static IEnumerable<string> GetSteamPaths()
        {
            var paths = new List<string>();

            void TryAdd(string p)
            {
                if (string.IsNullOrEmpty(p)) return;
                try
                {
                    if (Directory.Exists(p) && Directory.Exists(Path.Combine(p, "userdata")) &&
                        !paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        paths.Add(p);
                    }
                }
                catch
                {
                    // ignore invalid paths
                }
            }

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                    {
                        TryAdd(key?.GetValue("InstallPath") as string);
                    }
                }
                catch
                {
                    // registry unavailable
                }
            }

            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            TryAdd(Path.Combine(pfx86, "Steam"));

            return paths;
        }

        private static IEnumerable<string> SafeGetDirectories(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return Enumerable.Empty<string>();
            try
            {
                return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Enumerable.Empty<string>();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }
    }
}
