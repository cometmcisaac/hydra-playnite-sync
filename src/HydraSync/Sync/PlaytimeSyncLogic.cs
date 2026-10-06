using System;

namespace HydraSync.Sync
{
    /// <summary>
    /// Pure playtime math for the replace-if-larger sync mode and its undo.
    /// Kept free of Playnite SDK types so it can be unit-tested directly.
    ///
    /// Semantics: Hydra's cumulative total wins only when it exceeds Playnite's current
    /// playtime; otherwise Playnite's value is left untouched.
    /// </summary>
    public static class PlaytimeSyncLogic
    {
        /// <summary>Hydra's playtime in whole seconds.</summary>
        public static long HydraSeconds(long hydraMs)
        {
            return hydraMs <= 0 ? 0 : hydraMs / 1000;
        }

        /// <summary>True when Hydra's total should replace Playnite's current playtime.</summary>
        public static bool ShouldRaise(ulong currentPlaytime, long hydraSeconds)
        {
            return hydraSeconds > 0 && (ulong)hydraSeconds > currentPlaytime;
        }

        /// <summary>
        /// Original (pre-plugin) playtime for a game the plugin is about to modify.
        /// The current value is the answer whenever the plugin hasn't touched the game yet.
        /// For entries that carry no original but do record how much was added to the game,
        /// the pre-plugin value is recovered by subtracting that recorded amount (their deltas
        /// telescoped to LastHydraMs + MsCarry, in ms). Falls back to the current value
        /// when that recovery isn't possible or would go negative.
        /// </summary>
        public static ulong CaptureOriginal(ulong currentPlaytime, long recordedAddedMs)
        {
            if (recordedAddedMs <= 0) return currentPlaytime;
            var added = recordedAddedMs / 1000;
            if (added > 0 && (ulong)added <= currentPlaytime)
            {
                return currentPlaytime - (ulong)added;
            }

            return currentPlaytime;
        }

        /// <summary>
        /// Restored playtime for undo. Exact when an original was captured; approximate when
        /// only a recorded added amount is available (recovered via
        /// <paramref name="recordedAddedMs"/>); unchanged when neither is available.
        /// <paramref name="approximate"/> reports which path ran.
        /// </summary>
        public static ulong Restore(ulong currentPlaytime, ulong? originalPlaytimeSecs,
            long recordedAddedMs, out bool approximate)
        {
            if (originalPlaytimeSecs.HasValue)
            {
                approximate = false;
                return originalPlaytimeSecs.Value;
            }

            if (recordedAddedMs > 0)
            {
                var added = recordedAddedMs / 1000;
                if (added > 0 && (ulong)added <= currentPlaytime)
                {
                    approximate = true;
                    return currentPlaytime - (ulong)added;
                }
            }

            approximate = false;
            return currentPlaytime;
        }
    }
}
