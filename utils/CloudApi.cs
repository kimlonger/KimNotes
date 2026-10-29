using System;
using System.IO;
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
        internal const string Origin = "https://note.kimlulu.com";
        internal const string Prefix = "/api/v1/apps/kimnotes";
        private readonly HttpClient client;
        private bool? inlineImages;
        private bool? bootstrap;
        // The same authenticator protects JSON, private image uploads and downloads.
        internal Func<CancellationToken, Task<string>> AccessToken;
        internal Func<string, CancellationToken, Task<string>> RenewAccessToken;
        internal CloudApi(HttpMessageHandler handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler {
                AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }) { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(60) };
        }
        internal async Task<JObject> Json(string path, object body = null, string token = null, string verification = null, CancellationToken cancel = default(CancellationToken))
        {
            // Serialize once, but create/dispose a fresh HttpContent for every attempt.
            string json = body == null ? null : JsonConvert.SerializeObject(body);
            var bytes = await Send(path, json == null ? (Func<HttpContent>)null : () => new StringContent(json, Encoding.UTF8, "application/json"), token, verification, cancel);
            return JObject.Parse(Encoding.UTF8.GetString(bytes));
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
        {
            if (!inlineImages.HasValue) {
                var config = await Json("/config", cancel: cancel);
                inlineImages = config.Value<int?>("inlineImageAttachmentsVersion") >= 1;
                bootstrap = config.Value<int?>("syncBootstrapVersion") >= 1;
            }
            return inlineImages.Value;
        }
        internal async Task<bool> SupportsBootstrap(CancellationToken cancel)
        {
            if(!bootstrap.HasValue){var config=await Json("/config",cancel:cancel);bootstrap=config.Value<int?>("syncBootstrapVersion")>=1;inlineImages=config.Value<int?>("inlineImageAttachmentsVersion")>=1;}
            return bootstrap.Value;
        }
        internal Task<byte[]> Download(string id, string token, CancellationToken cancel) =>
            Send("/attachments/" + Guid.Parse(id).ToString(), null, token, null, cancel);
        private async Task<byte[]> Send(string path, Func<HttpContent> content, string token, string verification, CancellationToken cancel)
        {
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
        private async Task<byte[]> SendOnce(string path, Func<HttpContent> content, string token, string verification, CancellationToken cancel)
        {
            using (var request = new HttpRequestMessage(content == null ? HttpMethod.Get : HttpMethod.Post, Prefix + path))
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
                        if (result.Length + count > 12 * 1024 * 1024) throw new IOException("云端响应超过大小限制。");
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
        public void Dispose() => client.Dispose();
    }
    internal sealed class CloudApiException : Exception
    {
        internal int Status { get; }
        internal string Code { get; }
        internal CloudApiException(int status, string message, string code = null) : base(message) { Status = status; Code = code; }
    }
}
