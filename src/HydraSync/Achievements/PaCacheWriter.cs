using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace HydraSync.Achievements
{
    /// <summary>One achievement entry in PlayniteAchievements' legacy JSON cache format.</summary>
    public class PaAchievement
    {
        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string UnlockedIconPath { get; set; }
        public string LockedIconPath { get; set; }
        public bool Unlocked { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public bool Hidden { get; set; }
    }

    /// <summary>
    /// Writes per-game achievement JSON into PlayniteAchievements' achievement_cache/
    /// directory (inside PA's data folder, ExtensionsData\{PA class Guid}). PA's
    /// LegacyJsonCacheImporter.ImportIfNeeded() imports every *.json there (key = filename =
    /// Playnite game GUID), stores it in PA's SQLite cache under the given ProviderKey
    /// ("Steam" / "Manual"), then deletes the file. The import normally runs at PA startup;
    /// <c>PlayniteAchievementsBridge</c> triggers it immediately after a write so no restart is
    /// needed. Display is provider-agnostic (cached data is read by game GUID), so the data
    /// shows up for any game, including non-Steam ones.
    ///
    /// Field names follow PA's Models/Achievements/GameAchievementData.cs schema;
    /// the importer parses tolerantly (missing fields default).
    /// </summary>
    public static class PaCacheWriter
    {
        // Plugin.GetPluginUserDataPath() = ExtensionsData\<plugin class Guid> — PA's class Id,
        // NOT the "PlayniteAchievements" name from its extension.yaml manifest. (Root cause of
        // "PA not found" on real installs: the manifest name folder never exists.)
        private const string PaClassGuid = "e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b";
        private const string PaManifestName = "PlayniteAchievements";

        /// <summary>
        /// Resolved PlayniteAchievements data dir: ExtensionsData\{class Guid} first (what PA
        /// actually uses), then the manifest-name folder (future-safe), then any ExtensionsData
        /// child containing PA's game_custom_data.db marker. Falls back to the Guid path for
        /// error messages when PA has never run.
        /// </summary>
        public static string PaPluginDir
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Playnite", "ExtensionsData");

                var byGuid = Path.Combine(root, PaClassGuid);
                if (Directory.Exists(byGuid)) return byGuid;

                var byName = Path.Combine(root, PaManifestName);
                if (Directory.Exists(byName)) return byName;

                try
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        if (File.Exists(Path.Combine(dir, "game_custom_data.db"))) return dir;
                    }
                }
                catch
                {
                    // ExtensionsData missing/unreadable - report as not found below.
                }

                return byGuid;
            }
        }

        public static string CacheDir => Path.Combine(PaPluginDir, "achievement_cache");

        /// <summary>True when the PlayniteAchievements extension is installed.</summary>
        public static bool IsAvailable => Directory.Exists(PaPluginDir);

        /// <param name="providerKey">
        /// PA cache ProviderKey. "Steam" for Steam-shop games (PA then labels and styles the data
        /// with its Steam source); "Manual" for non-Steam games — PA treats cached non-"Manual"
        /// data as "another provider owns this game" and hides its manual-tracking features, while
        /// "Manual" keeps them available (and PA's manual refresh never runs for games without a
        /// manual link, so our data can't be clobbered).
        /// </param>
        /// <param name="existing">
        /// PA's current record for this game when it could be read
        /// (<c>PlayniteAchievementsBridge.TryReadGameData</c>). The payload is merged with it so
        /// other providers' entries and unlocks PA already knows are preserved; null = no merge.
        /// </param>
        public static void Write(Game game, int appId, string providerGameKey,
            IReadOnlyList<PaAchievement> achievements, string providerKey, JObject existing, ILogger log)
        {
            Directory.CreateDirectory(CacheDir);

            var dto = new PaDataDto
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = string.IsNullOrEmpty(providerKey) ? "Hydra" : providerKey,
                LibrarySourceName = "Hydra",
                HasAchievements = true,
                GameName = game.Name,
                AppId = appId,
                ProviderGameKey = providerGameKey,
                PlayniteGameId = game.Id,
                Achievements = new List<PaAchievement>(achievements),
            };

            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            });

            var payload = PaPayloadMerge.Merge(JObject.FromObject(dto, serializer), existing);
            var json = payload.ToString(Formatting.Indented);

            var path = Path.Combine(CacheDir, game.Id.ToString("D") + ".json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }

            log?.Debug($"HydraSync: wrote PA cache for {game.Name} ({achievements.Count} achievements" +
                       (existing == null ? "" : ", merged with existing PA data") + ")");
        }

        private class PaDataDto
        {
            public DateTime LastUpdatedUtc { get; set; }
            public string ProviderKey { get; set; }
            public string LibrarySourceName { get; set; }
            public bool HasAchievements { get; set; }
            public string GameName { get; set; }
            public int AppId { get; set; }
            public string ProviderGameKey { get; set; }
            public Guid PlayniteGameId { get; set; }
            public List<PaAchievement> Achievements { get; set; }
        }
    }
}