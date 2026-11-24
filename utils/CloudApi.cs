using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KimNotes.utils
{
    internal sealed class CloudApi : IDisposable
    {
        // The backend advertises avatar links under this protocol path even when a gateway mounts the API elsewhere.
        internal const string Prefix = "/api/v1/apps/kimnotes";
        private const string AvatarPath = "/profile/avatar?v=";
        private static readonly byte[] AvatarPngHeader =
        {
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, // PNG signature
            0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,             // IHDR chunk
            0, 0, 1, 0, 0, 0, 1, 0                           // 256 × 256
        };
        private readonly HttpClient client;
        internal bool IsConfigured => client.BaseAddress != null;
        private JObject capabilities;
        // The same authenticator protects JSON, private image uploads and downloads.
        internal Func<CancellationToken, Task<string>> AccessToken;
        internal Func<string, CancellationToken, Task<string>> RenewAccessToken;
        internal CloudApi(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler {
                AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }) {
                BaseAddress = handler == null ? DesktopEndpoints.ApiBase : new Uri("https://kimnotes.invalid" + Prefix + "/"),
                Timeout = TimeSpan.FromSeconds(60)
            };
        }
        internal async Task<JObject> Json(string path, object body = null, string token = null, string verification = null, CancellationToken cancel = default(CancellationToken))
        {
            // Serialize once, but create/dispose a fresh HttpContent for every attempt.
            string json = body == null ? null : JsonConvert.SerializeObject(body);
            var bytes = await Send(path, json == null ? (Func<HttpContent>)null : () => new StringContent(json, Encoding.UTF8, "application/json"), token, verification, cancel);
            return JObject.Parse(Encoding.UTF8.GetString(bytes));
        }
        internal async Task<JObject> TransferJson(HttpMethod method, string path, object body, string token, CancellationToken cancel)
        {
            const string receivers = "/transfers/receivers";
            const string pickup = "/transfers/pickup";
            if (!(path == receivers || path.StartsWith(receivers + "/", StringComparison.Ordinal)
                || path == pickup || path.StartsWith(pickup + "/", StringComparison.Ordinal)) || path.Contains(".."))
                throw new ArgumentException("Invalid transfer API path.", nameof(path));
            string json = body == null ? null : JsonConvert.SerializeObject(body);
            Func<HttpContent> content = json == null ? (Func<HttpContent>)null : () => new StringContent(json, Encoding.UTF8, "application/json");
            byte[] bytes = await SendOnce(path, content, token, null, cancel, 64 * 1024, method);
            return bytes.Length == 0 ? new JObject() : JObject.Parse(Encoding.UTF8.GetString(bytes));
        }
        internal async Task<string> Upload(byte[] bytes, string token, CancellationToken cancel)
        {
            Func<HttpContent> content = () => {
                var data = new ByteArrayContent(bytes);
                data.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return data;
            };
            return JObject.Parse(Encoding.UTF8.GetString(await Send("/attachments", content, token, null, cancel))).Value<string>("id");
        }
        internal async Task<bool> SupportsInlineImages(CancellationToken cancel)
            => (await GetCapabilities(cancel)).Value<int?>("inlineImageAttachmentsVersion") >= 1;

        internal async Task<bool> SupportsBootstrap(CancellationToken cancel)
            => (await GetCapabilities(cancel)).Value<int?>("syncBootstrapVersion") >= 1;

        internal async Task<bool> SupportsTodoSync(CancellationToken cancel)
            => (await GetCapabilities(cancel)).Value<int?>("todoSyncVersion") >= 1;

        private async Task<JObject> GetCapabilities(CancellationToken cancel)
            => capabilities ?? (capabilities = await Json("/config", cancel: cancel));

        internal Task<byte[]> Download(string id, string token, CancellationToken cancel) =>
            Send("/attachments/" + Guid.Parse(id).ToString(), null, token, null, cancel);

        internal static Guid? AvatarVersion(string url)
        {
            string prefix = Prefix + AvatarPath;
            if (url == null || !url.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var value = url.Substring(prefix.Length);
            return Guid.TryParseExact(value, "D", out var version) && value == version.ToString("D") ? version : (Guid?)null;
        }

        internal async Task<byte[]> DownloadAvatar(Guid version, string token, CancellationToken cancel)
        {
            // Profile images use the verified access token without starting a separate renewal.
            var data = await SendOnce(AvatarPath + version.ToString("D"), null, token, null, cancel, 512 * 1024);
            if (!data.Take(AvatarPngHeader.Length).SequenceEqual(AvatarPngHeader))
                throw new InvalidDataException("云端头像格式无效。");
            return data;
        }

        private async Task<byte[]> Send(string path, Func<HttpContent> content, string token, string verification, CancellationToken cancel)
        {
            EnsureConfigured();
            if (!path.StartsWith("/") || path.Contains("..") || path.Contains("://")) throw new ArgumentException("Invalid API path.");
            bool authenticated = AccessToken != null && !path.StartsWith("/auth/desktop/", StringComparison.Ordinal) && path != "/auth/logout";
            if (authenticated) token = await AccessToken(cancel);
            for (int attempt = 0; ; attempt++) {
                try { return await SendOnce(path, content, token, verification, cancel); }
                catch (CloudApiException error) when (authenticated && attempt == 0 && error.Status == 401 && error.Code == "access_expired") {
                    token = await RenewAccessToken(token, cancel);
                }
            }
        }
        private async Task<byte[]> SendOnce(string path, Func<HttpContent> content, string token, string verification,
            CancellationToken cancel, int maxBytes = 12 * 1024 * 1024, HttpMethod method = null)
        {
            EnsureConfigured();
            // Append within the configured authority and base path, including for query-bearing routes.
            var uri = new Uri(client.BaseAddress.AbsoluteUri + path.Substring(1));
            using (var request = new HttpRequestMessage(method ?? (content == null ? HttpMethod.Get : HttpMethod.Post), uri))
            {
                request.Content = content?.Invoke();
                request.Headers.Add("X-Kim-Client", "desktop");
                request.Headers.Add("X-Kim-Auth-Version", "2");
                request.Headers.Add("X-Kim-Inline-Images", "1");
                if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!string.IsNullOrEmpty(verification)) request.Headers.Add("X-Kim-Turnstile", verification);
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel))
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var result = new MemoryStream())
                {
                    var buffer = new byte[16384]; int count;
                    while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancel)) > 0)
                    {
                        if (result.Length + count > maxBytes) throw new IOException("云端响应超过大小限制。");
                        result.Write(buffer, 0, count);
                    }
                    var bytes = result.ToArray();
                    if (!response.IsSuccessStatusCode)
                    {
                        string message = "云端请求未完成，请稍后重试。", code = null;
                        try { var error = JObject.Parse(Encoding.UTF8.GetString(bytes)); message = error.Value<string>("message") ?? message; code = error.Value<string>("code"); } catch (JsonException) { }
                        throw new CloudApiException((int)response.StatusCode, message, code);
                    }
                    return bytes;
                }
            }
        }
        internal void EnsureConfigured()
        {
            if (!IsConfigured) throw new InvalidOperationException(DesktopEndpoints.MissingMessage);
        }

        public void Dispose() => client.Dispose();
    }
    internal sealed class CloudApiException : Exception
    {
        internal int Status { get; }
        internal string Code { get; }
        internal CloudApiException(int status, string message, string code = null) : base(message) { Status = status; Code = code; }
    }
}
