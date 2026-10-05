using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HydraSync.Update
{
    /// <summary>Result of a GitHub release check.</summary>
    public class UpdateCheckResult
    {
        public bool UpdateAvailable;
        public Version CurrentVersion;
        public Version LatestVersion;
        public string DownloadUrl;
        public string Error;
    }

    /// <summary>
    /// Checks the project's GitHub releases for a newer version than the one installed.
    /// One API call per session (unauthenticated rate limit is 60/h - ample).
    /// </summary>
    public static class UpdateChecker
    {
        public const string RepoSlug = "cometmcisaac/hydra-playnite-sync";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HydraSync-UpdateChecker/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>Parses a release tag ("v1.5.0" or "1.5.0") into a Version; null when unrecognized.</summary>
        public static Version ParseTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return null;
            var t = tagName.Trim();
            if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t.Substring(1);
            return Version.TryParse(t, out var v) ? v : null;
        }

        /// <summary>Pulls the "Version:" line out of extension.yaml content; null when missing/invalid.</summary>
        public static Version ParseManifestVersion(string yamlContent)
        {
            if (string.IsNullOrEmpty(yamlContent)) return null;
            foreach (var raw in yamlContent.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("Version:", StringComparison.Ordinal)) continue;
                var value = line.Substring("Version:".Length).Trim();
                return Version.TryParse(value, out var v) ? v : null;
            }

            return null;
        }

        /// <summary>Version from the extension.yaml shipped next to the plugin assembly.</summary>
        public static Version GetLocalVersion()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(UpdateChecker).Assembly.Location);
                if (dir == null) return null;
                var path = Path.Combine(dir, "extension.yaml");
                return File.Exists(path) ? ParseManifestVersion(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Queries GitHub for the latest release and compares it with the installed version.</summary>
        public static async Task<UpdateCheckResult> CheckAsync()
        {
            var current = GetLocalVersion();
            var result = new UpdateCheckResult { CurrentVersion = current };
            try
            {
                var json = await Http.GetStringAsync(
                    "https://api.github.com/repos/" + RepoSlug + "/releases/latest");
                var root = JObject.Parse(json);

                var latest = ParseTag((string)root["tag_name"]);
                if (latest == null)
                {
                    result.Error = "unrecognized release tag";
                    return result;
                }

                result.LatestVersion = latest;

                string url = null;
                if (root["assets"] is JArray assets)
                {
                    var asset = assets.OfType<JObject>().FirstOrDefault(a =>
                        {
                            var name = (string)a["name"] ?? "";
                            return name.StartsWith("HydraSync-", StringComparison.OrdinalIgnoreCase) &&
                                   name.EndsWith(".pext", StringComparison.OrdinalIgnoreCase);
                        }) ?? assets.OfType<JObject>().FirstOrDefault();
                    url = (string)asset?["browser_download_url"];
                }

                result.DownloadUrl = url ??
                    $"https://github.com/{RepoSlug}/releases/download/v{latest}/HydraSync-{latest}.pext";
                result.UpdateAvailable = current != null && latest > current;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>Streams a release asset to a local file.</summary>
        public static async Task DownloadAsync(string url, string destPath)
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (var body = await response.Content.ReadAsStreamAsync())
                using (var file = File.Create(destPath))
                {
                    await body.CopyToAsync(file);
                }
            }
        }
    }
}
