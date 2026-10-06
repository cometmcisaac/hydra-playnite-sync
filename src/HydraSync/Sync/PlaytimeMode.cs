namespace HydraSync.Sync
{
    /// <summary>
    /// How Hydra's playtime is applied to a matched Playnite game.
    /// Stored in the plugin settings as a whole number.
    /// </summary>
    public enum PlaytimeMode
    {
        /// <summary>
        /// Default: Hydra's cumulative total replaces Playnite's playtime whenever it is
        /// strictly higher. Never adds on top of what Playnite already recorded.
        /// </summary>
        HydraWins = 0,

        /// <summary>
        /// Adds only the playtime Hydra gained since the previous sync, leaving whatever
        /// Playnite recorded on top. The first sync after switching to this mode records a
        /// baseline and changes nothing, so already-synced games are never doubled.
        /// </summary>
        AddHydraIncrements = 1,
    }

    /// <summary>User-facing labels for <see cref="PlaytimeMode"/>.</summary>
    public static class PlaytimeModeLogic
    {
        public static string Describe(PlaytimeMode mode)
        {
            return mode == PlaytimeMode.AddHydraIncrements
                ? "add Hydra's new playtime since the previous sync"
                : "Hydra wins (replace when larger)";
        }
    }
}