using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace HydraSync.Achievements
{
    /// <summary>
    /// Merges a payload Hydra Sync is about to write with the achievement data
    /// Playnite Achievements already has for the same game.
    ///
    /// Without this the sync overwrites the whole record, which throws away entries from
    /// another provider and can turn an achievement Playnite knows as unlocked back into
    /// locked. Rules:
    ///   - an unlock is never downgraded to locked;
    ///   - entries only Hydra Sync knows about are added, entries only Playnite knows
    ///     about are kept;
    ///   - names/descriptions/icons are refreshed when both sides come from the same
    ///     provider key, otherwise existing values win and ours only fill gaps;
    ///   - the provider key and app id of existing data are never downgraded.
    ///
    /// Pure JSON manipulation (no Playnite SDK types) so it can be unit-tested directly.
    /// </summary>
    public static class PaPayloadMerge
    {
        private static readonly string[] MetadataFields =
        {
            "DisplayName", "Description", "UnlockedIconPath", "LockedIconPath", "Hidden",
        };

        /// <summary>
        /// <paramref name="ours"/> is the payload Hydra Sync built, <paramref name="existing"/>
        /// is Playnite Achievements' current record for the game (null when it has none).
        /// </summary>
        public static JObject Merge(JObject ours, JObject existing)
        {
            if (ours == null) return existing;
            if (existing == null || existing.Count == 0) return ours;

            var merged = (JObject)existing.DeepClone();

            // Both sides describe the same game, so the freshest values win for the fields
            // that describe *our* write rather than what Playnite already knows.
            merged["LastUpdatedUtc"] = ours["LastUpdatedUtc"];
            merged["HasAchievements"] = ours["HasAchievements"] ?? true;
            merged["PlayniteGameId"] = ours["PlayniteGameId"];

            var existingKey = Text(existing["ProviderKey"]);
            var ourKey = Text(ours["ProviderKey"]);
            var sameSource = !string.IsNullOrEmpty(existingKey) &&
                             !string.IsNullOrEmpty(ourKey) &&
                             string.Equals(existingKey, ourKey, StringComparison.OrdinalIgnoreCase);

            // Provider ownership belongs to whatever is already stored - a real provider's
            // key must not be replaced by ours, and "Unmapped" is only a placeholder.
            merged["ProviderKey"] = string.IsNullOrEmpty(existingKey) ||
                                    string.Equals(existingKey, "Unmapped", StringComparison.OrdinalIgnoreCase)
                ? ours["ProviderKey"]
                : existing["ProviderKey"];

            foreach (var field in new[] { "LibrarySourceName", "GameName", "ProviderGameKey" })
            {
                if (IsBlank(merged[field])) merged[field] = ours[field];
            }

            if (Number(merged["AppId"]) <= 0 && Number(ours["AppId"]) > 0)
            {
                merged["AppId"] = ours["AppId"];
            }

            merged["Achievements"] = MergeAchievements(
                existing["Achievements"] as JArray, ours["Achievements"] as JArray, sameSource);

            return merged;
        }

        /// <summary>
        /// Merges two achievement lists by API name (case-insensitive). Existing entries keep
        /// their position; new ones are appended in the incoming order.
        /// </summary>
        public static JArray MergeAchievements(JArray existing, JArray incoming, bool sameSource)
        {
            var result = new JArray();
            var byName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            if (existing != null)
            {
                foreach (var token in existing)
                {
                    if (!(token is JObject entry)) continue;
                    var name = Text(entry["ApiName"]);
                    if (string.IsNullOrEmpty(name) || byName.ContainsKey(name)) continue;

                    // Clone: JArray.Add would copy the token anyway, and the copy is what
                    // MergeInto has to mutate for the changes to be visible.
                    var clone = (JObject)entry.DeepClone();
                    byName[name] = clone;
                    result.Add(clone);
                }
            }

            if (incoming == null) return result;

            foreach (var token in incoming)
            {
                if (!(token is JObject entry)) continue;
                var name = Text(entry["ApiName"]);
                if (string.IsNullOrEmpty(name)) continue;

                if (byName.TryGetValue(name, out var target))
                {
                    MergeInto(target, entry, sameSource);
                    continue;
                }

                var clone = (JObject)entry.DeepClone();
                byName[name] = clone;
                result.Add(clone);
            }

            return result;
        }

        private static void MergeInto(JObject target, JObject incoming, bool sameSource)
        {
            // Never downgrade an unlock. Ours wins when it has an unlock; when ours says
            // "locked" (or knows nothing) the existing state is kept as-is.
            if (IsTrue(incoming["Unlocked"]))
            {
                target["Unlocked"] = true;
                if (incoming["UnlockTimeUtc"] != null && incoming["UnlockTimeUtc"].Type != JTokenType.Null)
                {
                    target["UnlockTimeUtc"] = incoming["UnlockTimeUtc"];
                }
            }
            else if (target["Unlocked"] == null)
            {
                target["Unlocked"] = false;
            }

            foreach (var field in MetadataFields)
            {
                var ours = incoming[field];
                if (ours == null || ours.Type == JTokenType.Null) continue;
                if (IsBlank(ours) && !(field == "Hidden")) continue;

                if (sameSource || IsBlank(target[field]))
                {
                    target[field] = ours;
                }
            }
        }

        private static bool IsBlank(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return true;
            if (token.Type == JTokenType.Boolean) return false;
            if (token.Type == JTokenType.Integer) return false;
            var text = token.ToString();
            return string.IsNullOrWhiteSpace(text);
        }

        private static bool IsTrue(JToken token)
        {
            if (token == null) return false;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            return string.Equals(token.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string Text(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static long Number(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            long value;
            return long.TryParse(token.ToString(), out value) ? value : 0;
        }
    }
}