using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;

namespace HydraSync
{
    /// <summary>
    /// Persisted plugin settings (stored as config.json in the plugin's data directory).
    /// </summary>
    public class PluginSettings : ISettings
    {
        private HydraSyncPlugin _plugin;
        private PluginSettings _backup;

        public PluginSettings()
        {
        }

        /// <summary>
        /// Called by the plugin after LoadPluginSettings (deserialization bypasses ctors with args).
        /// </summary>
        internal void AttachPlugin(HydraSyncPlugin plugin)
        {
            _plugin = plugin;
        }

        // --- settings ---

        public bool SyncPlaytime { get; set; } = true;
        public bool SyncAchievements { get; set; } = true;
        public bool WriteToPlayniteAchievements { get; set; } = true;
        public int SyncIntervalMinutes { get; set; } = 15;

        /// <summary>
        /// Optional override for Hydra's LevelDB folder (auto-detects %APPDATA%\Hydra and
        /// %APPDATA%\hydralauncher when empty).
        /// </summary>
        public string HydraDataDir { get; set; } = string.Empty;

        /// <summary>
        /// Fetch achievement names/descriptions/icons from the Steam store API when a game has an AppID.
        /// </summary>
        public bool FetchSteamSchema { get; set; } = true;

        /// <summary>
        /// Optional Steam Web API key (https://steamcommunity.com/dev/apikey). When set, the full
        /// achievement schema (API names, descriptions, icons) is fetched from GetSchemaForGame —
        /// used as fallback when the game has no local steam_settings/achievements.json.
        /// </summary>
        public string SteamWebApiKey { get; set; } = string.Empty;

        public PluginSettings GetClone()
        {
            return (PluginSettings)MemberwiseClone();
        }

        // --- ISettings ---

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (SyncIntervalMinutes < 1 || SyncIntervalMinutes > 24 * 60)
            {
                errors.Add("Sync interval must be between 1 and 1440 minutes.");
            }

            return errors.Count == 0;
        }

        public void BeginEdit()
        {
            _backup = _plugin?.Settings.GetClone();
        }

        public void CancelEdit()
        {
            if (_backup == null || _plugin == null)
            {
                return;
            }

            var current = _plugin.Settings;
            current.SyncPlaytime = _backup.SyncPlaytime;
            current.SyncAchievements = _backup.SyncAchievements;
            current.WriteToPlayniteAchievements = _backup.WriteToPlayniteAchievements;
            current.SyncIntervalMinutes = _backup.SyncIntervalMinutes;
            current.HydraDataDir = _backup.HydraDataDir;
            current.FetchSteamSchema = _backup.FetchSteamSchema;
            current.SteamWebApiKey = _backup.SteamWebApiKey;
        }

        public void EndEdit()
        {
            if (_plugin == null)
            {
                return;
            }

            _plugin.SavePluginSettings(_plugin.Settings);
        }
    }
}
