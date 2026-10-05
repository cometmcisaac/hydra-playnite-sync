using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using LevelDbManaged;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace HydraSync.Hydra
{
    /// <summary>
    /// Reads game records from Hydra Launcher's local LevelDB
    /// (%APPDATA%\Hydra\hydra-db, classic-level, JSON values).
    ///
    /// LevelDb.Managed is used because it is pure managed code: no native DLLs to
    /// ship, it can read while Hydra holds the database open (lock-free), and it
    /// handles snappy-compressed SSTs written by classic-level (verified against a
    /// real classic-level fixture). If the direct open still fails (e.g. mid-flush),
    /// we fall back to opening a snapshot copy of the directory.
    /// </summary>
    public static class HydraDbReader
    {
        public static IReadOnlyList<HydraGame> ReadGames(string dbPath, ILogger log)
        {
            if (string.IsNullOrEmpty(dbPath) || !Directory.Exists(dbPath))
            {
                return new List<HydraGame>();
            }

            try
            {
                return ReadCore(dbPath, log);
            }
            catch (Exception primary)
            {
                log?.Warn($"Direct read of Hydra DB failed ({primary.Message}), retrying on a snapshot copy.");
            }

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "HydraSync", Guid.NewGuid().ToString("N"));
                try
                {
                    CopyDatabase(dbPath, tmp);
                    return ReadCore(tmp, log);
                }
                catch (Exception ex)
                {
                    log?.Warn($"Hydra DB snapshot read attempt {attempt} failed: {ex.Message}");
                    Thread.Sleep(300);
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    }
                    catch
                    {
                        // best effort cleanup
                    }
                }
            }

            throw new IOException($"Unable to read Hydra database at {dbPath}");
        }

        private static IReadOnlyList<HydraGame> ReadCore(string path, ILogger log)
        {
            var games = new List<HydraGame>();

            using (var db = LevelDb.OpenReadOnly(path, new LevelDbOptions()))
            {
                foreach (var rec in db.Enumerate())
                {
                    var value = rec.Value;
                    if (value == null || value.Length == 0) continue;

                    JObject obj;
                    try
                    {
                        var json = Encoding.UTF8.GetString(value);
                        obj = JObject.Parse(json);
                    }
                    catch
                    {
                        // Not JSON (or truncated) - not a record we care about.
                        continue;
                    }

                    var game = HydraGame.FromJson(obj);
                    if (game != null) games.Add(game);
                }
            }

            log?.Debug($"HydraSync: read {games.Count} game records from Hydra DB");
            return games;
        }

        private static void CopyDatabase(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                // LOCK is held open by LevelDB; copying it fails on Windows.
                if (string.Equals(name, "LOCK", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(destination, name), true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, "LOCK", StringComparison.OrdinalIgnoreCase)) continue;
                CopyDatabase(dir, Path.Combine(destination, name));
            }
        }
    }
}
