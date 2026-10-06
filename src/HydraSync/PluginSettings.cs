using System;
using System.Collections.Generic;
using System.Linq;
using HydraSync.Sync;
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

        /// <summary>
        /// How Hydra's playtime is applied: <see cref="PlaytimeMode.HydraWins"/> (default) only
        /// replaces Playnite's value when Hydra's total is higher, or
        /// <see cref="PlaytimeMode.AddHydraIncrements"/> which adds just the playtime Hydra gained
        /// since the last sync. Additive mode records a baseline on the first pass, so already
        /// synced games are never doubled.
        /// </summary>
        public PlaytimeMode PlaytimeMode { get; set; } = PlaytimeMode.HydraWins;

        /// <summary>
        /// Opt-in automatic syncing: one pass shortly after Playnite starts, then one
        /// every <see cref="SyncIntervalMinutes"/> minutes. Off by default - syncing only
        /// happens manually (main menu / game menu "Sync now") until this is enabled.
        /// </summary>
        public bool AutoSync { get; set; } = false;

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

        /// <summary>
        /// Check the project's GitHub releases shortly after Playnite starts and offer
        /// one-click download/install when a newer version is out.
        /// </summary>
        public bool CheckForUpdates { get; set; } = true;

        /// <summary>
        /// After a sync raises a game's playtime, ask the HowLongToBeat extension to submit the
        /// new total for that game - the same call it makes itself when a game exits. Opt-in:
        /// it writes to a third-party account, and only games this run actually raised are pushed.
        /// </summary>
        public bool PushPlaytimeToHowLongToBeat { get; set; } = false;

        /// <summary>
        /// Merge with the achievement data Playnite Achievements already has instead of
        /// replacing it, so entries from other providers and unlocks it knows about survive.
        /// </summary>
        public bool MergeWithPlayniteAchievements { get; set; } = true;

        /// <summary>
        /// Ask Playnite Achievements to import new data as soon as it is written, instead of
        /// waiting for the next Playnite start. When the extension can't be reached the data is
        /// still written and Playnite imports it at the next start, as before.
        /// </summary>
        public bool ImportAchievementsImmediately { get; set; } = true;

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
            current.PlaytimeMode = _backup.PlaytimeMode;
            current.AutoSync = _backup.AutoSync;
            current.SyncIntervalMinutes = _backup.SyncIntervalMinutes;
            current.HydraDataDir = _backup.HydraDataDir;
            current.FetchSteamSchema = _backup.FetchSteamSchema;
            current.SteamWebApiKey = _backup.SteamWebApiKey;
            current.CheckForUpdates = _backup.CheckForUpdates;
            current.PushPlaytimeToHowLongToBeat = _backup.PushPlaytimeToHowLongToBeat;
            current.MergeWithPlayniteAchievements = _backup.MergeWithPlayniteAchievements;
            current.ImportAchievementsImmediately = _backup.ImportAchievementsImmediately;
        }

        public void EndEdit()
        {
            if (_plugin == null)
            {
                return;
            }

            _plugin.SavePluginSettings(_plugin.Settings);

            // Auto-sync / interval changes should take effect without a Playnite restart.
            _plugin.RescheduleTimer();
        }
    }
}
