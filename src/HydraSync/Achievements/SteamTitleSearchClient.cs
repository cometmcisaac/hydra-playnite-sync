using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace HydraSync.Achievements
{
    /// <summary>A Steam store app whose title matched a library game.</summary>
    public class SteamTitleMatch
    {
        public int AppId { get; set; }
        public string Name { get; set; }
        public double Similarity { get; set; }
    }

    /// <summary>
    /// Finds a game's Steam AppID by searching the store by title. Only used for games that
    /// have no AppID at all (a non-Steam Hydra entry with no steam_appid.txt anywhere in its
    /// folder), which is exactly the case where no achievement names, descriptions or icons
    /// could be resolved before. Results - including "nothing found" - are cached in the plugin
    /// data dir for 30 days so a game that isn't on Steam is only looked up once.
    ///
    /// A candidate is only accepted when its title is essentially the same game
    /// (<see cref="Similarity"/> >= 0.9), so a search never attaches another game's schema.
    /// </summary>
    public static class SteamTitleSearchClient
    {
        private const double MinimumSimilarity = 0.9;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
        private static readonly HttpClient Http;

        static SteamTitleSearchClient()
        {
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("HydraSync/1.0 (Playnite extension)");
        }

        /// <summary>
        /// Searches the Steam store for <paramref name="title"/> and returns the closest match
        /// above the similarity threshold, or null when nothing is close enough.
        /// </summary>
        public static async Task<SteamTitleMatch> FindAsync(string title, string cacheDir, ILogger log)
        {
            var normalized = Normalize(title);
            if (normalized.Length < 3)
            {
                log?.Debug("HydraSync: title search skipped (title too short)");
                return null;
            }

            var cacheFile = Path.Combine(cacheDir ?? ".", "title-" + Hash(normalized) + ".json");
            try
            {
                if (File.Exists(cacheFile) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl)
                {
                    var cached = JsonConvert.DeserializeObject<TitleSearchCache>(File.ReadAllText(cacheFile));
                    if (cached != null)
                    {
                        return cached.AppId > 0
                            ? new SteamTitleMatch { AppId = cached.AppId, Name = cached.Name, Similarity = cached.Similarity }
                            : null;
                    }
                }
            }
            catch
            {
                // Corrupt cache - search again below.
            }

            SteamTitleMatch match = null;
            try
            {
                match = await SearchAsync(normalized, log);
            }
            catch (Exception ex)
            {
                // Never poison the cache on a network failure.
                log?.Debug($"Steam title search failed for \"{title}\": {ex.Message}");
                return null;
            }

            try
            {
                if (!string.IsNullOrEmpty(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                    File.WriteAllText(cacheFile, JsonConvert.SerializeObject(new TitleSearchCache
                    {
                        Title = title,
                        AppId = match?.AppId ?? 0,
                        Name = match?.Name,
                        Similarity = match?.Similarity ?? 0,
                    }));
                }
            }
            catch (Exception ex)
            {
                log?.Debug("HydraSync: could not cache title search: " + ex.Message);
            }

            return match;
        }

        private static async Task<SteamTitleMatch> SearchAsync(string normalizedTitle, ILogger log)
        {
            var url = "https://store.steampowered.com/api/storesearch/?term=" +
                      Uri.EscapeDataString(normalizedTitle) + "&cc=us&l=en";
            var body = await Http.GetStringAsync(url);

            var root = Newtonsoft.Json.Linq.JObject.Parse(body);
            var items = root["items"] as Newtonsoft.Json.Linq.JArray;
            if (items == null) return null;

            SteamTitleMatch best = null;
            foreach (var token in items)
            {
                var item = token as Newtonsoft.Json.Linq.JObject;
                if (item == null) continue;

                var type = (string)item["type"];
                if (!string.IsNullOrEmpty(type) &&
                    !string.Equals(type, "app", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = (int?)item["id"] ?? 0;
                if (id <= 0) continue;

                var name = (string)item["name"];
                var similarity = Similarity(normalizedTitle, Normalize(name));
                if (similarity < MinimumSimilarity) continue;
                if (best != null && similarity <= best.Similarity) continue;

                best = new SteamTitleMatch { AppId = id, Name = name, Similarity = similarity };
            }

            if (best != null)
            {
                log?.Debug($"HydraSync: title search matched \"{normalizedTitle}\" to " +
                           $"{best.Name} (AppID {best.AppId}, similarity {best.Similarity:0.00})");
            }
            else
            {
                log?.Debug($"HydraSync: title search found no close Steam match for \"{normalizedTitle}\"");
            }

            return best;
        }

        /// <summary>
        /// Lower-cased, symbol-free form used for comparing titles (same rules as library
        /// matching, kept local so this file stays free of engine types).
        /// </summary>
        internal static string Normalize(string title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;

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

        /// <summary>
        /// 1.0 for identical titles, otherwise the share of the longest title's words that also
        /// appear in the other one. "Hollow Knight" vs "Hollow Knight Silksong" scores 0.67, so
        /// sequels and expansions are never mistaken for the game.
        /// </summary>
        internal static double Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

            var left = a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var right = b.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (left.Count == 0 || right.Count == 0) return 0;

            var shared = left.Count(word => right.Contains(word));
            return (double)shared / Math.Max(left.Count, right.Count);
        }

        private static string Hash(string normalizedTitle)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedTitle));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, 16);
            }
        }

        private class TitleSearchCache
        {
            public string Title { get; set; }
            public int AppId { get; set; }
            public string Name { get; set; }
            public double Similarity { get; set; }
        }
    }
}