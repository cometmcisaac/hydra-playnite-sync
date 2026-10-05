using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HydraSync.Hydra
{
    /// <summary>An achievement unlock parsed from a cracker/emulator file.</summary>
    public class UnlockedAchievement
    {
        public string Name { get; set; }
        /// <summary>Unlock time in Unix epoch milliseconds (UTC). Null when the file has no usable timestamp.</summary>
        public long? UnlockTimeMs { get; set; }
    }

    /// <summary>
    /// Exact port of Hydra's parse-achievement-file.ts + parse-achievement-formats.ts:
    /// cracker file type → parser dispatch and every per-format processor.
    /// </summary>
    public static class AchievementParsers
    {
        private const int MicrosecondTimestampLength = 7;
        private static readonly System.Text.RegularExpressions.Regex UnlockedPattern =
            new System.Text.RegularExpressions.Regex(@"\bunlocked\s*=\s*true\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex UnlockTimePattern =
            new System.Text.RegularExpressions.Regex(@"(?:^|[{,\s])time\s*=\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Dispatches on the locator's file type (mirrors ACHIEVEMENT_PARSERS).</summary>
        public static List<UnlockedAchievement> ParseFile(string type, string filePath)
        {
            var result = new List<UnlockedAchievement>();
            if (string.IsNullOrEmpty(type) || !File.Exists(filePath)) return result;

            try
            {
                switch (type.ToLowerInvariant())
                {
                    case "codex":
                    case "rune":
                        ProcessDefault(IniParse(filePath), "Achieved", "UnlockTime", result);
                        break;
                    case "onlinefix":
                        ProcessOnlineFix(IniParse(filePath), result);
                        break;
                    case "goldberg":
                    case "empress":
                        ProcessGoldberg(JsonParse(filePath), result);
                        break;
                    case "userstats":
                        ProcessUserStats(IniParse(filePath), result);
                        break;
                    case "rld":
                        ProcessRld(IniParse(filePath), result);
                        break;
                    case "skidrow":
                        ProcessSkidrow(IniParse(filePath), result);
                        break;
                    case "3dm":
                        Process3Dm(IniParse(filePath), result);
                        break;
                    case "ali213":
                        ProcessDefault(IniParse(filePath), "HaveAchieved", "HaveAchievedTime", result);
                        break;
                    case "creamapi":
                        ProcessCreamApi(IniParse(filePath), result);
                        break;
                    case "razor1911":
                        ProcessRazor1911(filePath, result);
                        break;
                    case "steam":
                        ProcessSteamCache(JsonParse(filePath), result);
                        break;
                }
            }
            catch
            {
                // Hydra also swallows parse errors per file.
                return new List<UnlockedAchievement>();
            }

            result.RemoveAll(a => string.IsNullOrWhiteSpace(a.Name));
            return result;
        }

        // ---------- INI / JSON primitives ----------

        /// <summary>
        /// Port of iniParse: sectioned dictionary. Lines before the first [section]
        /// land in the "" section (matching JS objectName = "").
        /// </summary>
        // JS Record<string,...> is case-sensitive - keep INI keys case-sensitive too,
        // otherwise "achieved" and "Achieved" (distinct OnlineFix variants) collide.
        public static Dictionary<string, Dictionary<string, string>> IniParse(string filePath)
        {
            var text = File.ReadAllText(filePath);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var sectionDict = new Dictionary<string, string>(StringComparer.Ordinal);
            result[""] = sectionDict;
            var sectionName = "";

            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    sectionName = line.Substring(1, line.Length - 2);
                    if (!result.TryGetValue(sectionName, out sectionDict))
                    {
                        sectionDict = new Dictionary<string, string>(StringComparer.Ordinal);
                        result[sectionName] = sectionDict;
                    }

                    continue;
                }

                var eq = line.IndexOf('=');
                string name, value;
                if (eq >= 0)
                {
                    name = line.Substring(0, eq).Trim();
                    value = line.Substring(eq + 1).Trim();
                }
                else
                {
                    name = line;
                    value = "";
                }

                sectionDict[name] = value;
            }

            return result;
        }

        private static JToken JsonParse(string filePath)
        {
            using (var sr = new StringReader(File.ReadAllText(filePath)))
            using (var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None })
            {
                return JToken.ReadFrom(jr);
            }
        }

        // ---------- processors ----------

        private static void ProcessDefault(
            Dictionary<string, Dictionary<string, string>> ini,
            string achievedKey,
            string unlockTimeKey,
            List<UnlockedAchievement> result)
        {
            foreach (var kv in ini)
            {
                if (kv.Value == null) continue;
                if (!kv.Value.TryGetValue(achievedKey, out var achieved)) continue;
                if (!IsAchieved(achieved)) continue;
                if (!kv.Value.TryGetValue(unlockTimeKey, out var timeText)) continue;

                var ms = SecOrRawToMs(timeText);
                if (ms == null) continue;
                result.Add(new UnlockedAchievement { Name = kv.Key, UnlockTimeMs = ms });
            }
        }

        private static void ProcessOnlineFix(
            Dictionary<string, Dictionary<string, string>> ini,
            List<UnlockedAchievement> result)
        {
            foreach (var kv in ini)
            {
                if (kv.Value == null) continue;

                if (kv.Value.TryGetValue("achieved", out var a1) && IsAchieved(a1))
                {
                    if (kv.Value.TryGetValue("timestamp", out var ts))
                    {
                        var ms = SecOrRawToMs(ts);
                        if (ms != null) result.Add(new UnlockedAchievement { Name = kv.Key, UnlockTimeMs = ms });
                    }

                    continue;
                }

                if (kv.Value.TryGetValue("Achieved", out var a2) && IsAchieved(a2) &&
                    kv.Value.TryGetValue("TimeUnlocked", out var tu))
                {
                    var ms = ParseUnlockTime(tu);
                    if (ms != null) result.Add(new UnlockedAchievement { Name = kv.Key, UnlockTimeMs = ms });
                }
            }
        }

        private static void ProcessCreamApi(
            Dictionary<string, Dictionary<string, string>> ini,
            List<UnlockedAchievement> result)
        {
            foreach (var kv in ini)
            {
                if (kv.Value == null) continue;
                if (!kv.Value.TryGetValue("achieved", out var achieved) || !IsAchieved(achieved)) continue;
                if (!kv.Value.TryGetValue("unlocktime", out var timeText)) continue;

                var ms = ParseUnlockTime(timeText);
                if (ms != null) result.Add(new UnlockedAchievement { Name = kv.Key, UnlockTimeMs = ms });
            }
        }

        private static void ProcessSkidrow(
            Dictionary<string, Dictionary<string, string>> ini,
            List<UnlockedAchievement> result)
        {
            if (!ini.TryGetValue("Achievements", out var section)) return;

            foreach (var kv in section)
            {
                var parts = kv.Value.Split('@');
                if (parts.Length == 0 || parts[0] != "1") continue;

                var ms = SecOrRawToMs(parts[parts.Length - 1]);
                if (ms != null) result.Add(new UnlockedAchievement { Name = kv.Key, UnlockTimeMs = ms });
            }
        }

        private static void ProcessGoldberg(JToken data, List<UnlockedAchievement> result)
        {
            if (data is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (!JTruthy(item["earned"])) continue;
                    var name = item["name"]?.ToString();
                    var ms = SecOrRawToMs(item["earned_time"]?.ToString());
                    if (name != null && ms != null)
                        result.Add(new UnlockedAchievement { Name = name, UnlockTimeMs = ms });
                }

                return;
            }

            if (data is JObject obj)
            {
                foreach (var kv in obj)
                {
                    if (kv.Value is JObject entry && JTruthy(entry["earned"]))
                    {
                        var ms = SecOrRawToMs(entry["earned_time"]?.ToString());
                        if (ms != null)
                            result.Add(new UnlockedAchievement { Name = kv.Key, UnlockTimeMs = ms });
                    }
                }
            }
        }

        private static void Process3Dm(
            Dictionary<string, Dictionary<string, string>> ini,
            List<UnlockedAchievement> result)
        {
            if (!ini.TryGetValue("State", out var states)) return;
            ini.TryGetValue("Time", out var times);

            foreach (var kv in states)
            {
                if (kv.Value != "0101") continue;
                if (times == null || !times.TryGetValue(kv.Key, out var timeHex)) continue;

                var ms = HexU32LeToMs(timeHex);
                if (ms != null) result.Add(new UnlockedAchievement { Name = kv.Key, UnlockTimeMs = ms });
            }
        }

        private static void ProcessRld(
            Dictionary<string, Dictionary<string, string>> ini,
            List<UnlockedAchievement> result)
        {
            foreach (var section in ini)
            {
                if (string.Equals(section.Key, "Steam", StringComparison.OrdinalIgnoreCase)) continue;
                if (section.Value == null) continue;
                if (!section.Value.TryGetValue("State", out var stateHex)) continue;

                // JS compares the raw u32 (no ms conversion): unlocked === 1.
                var state = HexU32Le(stateHex);
                if (state != 1) continue;

                if (!section.Value.TryGetValue("Time", out var timeHex)) continue;
                var rawTime = HexU32Le(timeHex);
                if (rawTime.HasValue) result.Add(new UnlockedAchievement { Name = section.Key, UnlockTimeMs = rawTime.Value * 1000L });
            }
        }

        private static void ProcessUserStats(
            Dictionary<string, Dictionary<string, string>> ini,
            List<UnlockedAchievement> result)
        {
            if (!ini.TryGetValue("ACHIEVEMENTS", out var achievements)) return;

            foreach (var kv in achievements)
            {
                var value = kv.Value ?? "";
                if (!UnlockedPattern.IsMatch(value)) continue;

                var m = UnlockTimePattern.Match(value);
                if (!m.Success) continue;

                var ms = SecOrRawToMs(m.Groups[1].Value);
                if (ms != null)
                    result.Add(new UnlockedAchievement { Name = kv.Key.Replace("\"", ""), UnlockTimeMs = ms });
            }
        }

        private static void ProcessRazor1911(string filePath, List<UnlockedAchievement> result)
        {
            var text = File.ReadAllText(filePath);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rawLine.Split(' ');
                if (parts.Length < 3) continue;
                if (parts[1] != "1") continue;

                var ms = SecOrRawToMs(parts[2]);
                if (ms != null) result.Add(new UnlockedAchievement { Name = parts[0], UnlockTimeMs = ms });
            }
        }

        private static void ProcessSteamCache(JToken data, List<UnlockedAchievement> result)
        {
            if (!(data is JArray arr)) return;

            JToken highlight = null;
            foreach (var item in arr)
            {
                if (item is JArray pair && pair.Count >= 2 && pair[0]?.ToString() == "achievements")
                {
                    highlight = pair[1]?["data"]?["vecHighlight"];
                    break;
                }
            }

            if (!(highlight is JArray list)) return;

            foreach (var a in list)
            {
                if (!JTruthy(a["bAchieved"])) continue;
                var name = a["strID"]?.ToString();
                var ms = SecOrRawToMs(a["rtUnlocked"]?.ToString());
                if (name != null && ms != null)
                    result.Add(new UnlockedAchievement { Name = name, UnlockTimeMs = ms });
            }
        }

        // ---------- helpers ----------

        /// <summary>Covers JS loose equality against "1" / "true" used across formats.</summary>
        private static bool IsAchieved(string value)
        {
            if (value == null) return false;
            var v = value.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool JTruthy(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return false;
            switch (t.Type)
            {
                case JTokenType.Boolean: return t.Value<bool>();
                case JTokenType.Integer: return t.Value<long>() != 0;
                case JTokenType.Float: return t.Value<double>() != 0;
                case JTokenType.String:
                    var s = t.ToString().Trim();
                    return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        /// <summary>Seconds (or already-ms) → ms. Returns null when unparsable.</summary>
        private static long? SecOrRawToMs(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
            if (double.IsNaN(n) || double.IsInfinity(n)) return null;
            return (long)(n * 1000);
        }

        /// <summary>Port of parseUnlockTime: 7-digit string → µs→ms, otherwise sec→ms.</summary>
        private static long? ParseUnlockTime(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim();
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
            if (double.IsNaN(n) || double.IsInfinity(n)) return null;
            return (long)(t.Length == MicrosecondTimestampLength ? n * 1000 * 1000 : n * 1000);
        }

        /// <summary>Little-endian u32 hex → raw value (null when unparsable).</summary>
        private static uint? HexU32Le(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            hex = hex.Trim();

            var bytes = new List<byte>(4);
            for (var i = 0; i + 1 < hex.Length && bytes.Count < 4; i += 2)
            {
                if (!byte.TryParse(hex.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    return null;
                bytes.Add(b);
            }

            if (bytes.Count == 0) return null;
            while (bytes.Count < 4) bytes.Add(0);

            return BitConverter.ToUInt32(bytes.ToArray(), 0); // .NET targets are little-endian
        }

        /// <summary>Little-endian u32 hex → ms (timestamp semantics).</summary>
        private static long? HexU32LeToMs(string hex)
        {
            var raw = HexU32Le(hex);
            return raw.HasValue ? raw.Value * 1000L : (long?)null;
        }
    }
}
