using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace HydraSync.Achievements
{
    public class SteamSchemaAchievement
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public bool Hidden { get; set; }
        public string IconUrl { get; set; }
        public string IconGrayUrl { get; set; }
    }

    public class SteamSchema
    {
        public int AppId { get; set; }
        public bool HasAchievements { get; set; }
        public DateTime FetchedUtc { get; set; }
        public List<SteamSchemaAchievement> Achievements { get; set; } = new List<SteamSchemaAchievement>();
    }

    /// <summary>
    /// Achievement metadata (names/descriptions/icons) from the public Steam store
    /// appdetails API, cached in the plugin data dir (steam_schema_cache/{appid}.json, TTL 30 days).
    /// Only consulted when a game actually has unlocks (keeps API usage tiny).
    /// </summary>
    public static class SteamSchemaClient
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
        private static readonly HttpClient Http;

        static SteamSchemaClient()
        {
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("HydraSync/1.0 (Playnite extension)");
        }

        public static async Task<SteamSchema> GetAsync(int appId, string cacheDir, ILogger log)
        {
            var cacheFile = Path.Combine(cacheDir, appId + ".json");

            // Cache hit?
            try
            {
                if (File.Exists(cacheFile) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl)
                {
                    return JsonConvert.DeserializeObject<SteamSchema>(File.ReadAllText(cacheFile));
                }
            }
            catch
            {
                // Corrupt cache - refetch below.
            }

            SteamSchema schema;
            try
            {
                schema = await FetchAsync(appId);
            }
            catch (Exception ex)
            {
                // Network failure: don't poison the cache, just use unlock names this run.
                log?.Debug($"Steam schema fetch failed for {appId}: {ex.Message}");
                return null;
            }

            try
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(cacheFile, JsonConvert.SerializeObject(schema));
            }
            catch
            {
                // Cache write is best-effort.
            }

            return schema;
        }

        private static async Task<SteamSchema> FetchAsync(int appId)
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=us&l=en";
            var json = await Http.GetStringAsync(url);
            var schema = new SteamSchema { AppId = appId, FetchedUtc = DateTime.UtcNow };

            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            if (!root.TryGetValue(appId.ToString(), out var entry) ||
                entry["success"]?.ToObject<bool>() != true)
            {
                // success=false (no such app / no data) - cache negative result.
                schema.HasAchievements = false;
                return schema;
            }

            var achievements = entry["data"]?["achievements"] as Newtonsoft.Json.Linq.JArray;
            if (achievements == null || achievements.Count == 0)
            {
                schema.HasAchievements = false;
                return schema;
            }

            schema.HasAchievements = true;
            foreach (var a in achievements)
            {
                var name = a["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                // Two shapes exist: path.{icon,icongray} or top-level icon/icongray.
                var icon = a["path"]?["icon"]?.ToString() ?? a["icon"]?.ToString();
                var iconGray = a["path"]?["icongray"]?.ToString() ?? a["icongray"]?.ToString();

                schema.Achievements.Add(new SteamSchemaAchievement
                {
                    Name = name,
                    DisplayName = a["displayName"]?.ToString() ?? name,
                    Description = a["description"]?.ToString() ?? "",
                    Hidden = a["hidden"]?.ToString() == "1" ||
                             a["hidden"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                    IconUrl = BuildIconUrl(appId, icon),
                    IconGrayUrl = BuildIconUrl(appId, iconGray),
                });
            }

            return schema;
        }

        /// <summary>
        /// Full schema (API names + display names + descriptions + icons) from the official
        /// ISteamUserStats/GetSchemaForGame endpoint. Requires a user-supplied Steam Web API
        /// key (https://steamcommunity.com/dev/apikey). Cached separately from appdetails
        /// ({appid}.webapi.json, TTL 30 days). Returns null on failure/absent key.
        /// </summary>
        public static async Task<SteamSchema> GetFromWebApiAsync(int appId, string apiKey, string cacheDir, ILogger log)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(apiKey)) return null;

            var cacheFile = Path.Combine(cacheDir, appId + ".webapi.json");
            try
            {
                if (File.Exists(cacheFile) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl)
                {
                    return JsonConvert.DeserializeObject<SteamSchema>(File.ReadAllText(cacheFile));
                }
            }
            catch
            {
                // Corrupt cache - refetch below.
            }

            SteamSchema schema;
            try
            {
                var url = "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/" +
                          $"?appid={appId}&key={Uri.EscapeDataString(apiKey.Trim())}";
                var json = await Http.GetStringAsync(url);
                schema = new SteamSchema { AppId = appId, FetchedUtc = DateTime.UtcNow };

                var achievements = Newtonsoft.Json.Linq.JObject.Parse(json)
                    ["game"]?["availableGameStats"]?["achievements"] as Newtonsoft.Json.Linq.JArray;
                if (achievements == null || achievements.Count == 0)
                {
                    schema.HasAchievements = false;
                    return schema;
                }

                schema.HasAchievements = true;
                foreach (var a in achievements)
                {
                    var name = a["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    schema.Achievements.Add(new SteamSchemaAchievement
                    {
                        Name = name,
                        DisplayName = a["displayName"]?.ToString() ?? name,
                        Description = a["description"]?.ToString() ?? "",
                        Hidden = a["hidden"]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean
                            ? a["hidden"].ToObject<bool>()
                            : a["hidden"]?.ToString() == "1" ||
                              a["hidden"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                        IconUrl = BuildIconUrl(appId, a["icon"]?.ToString()),
                        IconGrayUrl = BuildIconUrl(appId, a["icongray"]?.ToString()),
                    });
                }
            }
            catch (Exception ex)
            {
                log?.Debug($"Steam Web API schema fetch failed for {appId}: {ex.Message}");
                return null;
            }

            try
            {
                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(cacheFile, JsonConvert.SerializeObject(schema));
            }
            catch
            {
                // Cache write is best-effort.
            }

            return schema;
        }

        private static string BuildIconUrl(int appId, string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return icon;
            return $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{icon}";
        }
    }
}
