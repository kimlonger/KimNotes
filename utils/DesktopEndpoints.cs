using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KimNotes.utils
{
    internal static class DesktopEndpoints
    {
        internal const string MissingMessage = "此版本未配置服务地址，仅可使用本地功能。";
        private const string ResourceName = "KimNotes.desktop-endpoints.json";
        private const int RendezvousPort = 43763;

        internal static Uri WebsiteRoot { get; }
        internal static Uri ApiBase { get; }
        internal static string RendezvousEndpoint { get; }
        internal static Uri LegacyUpdateApi { get; }

        static DesktopEndpoints()
        {
            using (var resource = typeof(DesktopEndpoints).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (resource == null) return;
                using (var reader = new StreamReader(resource))
                {
                    JObject settings;
                    try { settings = JObject.Parse(reader.ReadToEnd()); }
                    catch (JsonException) { return; }

                    var website = ReadHttpsUrl(settings, "websiteRoot");
                    var api = ReadHttpsUrl(settings, "apiBase");
                    if (website == null || website.AbsolutePath != "/" || api == null) return;

                    WebsiteRoot = website;
                    ApiBase = new Uri(api.AbsoluteUri.TrimEnd('/') + "/");
                    RendezvousEndpoint = ReadRendezvous(settings, website);
                    LegacyUpdateApi = ReadHttpsUrl(settings, "legacyUpdateApi");
                }
            }
        }

        private static Uri ReadHttpsUrl(JObject settings, string name)
        {
            string value = ReadString(settings, name);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrEmpty(url.Host) || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                return null;
            return url;
        }

        private static string ReadRendezvous(JObject settings, Uri website)
        {
            string value = ReadString(settings, "rendezvousEndpoint");
            if (string.IsNullOrEmpty(value))
                return new UriBuilder("udp", website.Host, RendezvousPort).Uri.Authority;
            if (!Uri.TryCreate("udp://" + value, UriKind.Absolute, out var endpoint) || string.IsNullOrEmpty(endpoint.Host) ||
                endpoint.Port <= 0 || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                return null;
            return endpoint.Authority;
        }

        private static string ReadString(JObject settings, string name) =>
            settings[name]?.Type == JTokenType.String ? ((string)settings[name]).Trim() : null;
    }
}
