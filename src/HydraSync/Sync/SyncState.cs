using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace HydraSync.Sync
{
    /// <summary>Per-Hydra-game sync bookkeeping.</summary>
    public class HydraGameState
    {
        /// <summary>Playnite game GUID the Hydra record is currently mapped to.</summary>
        public string PlayniteGameId { get; set; }

        /// <summary>Hydra's cumulative playtime (ms) at the end of the last successful sync.</summary>
        public long LastHydraMs { get; set; }

        /// <summary>Sub-second remainder carried between syncs (ms).</summary>
        public long MsCarry { get; set; }

        /// <summary>
        /// Playtime (seconds) the game had before this plugin first modified it —
        /// the value "Undo playtime changes" restores. Null when the plugin has
        /// never changed this game's playtime (or after an undo).
        /// </summary>
        public ulong? OriginalPlaytimeSecs { get; set; }

        /// <summary>Hash of the last achievement payload written for this game (skip unchanged rewrites).</summary>
        public string AchievementFingerprint { get; set; }
    }

    /// <summary>
    /// Persisted plugin state (sync_state.json in the plugin data directory).
    /// </summary>
    public class SyncState
    {
        // Keys are stored lowercased ("steam:1091500") so the default comparer is fine.
        public Dictionary<string, HydraGameState> Games { get; set; } =
            new Dictionary<string, HydraGameState>(StringComparer.OrdinalIgnoreCase);

        public bool NotifiedPaImport { get; set; }

        public DateTime? LastSyncUtc { get; set; }

        public HydraGameState GetOrAdd(string key)
        {
            if (!Games.TryGetValue(key, out var state))
            {
                state = new HydraGameState();
                Games[key] = state;
            }

            return state;
        }

        public static SyncState Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var state = JsonConvert.DeserializeObject<SyncState>(File.ReadAllText(path));
                    if (state != null)
                    {
                        if (state.Games == null)
                        {
                            state.Games = new Dictionary<string, HydraGameState>(StringComparer.OrdinalIgnoreCase);
                        }

                        return state;
                    }
                }
            }
            catch
            {
                // Corrupt state - start fresh rather than blocking sync.
            }

            return new SyncState();
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
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
        }
    }
}
