using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements
{
    /// <summary>
    /// Stand-in for the Playnite Achievements extension, mirroring only the members
    /// <c>PlayniteAchievementsBridge</c> reaches for: a static <c>Instance</c>, a public
    /// <c>CacheManager</c> property, and a cache manager that exposes LoadGameData,
    /// NotifyCacheInvalidated(IReadOnlyList&lt;Guid&gt;) and a *private* importer field whose
    /// type has a public ImportIfNeeded(). Names must match the real extension.
    /// </summary>
    public class PlayniteAchievementsPlugin
    {
        public static PlayniteAchievementsPlugin Instance { get; set; }

        public FakeCacheManager CacheManager { get; set; }

        /// <summary>Set to false to simulate an extension that hasn't finished starting up.</summary>
        public static bool Ready = true;

        /// <summary>Make ImportIfNeeded() throw (simulates an unhappy import).</summary>
        public static bool ThrowOnImport = false;

        /// <summary>Creates the fake as if Playnite had just created the extension.</summary>
        public static PlayniteAchievementsPlugin Start()
        {
            Ready = true;
            ThrowOnImport = false;
            FakeLegacyJsonCacheImporter.Calls = 0;
            var plugin = new PlayniteAchievementsPlugin { CacheManager = new FakeCacheManager() };
            Instance = plugin;
            return plugin;
        }
    }

    public sealed class FakeCacheManager
    {
        // The real extension keeps its importer in a private readonly field.
        private readonly FakeLegacyJsonCacheImporter _importer = new FakeLegacyJsonCacheImporter();

        public int NotifyCalls;
        public int NotifyAllCalls;
        public readonly List<Guid> LastNotified = new List<Guid>();
        public readonly Dictionary<string, JObject> Data = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        public object LoadGameData(string key)
        {
            JObject value;
            return Data.TryGetValue(key, out value) ? (object)value : null;
        }

        public void NotifyCacheInvalidated(IReadOnlyList<Guid> changedGameIds)
        {
            NotifyCalls++;
            LastNotified.Clear();
            if (changedGameIds == null) return;
            foreach (var id in changedGameIds) LastNotified.Add(id);
        }

        public void NotifyCacheInvalidated()
        {
            NotifyAllCalls++;
        }
    }

    public sealed class FakeLegacyJsonCacheImporter
    {
        public static int Calls;

        public void ImportIfNeeded()
        {
            Calls++;
            if (PlayniteAchievementsPlugin.ThrowOnImport)
            {
                throw new InvalidOperationException("import exploded");
            }
        }
    }
}
/// <summary>
/// Stand-in for a future/incompatible Playnite Achievements build: the plugin type exists but
/// exposes no cache manager, so the bridge must degrade to "unavailable" instead of throwing.
/// </summary>
public static class ShimPluginNoCache
{
    public static object Instance { get; private set; }

    public static object Start()
    {
        Instance = new object();
        return Instance;
    }
}
