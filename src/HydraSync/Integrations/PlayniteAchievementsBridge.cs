using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace HydraSync.Integrations
{
    /// <summary>Outcome of handing a freshly written cache file to Playnite Achievements.</summary>
    public enum PaImportResult
    {
        /// <summary>Imported and the extension was told to refresh the game.</summary>
        Imported,

        /// <summary>Playnite Achievements (or the pieces we need) isn't available.</summary>
        Unavailable,

        /// <summary>It is there, but the import threw.</summary>
        Failed,
    }

    /// <summary>
    /// Talks to the Playnite Achievements extension without referencing it.
    ///
    /// Hydra Sync writes per-game JSON into PA's achievement_cache folder. PA normally picks
    /// those files up in its legacy importer when Playnite starts, which meant a restart after
    /// every change. This bridge calls the very same importer (plus the cache-invalidated
    /// notification that makes the game view repaint) right after a write, so unlocks show up
    /// immediately. Everything is late-bound by name and every failure degrades to "let Playnite
    /// import it on the next start" - the same behaviour as before this bridge existed.
    /// </summary>
    public static class PlayniteAchievementsBridge
    {
        public const string DefaultAssemblyName = "PlayniteAchievements";

        private const string DefaultPluginTypeName = "PlayniteAchievements.PlayniteAchievementsPlugin";
        private const string DefaultCacheManagerProperty = "CacheManager";

        /// <summary>Don't hammer Playnite for the extension's internals while it's missing.</summary>
        private const int ResolveRetrySeconds = 60;

        private static bool _resolved;
        private static string _unavailableReason;
        private static DateTime _lastResolveAttemptUtc = DateTime.MinValue;

        private static object _cache;
        private static object _importer;
        private static MethodInfo _loadGameData;
        private static MethodInfo _importIfNeeded;
        private static MethodInfo _notifyInvalidated;
        private static PropertyInfo _instanceProperty;
        private static PropertyInfo _cacheManagerProperty;

        // Test seams: the harness compiles this file without the extension, so tests inject
        // their own resolver (same pattern as HowLongToBeatBridge).
        internal static Func<string, Assembly> AssemblyResolver = DefaultAssemblyResolver;
        internal static Func<Assembly, Type> PluginTypeResolver = DefaultPluginTypeResolver;

        private static Assembly DefaultAssemblyResolver(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return asm;
                }
            }

            return null;
        }

        private static Type DefaultPluginTypeResolver(Assembly assembly)
        {
            var type = assembly.GetType(DefaultPluginTypeName);
            if (type != null) return type;

            foreach (var candidate in assembly.GetTypes())
            {
                if (typeof(object).IsAssignableFrom(candidate) &&
                    candidate.Name.IndexOf("Plugin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>True when the extension and the members we call are all present.</summary>
        public static bool IsAvailable
        {
            get
            {
                string reason;
                return TryResolve(out reason);
            }
        }

        /// <summary>Why the bridge is unavailable (null when it is available).</summary>
        public static string UnavailableReason
        {
            get
            {
                string reason;
                return TryResolve(out reason) ? null : _unavailableReason;
            }
        }

        /// <summary>
        /// Playnite Achievements' current record for a game as JSON, or null when it has none
        /// (or the extension isn't reachable). Used to merge instead of overwrite.
        /// </summary>
        public static JObject TryReadGameData(Guid gameId, ILogger log)
        {
            string reason;
            if (!TryResolve(out reason))
            {
                log?.Debug("HydraSync: PA bridge unavailable for read: " + _unavailableReason);
                return null;
            }

            if (_loadGameData == null)
            {
                log?.Debug("HydraSync: PA bridge has no LoadGameData - skipping merge read");
                return null;
            }

            try
            {
                var data = Invoke(() => _loadGameData.Invoke(_cache, new object[] { gameId.ToString("D") }));
                if (data == null) return null;
                return data as JObject ?? JObject.FromObject(data);
            }
            catch (Exception ex)
            {
                log?.Debug("HydraSync: PA bridge read failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Runs Playnite Achievements' legacy importer (which picks up the file we just wrote)
        /// and asks it to refresh <paramref name="gameId"/>. The importer runs on the calling
        /// thread - the sync is off the UI thread already - while the refresh notification is
        /// marshalled to the UI thread because Playnite raises it to its own views.
        /// </summary>
        public static PaImportResult ImportNow(Guid gameId, Action<Action> runOnUi, ILogger log)
        {
            string reason;
            if (!TryResolve(out reason))
            {
                log?.Debug("HydraSync: PA bridge unavailable for import: " + _unavailableReason);
                return PaImportResult.Unavailable;
            }

            if (_importIfNeeded == null)
            {
                return PaImportResult.Unavailable;
            }

            try
            {
                Invoke(() => _importIfNeeded.Invoke(_importer, null));
            }
            catch (Exception ex)
            {
                log?.Error(ex, "HydraSync: PA bridge import failed");
                return PaImportResult.Failed;
            }

            if (_notifyInvalidated != null && runOnUi != null)
            {
                try
                {
                    runOnUi(() => Invoke(() =>
                        _notifyInvalidated.Invoke(_cache, new object[] { new List<Guid> { gameId } })));
                }
                catch (Exception ex)
                {
                    // The data is imported at this point; a stale view just needs a refresh.
                    log?.Debug("HydraSync: PA bridge could not request a refresh: " + ex.Message);
                }
            }

            return PaImportResult.Imported;
        }

        /// <summary>Drops all cached reflection state (test seam).</summary>
        internal static void ResetForTests()
        {
            _resolved = false;
            _unavailableReason = null;
            _lastResolveAttemptUtc = DateTime.MinValue;
            _cache = null;
            _importer = null;
            _loadGameData = null;
            _importIfNeeded = null;
            _notifyInvalidated = null;
            _instanceProperty = null;
            _cacheManagerProperty = null;
            AssemblyResolver = DefaultAssemblyResolver;
            PluginTypeResolver = DefaultPluginTypeResolver;
        }

        // ---------- resolution ----------

        private static bool TryResolve(out string reason)
        {
            if (_resolved)
            {
                reason = null;
                return true;
            }

            if ((DateTime.UtcNow - _lastResolveAttemptUtc).TotalSeconds < ResolveRetrySeconds)
            {
                reason = _unavailableReason;
                return false;
            }

            _lastResolveAttemptUtc = DateTime.UtcNow;
            ClearResolvedMembers();

            try
            {
                var assembly = AssemblyResolver(DefaultAssemblyName);
                if (assembly == null)
                {
                    return Unavailable("the Playnite Achievements extension isn't loaded", out reason);
                }

                var pluginType = PluginTypeResolver(assembly);
                if (pluginType == null)
                {
                    return Unavailable("the Playnite Achievements plugin type couldn't be found", out reason);
                }

                _instanceProperty = pluginType.GetProperty(
                    "Instance", BindingFlags.Public | BindingFlags.Static);
                _cacheManagerProperty = pluginType.GetProperty(
                    DefaultCacheManagerProperty, BindingFlags.Public | BindingFlags.Instance);
                if (_instanceProperty == null || _cacheManagerProperty == null)
                {
                    return Unavailable("the Playnite Achievements plugin doesn't expose its cache", out reason);
                }

                var plugin = _instanceProperty.GetValue(null);
                if (plugin == null)
                {
                    return Unavailable("Playnite Achievements hasn't been created yet", out reason);
                }

                var cache = _cacheManagerProperty.GetValue(plugin);
                if (cache == null)
                {
                    return Unavailable("Playnite Achievements' cache isn't ready yet", out reason);
                }

                var cacheType = cache.GetType();
                _loadGameData = FindMethod(cacheType, "LoadGameData", parameters =>
                    parameters.Length == 1 && parameters[0].ParameterType == typeof(string));

                _notifyInvalidated = FindMethod(cacheType, "NotifyCacheInvalidated", parameters =>
                    parameters.Length == 1 &&
                    parameters[0].ParameterType.IsAssignableFrom(typeof(List<Guid>)));
                if (_notifyInvalidated == null)
                {
                    _notifyInvalidated = FindMethod(cacheType, "NotifyCacheInvalidated",
                        parameters => parameters.Length == 1);
                }

                var importer = FindImporter(cacheType);
                if (importer != null)
                {
                    _importer = importer.FieldValue(cache);
                    _importIfNeeded = importer.ImportMethod;
                }

                if (_importIfNeeded == null)
                {
                    return Unavailable("Playnite Achievements' data importer couldn't be found", out reason);
                }

                _cache = cache;
                _resolved = true;
                _unavailableReason = null;
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                return Unavailable("Playnite Achievements could not be reached: " + ex.Message, out reason);
            }
        }

        private static bool Unavailable(string reason, out string result)
        {
            _unavailableReason = reason;
            result = reason;
            return false;
        }

        private static void ClearResolvedMembers()
        {
            _cache = null;
            _importer = null;
            _loadGameData = null;
            _importIfNeeded = null;
            _notifyInvalidated = null;
            _instanceProperty = null;
            _cacheManagerProperty = null;
        }

        private static MethodInfo FindMethod(Type type, string name, Func<ParameterInfo[], bool> match)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                if (match(method.GetParameters())) return method;
            }

            return null;
        }

        /// <summary>
        /// The importer is a private field on Playnite Achievements' cache manager. Rather than
        /// depending on the field's name, take any field whose type exposes ImportIfNeeded().
        /// </summary>
        private static ImporterHandle FindImporter(Type cacheType)
        {
            for (var type = cacheType; type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    var fieldType = field.FieldType;
                    if (fieldType == null) continue;
                    var method = fieldType.GetMethod(
                        "ImportIfNeeded", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (method != null) return new ImporterHandle(field, method);
                }
            }

            return null;
        }

        private class ImporterHandle
        {
            public ImporterHandle(FieldInfo field, MethodInfo importMethod)
            {
                Field = field;
                ImportMethod = importMethod;
            }

            public FieldInfo Field { get; }

            public MethodInfo ImportMethod { get; }

            public object FieldValue(object cache)
            {
                return Field.GetValue(cache);
            }
        }

        private static void Invoke(Action action)
        {
            try
            {
                action();
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static T Invoke<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}