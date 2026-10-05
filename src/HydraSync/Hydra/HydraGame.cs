using System;
using Newtonsoft.Json.Linq;

namespace HydraSync.Hydra
{
    /// <summary>
    /// A game record parsed from Hydra Launcher's local LevelDB (value JSON).
    /// </summary>
    public class HydraGame
    {
        public string Title { get; set; }
        public string ObjectId { get; set; }
        public string Shop { get; set; }
        public long PlayTimeInMilliseconds { get; set; }
        /// <summary>Local time of the last Hydra session (null when never played).</summary>
        public DateTime? LastTimePlayed { get; set; }
        public bool IsDeleted { get; set; }
        public string Source { get; set; }
        public string ExecutablePath { get; set; }
        public long AchievementCount { get; set; }
        public long UnlockedAchievementCount { get; set; }
        public bool Favorite { get; set; }

        /// <summary>Stable key used for sync-state tracking (e.g. "steam:1091500").</summary>
        public string Key => ((Shop ?? "") + ":" + (ObjectId ?? "")).ToLowerInvariant();

        public bool HasNumericId
        {
            get
            {
                if (string.IsNullOrEmpty(ObjectId)) return false;
                foreach (var c in ObjectId)
                {
                    if (!char.IsDigit(c)) return false;
                }

                return ObjectId.Length > 0;
            }
        }

        /// <summary>
        /// Returns null when the value blob is not a game record (auth, prefs, etc.)
        /// or the record is deleted/invalid.
        /// </summary>
        public static HydraGame FromJson(JObject o)
        {
            if (o == null) return null;

            var playTime = o["playTimeInMilliseconds"];
            var objectId = o["objectId"];
            var shop = o["shop"];
            if (IsNull(playTime) || IsNull(objectId) || IsNull(shop)) return null;

            long ms;
            try
            {
                ms = playTime.Value<long>();
            }
            catch
            {
                return null;
            }

            var title = TokenToString(o["title"]);
            if (string.IsNullOrWhiteSpace(title)) return null;

            bool deleted;
            try
            {
                deleted = o["isDeleted"]?.Value<bool>() == true;
            }
            catch
            {
                deleted = false;
            }

            if (deleted) return null;

            return new HydraGame
            {
                Title = title.Trim(),
                ObjectId = objectId.ToString().Trim(),
                Shop = (TokenToString(shop) ?? "").Trim().ToLowerInvariant(),
                PlayTimeInMilliseconds = ms,
                LastTimePlayed = ParseDate(o["lastTimePlayed"]),
                IsDeleted = false,
                Source = TokenToString(o["source"]),
                ExecutablePath = TokenToString(o["executablePath"]),
                AchievementCount = TryLong(o["achievementCount"]),
                UnlockedAchievementCount = TryLong(o["unlockedAchievementCount"]),
                Favorite = TryBool(o["favorite"]),
            };
        }

        /// <summary>
        /// Hydra stores lastTimePlayed as a JS Date serialized to an ISO string (or null).
        /// Accepts ISO strings, JToken dates, and epoch-milliseconds numbers.
        /// Returns local time (Playnite's LastActivity convention).
        /// </summary>
        public static DateTime? ParseDate(JToken t)
        {
            if (IsNull(t)) return null;

            if (t.Type == JTokenType.Date)
            {
                var d = t.Value<DateTime>();
                switch (d.Kind)
                {
                    case DateTimeKind.Utc:
                        return d.ToLocalTime();
                    case DateTimeKind.Local:
                        return d;
                    default:
                        return DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();
                }
            }

            if (t.Type == JTokenType.Integer)
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(t.Value<long>()).LocalDateTime;
                }
                catch
                {
                    return null;
                }
            }

            var s = t.ToString();
            if (DateTimeOffset.TryParse(s, out var dto))
            {
                return dto.LocalDateTime;
            }

            return null;
        }

        private static bool IsNull(JToken t) => t == null || t.Type == JTokenType.Null;

        private static string TokenToString(JToken t)
        {
            if (IsNull(t)) return null;
            if (t.Type == JTokenType.String) return (string)t;
            return t.ToString();
        }

        private static long TryLong(JToken t)
        {
            if (IsNull(t)) return 0;
            try
            {
                return t.Value<long>();
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryBool(JToken t)
        {
            if (IsNull(t)) return false;
            try
            {
                return t.Value<bool>();
            }
            catch
            {
                return false;
            }
        }
    }
}
