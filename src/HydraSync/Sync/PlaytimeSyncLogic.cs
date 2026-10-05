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
        /// v1.x builds recorded no original; for entries written by those additive builds,
        /// recover it by subtracting the cumulative seconds they added (their deltas
        /// telescoped to LastHydraMs + MsCarry, in ms). Falls back to the current value
        /// when that recovery isn't possible or would go negative.
        /// </summary>
        public static ulong CaptureOriginal(ulong currentPlaytime, long legacyAddedMs)
        {
            if (legacyAddedMs <= 0) return currentPlaytime;
            var added = legacyAddedMs / 1000;
            if (added > 0 && (ulong)added <= currentPlaytime)
            {
                return currentPlaytime - (ulong)added;
            }

            return currentPlaytime;
        }

        /// <summary>
        /// Restored playtime for undo. Exact when an original was captured; approximate for
        /// legacy additive entries (recovered via <paramref name="legacyAddedMs"/>); unchanged
        /// when neither is available. <paramref name="approximate"/> reports which path ran.
        /// </summary>
        public static ulong Restore(ulong currentPlaytime, ulong? originalPlaytimeSecs,
            long legacyAddedMs, out bool approximate)
        {
            if (originalPlaytimeSecs.HasValue)
            {
                approximate = false;
                return originalPlaytimeSecs.Value;
            }

            if (legacyAddedMs > 0)
            {
                var added = legacyAddedMs / 1000;
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
