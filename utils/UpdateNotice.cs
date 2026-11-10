using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace KimNotes.utils
{
    internal static class UpdateNotice
    {
        internal const string VersionUrl = "https://note.kimlulu.com/update.json";
        internal const string DownloadPage = "https://note.kimlulu.com/";

        internal static bool IsNewer(string candidate, string current)
        {
            if (!Version.TryParse(candidate, out var a) || !Version.TryParse(current, out var b)) return false;
            return Normalize(a).CompareTo(Normalize(b)) > 0;
        }
        internal static bool IsSkipped(string candidate, string skipped)
        {
            if (!Version.TryParse(candidate, out var candidateVersion) || !Version.TryParse(skipped, out var skippedVersion)) return false;
            return Normalize(candidateVersion).CompareTo(Normalize(skippedVersion)) <= 0;
        }

        internal static bool ShouldNotify(string candidate, string current, string skipped, string lastVersion, string lastDate, string today)
        {
            if (!IsNewer(candidate, current) || IsSkipped(candidate, skipped)) return false;
            // Treat 1.2.3 and 1.2.3.0 as the same version, even if the server changes formatting.
            bool sameVersion = Version.TryParse(candidate, out var candidateVersion) &&
                Version.TryParse(lastVersion, out var previousVersion) &&
                Normalize(candidateVersion).Equals(Normalize(previousVersion));
            return !sameVersion || !string.Equals(lastDate, today, StringComparison.Ordinal);
        }
        private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        internal static async Task<string> FetchVersionAsync()
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 16384 })
            using (var response = await client.GetAsync(VersionUrl).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return ParseVersion(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }
        internal static string ParseVersion(string body)
        {
            var json = JObject.Parse(body);
            if (json["version"]?.Type != JTokenType.String) throw new FormatException("版本信息无效。");
            string version = (string)json["version"];
            if (string.IsNullOrEmpty(version) || version.Length > 32 || !Version.TryParse(version, out _))
                throw new FormatException("版本信息无效。");
            return version;
        }
    }
}
