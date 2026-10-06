using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Playnite.SDK;

namespace HydraSync.Integrations
{
    /// <summary>Outcome of one playtime push attempt to HowLongToBeat.</summary>
    public enum HltbPushResult
    {
        /// <summary>HowLongToBeat accepted and submitted the new playtime.</summary>
        Updated,

        /// <summary>The user is not logged in to HowLongToBeat.</summary>
        NotLoggedIn,

        /// <summary>The game carries HowLongToBeat's "ignore playtime sync" tag.</summary>
        Ignored,

        /// <summary>HowLongToBeat has no data linked for this game yet.</summary>
        NoData,

        /// <summary>The call was made but failed (or HowLongToBeat's API refused it).</summary>
        Failed,

        /// <summary>The HowLongToBeat extension is not available in this Playnite session.</summary>
        Unavailable,
    }

    /// <summary>Aggregated result of pushing several games.</summary>
    public class HltbPushReport
    {
        public int Updated;
        public int Skipped;
        public int Failed;
        public bool Unavailable;
        public string UnavailableReason;

        /// <summary>Per-game outcome lines for diagnostics.</summary>
        public List<string> Details = new List<string>();
    }

    /// <summary>
    /// Late-bound bridge to the HowLongToBeat Playnite extension.
    /// <para>
    /// We deliberately do NOT reference HowLongToBeat.dll: it is a separate extension that
    /// may be absent or a different version, and Playnite loads every extension itself. The
    /// plugin instance is located through the already-loaded assembly, and the single call we
    /// need - <c>HowLongToBeatDatabase.SetCurrentPlayTime(Game)</c> - is exactly what that
    /// extension runs itself in <c>OnGameStopped</c> when a game exits. Optional parameters are
    /// filled from their declared defaults so a future signature change still binds.
    /// </para>
    /// <para>
    /// Everything here degrades gracefully: when the extension is missing, not loaded yet, or
    /// has been restructured, the bridge records a reason, disables itself and never throws
    /// into the sync path.
    /// </para>
    /// </summary>
    public static class HowLongToBeatBridge
    {
        public const string DefaultAssemblyName = "HowLongToBeat";
        private const string PluginTypeName = "HowLongToBeat.HowLongToBeat";
        private const int ResolveRetrySeconds = 60;

        private const BindingFlags AnyStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly object Gate = new object();
        private static bool _resolved;
        private static DateTime _lastResolveAttemptUtc = DateTime.MinValue;
        private static string _unavailableReason;

        /// <summary>
        /// Assembly lookup, swappable for tests (the real one scans the app domain for the
        /// loaded HowLongToBeat extension).
        /// </summary>
        internal static Func<string, Assembly> AssemblyResolver = FindAssembly;

        /// <summary>Plugin class lookup, swappable for tests.</summary>
        internal static Func<Assembly, Type> PluginTypeResolver = assembly =>
            assembly.GetType(PluginTypeName, false) ?? FindPluginType(assembly);

        private static object _database;
        private static MethodInfo _setCurrentPlayTime;
        private static MethodInfo _isIgnored;
        private static MethodInfo _get;
        private static MethodInfo _getIsLoggedIn;

