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

        /// <summary>
        /// Undo value when the plugin knows the exact number it last applied. Only the
        /// sync's own contribution is removed (<c>lastApplied - original</c>), so playtime
        /// Playnite recorded after the sync survives. Falls back to <see cref="Restore"/>
        /// when either recorded value is missing.
        /// </summary>
        public static ulong RestoreApplied(ulong currentPlaytime, ulong? originalPlaytimeSecs,
            ulong? lastAppliedPlaytimeSecs, long recordedAddedMs, out bool approximate)
        {
            if (lastAppliedPlaytimeSecs.HasValue && originalPlaytimeSecs.HasValue)
            {
                var applied = lastAppliedPlaytimeSecs.Value;
                var original = originalPlaytimeSecs.Value;
                if (applied > original)
                {
                    var contribution = applied - original;
                    if (currentPlaytime >= contribution)
                    {
                        approximate = false;
                        return currentPlaytime - contribution;
                    }

                    // Playtime is now lower than what the sync added (hand-edited, or the
                    // game was removed and re-added) - the original is the closest value
                    // we can defend, and never raises playtime above what is there now.
                    approximate = true;
                    return original <= currentPlaytime ? original : currentPlaytime;
                }

                // Nothing was ever added (applied == original): undo is a no-op.
                approximate = false;
                return currentPlaytime;
            }

            return Restore(currentPlaytime, originalPlaytimeSecs, recordedAddedMs, out approximate);
        }

        /// <summary>Result of planning an additive playtime update.</summary>
        public struct PlaytimePlan
        {
            /// <summary>True when <see cref="NewPlaytime"/> should be written to the game.</summary>
            public bool ShouldApply;

            public ulong NewPlaytime;

            /// <summary>Seconds added to the game (0 when nothing is applied).</summary>
            public long AddedSeconds;

            /// <summary>Hydra total (ms) to remember as the baseline for the next sync.</summary>
            public long BaselineHydraMs;

            /// <summary>True when this pass only recorded a baseline and changed no playtime.</summary>
            public bool BaselineOnly;
        }

        /// <summary>
        /// Additive mode: only Hydra's playtime gained since <paramref name="baselineHydraMs"/>
        /// is added to the game's current value, so nothing is ever doubled. The first pass for
        /// a game records the baseline and changes nothing; a Hydra total below the baseline
        /// (its counter was reset) re-baselines instead of adding.
        /// </summary>
        public static PlaytimePlan PlanAdditive(ulong currentPlaytime, long hydraMs,
            bool hasBaseline, long baselineHydraMs)
        {
            var hydra = hydraMs < 0 ? 0 : hydraMs;

            if (!hasBaseline)
            {
                return new PlaytimePlan { BaselineHydraMs = hydra, BaselineOnly = true };
            }

            if (hydra <= baselineHydraMs)
            {
                return new PlaytimePlan
                {
                    BaselineHydraMs = hydra,
                    BaselineOnly = hydra != baselineHydraMs,
                };
            }

            var seconds = (hydra - baselineHydraMs) / 1000;
            if (seconds <= 0)
            {
                return new PlaytimePlan { BaselineHydraMs = hydra };
            }

            return new PlaytimePlan
            {
                ShouldApply = true,
                NewPlaytime = currentPlaytime + (ulong)seconds,
                AddedSeconds = seconds,
                BaselineHydraMs = hydra,
            };
        }
    }
}
