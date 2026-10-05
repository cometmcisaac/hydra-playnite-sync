namespace HydraSync.Sync
{
    /// <summary>
    /// What Hydra Sync is allowed to sync for a single Playnite game.
    /// Stored per game in sync_state.json; <see cref="Both"/> is the default
    /// (no override stored), which mirrors the global settings behaviour.
    /// </summary>
    public enum GameSyncMode
    {
        /// <summary>Sync playtime and achievements (default).</summary>
        Both = 0,

        /// <summary>Only sync playtime for this game.</summary>
        PlaytimeOnly = 1,

        /// <summary>Only sync achievements for this game.</summary>
        AchievementsOnly = 2,
    }

    /// <summary>
    /// Combines the global settings switches with a per-game override.
    /// Global switches stay master switches: a per-game mode can only narrow
    /// what happens for that game, never re-enable something switched off globally.
    /// </summary>
    public static class GameSyncModeLogic
    {
        public static void Resolve(
            bool globalPlaytime,
            bool globalAchievements,
            GameSyncMode mode,
            out bool playtime,
            out bool achievements)
        {
            playtime = globalPlaytime && mode != GameSyncMode.AchievementsOnly;
            achievements = globalAchievements && mode != GameSyncMode.PlaytimeOnly;
        }

        public static string Describe(GameSyncMode mode)
        {
            switch (mode)
            {
                case GameSyncMode.PlaytimeOnly:
                    return "playtime only";
                case GameSyncMode.AchievementsOnly:
                    return "achievements only";
                default:
                    return "playtime and achievements";
            }
        }
    }
}