        /// <summary>True when the HowLongToBeat extension is loaded and usable.</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureResolved(DefaultAssemblyName, null);
                return _database != null;
            }
        }

        /// <summary>Why the bridge is unavailable, or null when it is available.</summary>
        public static string UnavailableReason
        {
            get
            {
                EnsureResolved(DefaultAssemblyName, null);
                return _unavailableReason;
            }
        }

        /// <summary>Login state, or null when it cannot be determined.</summary>
        public static bool? IsLoggedIn
        {
            get
            {
                EnsureResolved(DefaultAssemblyName, null);
                if (_database == null || _getIsLoggedIn == null) return null;
                return InvokeBool(_getIsLoggedIn, ApiTarget(), null, null);
            }
        }

        /// <summary>
        /// Asks HowLongToBeat to submit the game's current Playnite playtime, the same call
        /// the extension makes when a game exits. <paramref name="noPlaying"/> defaults to true
        /// so a background sync updates the playtime without flagging the game as "Playing"
        /// (which is what HowLongToBeat's own automatic sync paths do).
        /// </summary>
        public static HltbPushResult PushPlaytime(
            Guid playniteGameId,
            object game,
            out string detail,
            bool noPlaying = true,
            ILogger log = null)
        {
            detail = null;

            if (game == null)
            {
                detail = "no game supplied";
                return HltbPushResult.Failed;
            }

            EnsureResolved(DefaultAssemblyName, log);
            if (_database == null)
            {
                detail = _unavailableReason ?? "HowLongToBeat is not available";
                return HltbPushResult.Unavailable;
            }

            try
            {
                if (_getIsLoggedIn != null)
                {
                    var loggedIn = InvokeBool(_getIsLoggedIn, ApiTarget(), null, log);
                    if (loggedIn == false)
                    {
                        detail = "not logged in to HowLongToBeat";
                        return HltbPushResult.NotLoggedIn;
                    }
                }

                if (_isIgnored != null && InvokeBool(_isIgnored, _database, new object[] { game }, log) == true)
                {
                    detail = "the game is on HowLongToBeat's ignore-playtime-sync list";
                    return HltbPushResult.Ignored;
                }

                if (_get != null && Invoke(_get, _database, new object[] { playniteGameId, true }, log) == null)
                {
                    detail = "HowLongToBeat has no data linked for this game yet";
                    return HltbPushResult.NoData;
                }

                var args = BuildPushArguments(game, noPlaying);
                if (args == null)
                {
                    detail = "HowLongToBeat's playtime method no longer matches this plugin";
                    Disable("SetCurrentPlayTime signature changed - automatic push disabled");
                    return HltbPushResult.Unavailable;
                }

                var updated = InvokeBool(_setCurrentPlayTime, _database, args, log) == true;
                detail = updated ? "submitted" : "HowLongToBeat did not accept the update";
                return updated ? HltbPushResult.Updated : HltbPushResult.Failed;
            }
            catch (Exception ex)
            {
                log?.Error(ex, "HydraSync: HowLongToBeat playtime push failed");
                detail = ex.Message;
                return HltbPushResult.Failed;
            }
        }

        /// <summary>True when HowLongToBeat is set to skip this game's playtime.</summary>
        public static bool IsIgnored(object game)
        {
            EnsureResolved(DefaultAssemblyName, null);
            if (_database == null || _isIgnored == null || game == null) return false;
            return InvokeBool(_isIgnored, _database, new object[] { game }, null) == true;
        }

        /// <summary>True when HowLongToBeat has data linked for this game.</summary>
        public static bool HasData(Guid playniteGameId)
        {
            EnsureResolved(DefaultAssemblyName, null);
            if (_database == null || _get == null) return false;
            return Invoke(_get, _database, new object[] { playniteGameId, true }, null) != null;
        }

        /// <summary>
        /// Pushes several games sequentially (HowLongToBeat's API is rate sensitive and each
        /// push costs a couple of HTTP round-trips, so parallel calls are deliberately avoided).
        /// </summary>
        public static HltbPushReport PushPlaytime(
            IEnumerable<(Guid Id, object Game)> games,
            int cap,
            ILogger log = null)
        {
            var report = new HltbPushReport();
            EnsureResolved(DefaultAssemblyName, log);

            if (_database == null)
            {
                report.Unavailable = true;
                report.UnavailableReason = _unavailableReason ?? "HowLongToBeat is not available";
                return report;
            }

            var pushed = 0;
            foreach (var entry in games ?? Enumerable.Empty<(Guid, object)>())
            {
                if (entry.Game == null) continue;
                if (cap > 0 && pushed >= cap)
                {
                    report.Skipped++;
                    report.Details.Add("cap reached (" + cap + " per sync)");
                    continue;
                }

                pushed++;
                var result = PushPlaytime(entry.Id, entry.Game, out var detail, true, log);
                switch (result)
                {
                    case HltbPushResult.Updated:
                        report.Updated++;
                        break;
                    case HltbPushResult.Failed:
                        report.Failed++;
                        break;
                    default:
                        report.Skipped++;
                        break;
                }

                report.Details.Add(Name(entry.Game) + ": " + result + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
            }

            return report;
        }

        // ---------- resolution ----------

        private static void EnsureResolved(string assemblyName, ILogger log)
        {
            lock (Gate)
            {
                if (_resolved) return;

                // Negative results are not latched forever: the extension may simply not be
                // loaded yet. Re-check at most once a minute so a disabled/absent extension
                // costs nothing.
                var now = DateTime.UtcNow;
                if (_lastResolveAttemptUtc != DateTime.MinValue &&
                    (now - _lastResolveAttemptUtc).TotalSeconds < ResolveRetrySeconds)
                {
                    return;
                }

                _lastResolveAttemptUtc = now;

                try
                {
                    var assembly = AssemblyResolver(assemblyName);
                    if (assembly == null)
                    {
                        _unavailableReason = "the HowLongToBeat extension is not loaded in this Playnite session";
                        return;
                    }

                    var pluginType = PluginTypeResolver(assembly);
                    if (pluginType == null)
                    {
                        _unavailableReason = "HowLongToBeat was loaded but its plugin type could not be found";
                        return;
                    }

                    var database = pluginType.GetProperty("PluginDatabase", AnyStatic)?.GetValue(null);
                    if (database == null)
                    {
                        _unavailableReason = "HowLongToBeat has not finished loading yet";
                        return;
                    }

                    var dbType = database.GetType();
                    var push = FindPushMethod(dbType);
                    if (push == null)
                    {
                        _unavailableReason = "HowLongToBeat's playtime method could not be found (extension updated?)";
                        return;
                    }

                    _database = database;
                    _setCurrentPlayTime = push;
                    _isIgnored = FindMethod(dbType, "IsGameIgnoredForPlaytimeSync", m => m.GetParameters().Length == 1);
                    _get = FindMethod(dbType, "Get", m =>
                    {
                        var p = m.GetParameters();
                        return p.Length >= 1 && p[0].ParameterType == typeof(Guid);
                    });

                    var api = dbType.GetProperty("HowLongToBeatApi", AnyInstance | AnyStatic)?.GetValue(database);
                    if (api != null)
                    {
                        _getIsLoggedIn = FindMethod(api.GetType(), "GetIsUserLoggedIn", m => m.GetParameters().Length == 0);
                    }

                    _unavailableReason = null;
                    _resolved = true;
                    log?.Debug("HydraSync: HowLongToBeat bridge resolved (" + push.GetParameters().Length + " params)");
                }
                catch (Exception ex)
                {
                    Disable("HowLongToBeat could not be reached: " + ex.Message);
                    log?.Debug("HydraSync: HowLongToBeat bridge unavailable - " + ex.Message);
                }
            }
        }

        private static void Disable(string reason)
        {
            _database = null;
            _setCurrentPlayTime = null;
            _isIgnored = null;
            _get = null;
            _getIsLoggedIn = null;
            _resolved = false;
            _unavailableReason = reason;
        }

        /// <summary>Test seam: drop cached resolution so the next call resolves again.</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _resolved = false;
                _lastResolveAttemptUtc = DateTime.MinValue;
                _unavailableReason = null;
                _database = null;
                _setCurrentPlayTime = null;
                _isIgnored = null;
                _get = null;
                _getIsLoggedIn = null;
            }
        }

        private static Assembly FindAssembly(string simpleName)
        {
            // Playnite has already loaded the extension, so its assembly (and statics) are here.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
                catch
                {
                    // Dynamic/anonymous assemblies can throw here - ignore them.
                }
            }

            return null;
        }

        private static Type FindPluginType(Assembly assembly)
        {
            // Fallback when the extension renames its plugin class: the class exposing a static
            // PluginDatabase is the one the bridge can talk to.
            foreach (var type in assembly.GetTypes())
            {
                if (type.GetProperty("PluginDatabase", AnyStatic) != null) return type;
            }

            return null;
        }

        private static MethodInfo FindPushMethod(Type dbType)
        {
            return FindMethod(dbType, "SetCurrentPlayTime", m =>
            {
                var p = m.GetParameters();
                return p.Length >= 1 && p[0].ParameterType != typeof(Guid);
            }, prefer: true);
        }

        private static MethodInfo FindMethod(Type type, string name, Func<MethodInfo, bool> predicate = null, bool prefer = false)
        {
            MethodInfo best = null;
            foreach (var method in type.GetMethods(AnyInstance | BindingFlags.Static))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                if (predicate != null && !predicate(method)) continue;

                // HLTB exposes a single SetCurrentPlayTime with optional parameters; prefer the
                // richest overload so the defaults we fill in are the extension's own.
                if (best == null ||
                    (prefer && method.GetParameters().Length > best.GetParameters().Length))
                {
                    best = method;
                }
            }

            return best;
        }

        private static object ApiTarget()
        {
            return _database?.GetType()
                .GetProperty("HowLongToBeatApi", AnyInstance | AnyStatic)
                ?.GetValue(_database);
        }

        /// <summary>
        /// Builds the argument list for SetCurrentPlayTime: the game, the noPlaying flag and
        /// every other parameter at its declared default.
        /// </summary>
        private static object[] BuildPushArguments(object game, bool noPlaying)
        {
            var parameters = _setCurrentPlayTime.GetParameters();
            var args = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (i == 0)
                {
                    if (!p.ParameterType.IsInstanceOfType(game)) return null;
                    args[i] = game;
                    continue;
                }

                if (string.Equals(p.Name, "noPlaying", StringComparison.OrdinalIgnoreCase) &&
                    p.ParameterType == typeof(bool))
                {
                    args[i] = noPlaying;
                    continue;
                }

                args[i] = p.HasDefaultValue ? p.DefaultValue : DefaultOf(p.ParameterType);
            }

            return args;
        }

        private static object DefaultOf(Type type)
        {
            if (type == typeof(string)) return string.Empty;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        // ---------- invocation ----------

        private static object Invoke(MethodInfo method, object target, object[] args, ILogger log)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex)
            {
                log?.Error(ex.InnerException ?? ex, "HydraSync: HowLongToBeat call failed");
                throw ex.InnerException ?? ex;
            }
        }

        private static bool? InvokeBool(MethodInfo method, object target, object[] args, ILogger log)
        {
            try
            {
                var result = Invoke(method, target, args, log);
                return result is bool b ? b : (bool?)null;
            }
            catch (Exception ex)
            {
                log?.Debug("HydraSync: HowLongToBeat check failed: " + ex.Message);
                return null;
            }
        }

        private static string Name(object game)
        {
            try
            {
                var name = game.GetType().GetProperty("Name")?.GetValue(game) as string;
                return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
            }
            catch
            {
                return "(unnamed)";
            }
        }

        /// <summary>Formats a report for a notification or diagnostics window.</summary>
        public static string Describe(HltbPushReport report)
        {
            if (report == null) return string.Empty;
            if (report.Unavailable) return report.UnavailableReason ?? "HowLongToBeat is not available";

            var parts = new List<string> { "updated " + report.Updated };
            if (report.Skipped > 0) parts.Add("skipped " + report.Skipped);
            if (report.Failed > 0) parts.Add("failed " + report.Failed);
            return string.Join(", ", parts);
        }

        internal static string FormatDetail(HltbPushResult result)
        {
            return result.ToString().ToLower(CultureInfo.InvariantCulture);
        }
    }
}