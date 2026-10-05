using System;
using System.Collections.Generic;
using System.IO;
using HydraSync.Hydra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HydraSync.Achievements
{
    /// <summary>
    /// Achievement DEFINITIONS (display names/descriptions/icons keyed by API name) read from
    /// the game directory's steam_settings/achievements.json — written by Hydra's metadata
    /// exporter or shipped with Goldberg/GSE-based cracks. This is the primary schema source:
    /// it is local, complete (unlike the store appdetails API, which now only returns a
    /// highlighted subset without API names), and its keys match the unlock files by
    /// construction. No Steam login or API key required.
    /// </summary>
    public static class LocalAchievementDefinitions
    {
        /// <summary>Paths of candidate definition files for a game (steam_settings roots only —
        /// never the &lt;numericId&gt; per-user unlock-state subdirectories).</summary>
        public static List<string> FindFiles(string executablePath, string installDirectory)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ssDir in AchievementFileLocator.FindSteamSettingsDirs(executablePath, installDirectory))
            {
                try
                {
                    var file = Path.Combine(ssDir, "achievements.json");
                    if (File.Exists(file) && seen.Add(file)) found.Add(file);
                }
                catch
                {
                    // Invalid path - skip.
                }
            }
            return found;
        }

        /// <summary>
        /// Parses a definitions file. Returns null when the file holds no usable definitions
        /// (e.g. it is actually a Goldberg unlock-state file with earned/earned_time entries).
        /// Accepts the Hydra/Goldberg array shape and the api-name-keyed object shape.
        /// </summary>
        public static SteamSchema Parse(string path, int appId)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                JToken token;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path)))
                {
                    DateParseHandling = DateParseHandling.None
                })
                {
                    token = JToken.Load(reader);
                }

                var schema = new SteamSchema
                {
                    AppId = appId,
                    FetchedUtc = DateTime.UtcNow,
                    HasAchievements = false
                };

                if (token is JArray array)
                {
                    foreach (var item in array)
                    {
                        if (!(item is JObject entry) || IsUnlockState(entry)) continue;
                        var ach = ReadEntry(entry, apiNameFallback: null, dir);
                        if (ach != null) schema.Achievements.Add(ach);
                    }
                }
                else if (token is JObject obj)
                {
                    foreach (var prop in obj.Properties())
                    {
                        if (!(prop.Value is JObject inner) || IsUnlockState(inner)) continue;
                        var ach = ReadEntry(inner, apiNameFallback: prop.Name, dir);
                        if (ach != null) schema.Achievements.Add(ach);
                    }
                }

                if (schema.Achievements.Count == 0) return null;
                schema.HasAchievements = true;
                return schema;
            }
            catch
            {
                // Malformed file - treat as no definitions; caller falls through the chain.
                return null;
            }
        }

        // Goldberg/GSE unlock-state files carry earned flags - they are NOT definitions.
        private static bool IsUnlockState(JObject entry)
        {
            return entry["earned"] != null || entry["earned_time"] != null;
        }

        private static SteamSchemaAchievement ReadEntry(JObject entry, string apiNameFallback, string dir)
        {
            var apiName = entry["name"]?.ToString();
            if (string.IsNullOrEmpty(apiName)) apiName = apiNameFallback;
            if (string.IsNullOrEmpty(apiName)) return null;

            var display =
                entry["displayName"]?.ToString() ??
                entry["displayname"]?.ToString() ??
                entry["localized_name"]?.ToString() ??
                apiName;

            return new SteamSchemaAchievement
            {
                Name = apiName,
                DisplayName = display,
                Description = entry["description"]?.ToString() ?? "",
                Hidden = ParseHidden(entry["hidden"]),
                IconUrl = ResolveIcon(entry["icon"], dir),
                IconGrayUrl = ResolveIcon(entry["icongray"] ?? entry["icon_gray"], dir)
            };
        }

        private static bool ParseHidden(JToken token)
        {
            if (token == null) return false;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            var s = token.ToString().Trim();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>http(s) URLs pass through; relative posix paths (Hydra's images/1.jpg or
        /// crack-relative icons) become absolute file paths — PA resolves local paths via File.Exists.</summary>
        private static string ResolveIcon(JToken token, string dir)
        {
            var value = token?.ToString().Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.StartsWith("/") || System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z]:"))
                return null; // Absolute or rooted paths from untrusted files: ignore.

            try
            {
                var full = Path.GetFullPath(Path.Combine(dir ?? "", value.Replace('/', Path.DirectorySeparatorChar)));
                return full;
            }
            catch
            {
                return null;
            }
        }
    }
}
