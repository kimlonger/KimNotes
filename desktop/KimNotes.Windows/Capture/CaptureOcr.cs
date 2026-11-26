using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using KimNotes.utils;
using Newtonsoft.Json.Linq;

namespace KimNotes.Windows.Capture
{
    internal static class CaptureOcr
    {
        private static readonly Uri TokenEndpoint = new Uri("https://aip.baidubce.com/oauth/2.0/token");
        private static readonly Uri RecognitionEndpoint = new Uri("https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic");
        private static readonly Uri LocatedRecognitionEndpoint = new Uri("https://aip.baidubce.com/rest/2.0/ocr/v1/general");
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };

        internal static async Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellation)
        {
            var words = await RecognizeWordsAsync(image, RecognitionEndpoint, cancellation).ConfigureAwait(false);
            return string.Join(Environment.NewLine, words.Select(item => (string)item["words"])
                .Where(word => !string.IsNullOrWhiteSpace(word)));
        }

        internal static async Task<IReadOnlyList<CaptureTextRegion>> RecognizeLocatedAsync(BitmapSource image, CancellationToken cancellation)
        {
            var words = await RecognizeWordsAsync(image, LocatedRecognitionEndpoint, cancellation).ConfigureAwait(false);
            var regions = new List<CaptureTextRegion>();
            foreach (var item in words)
            {
                string text = ((string)item["words"] ?? "").Trim();
                if (text.Length == 0) continue;
                var location = item["location"] as JObject;
                if (location == null || location["left"] == null || location["top"] == null ||
                    location["width"] == null || location["height"] == null)
                    throw new InvalidOperationException("OCR 返回的文字缺少位置信息。");
                int x = (int)location["left"], y = (int)location["top"];
                int width = (int)location["width"], height = (int)location["height"];
                long right = Math.Min((long)image.PixelWidth, (long)x + width);
                long bottom = Math.Min((long)image.PixelHeight, (long)y + height);
                int left = Math.Max(0, x), top = Math.Max(0, y);
                if (width <= 0 || height <= 0 || right <= left || bottom <= top)
                    throw new InvalidOperationException("OCR 返回的文字位置无效。");
                regions.Add(new CaptureTextRegion(text, new Int32Rect(left, top, (int)right - left, (int)bottom - top)));
            }
            return regions;
        }

        private static async Task<JArray> RecognizeWordsAsync(BitmapSource image, Uri endpoint, CancellationToken cancellation)
        {
            string key = ServiceCredentials.Get("OCR_API_KEY");
            string secret = ServiceCredentials.Get("OCR_SECRET");
            string encoded = await Task.Run(() =>
            {
                using (var stream = new MemoryStream())
                {
                    CaptureBitmap.Write(stream, image, false);
                    return Convert.ToBase64String(stream.ToArray());
                }
            }, cancellation).ConfigureAwait(false);
            var tokenResult = await Request(TokenEndpoint, new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = key,
                ["client_secret"] = secret
            }, cancellation).ConfigureAwait(false);
            string token = (string)tokenResult["access_token"];
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("百度授权失败，请检查 OCR 凭证。");

            var url = new UriBuilder(endpoint) { Query = "access_token=" + Uri.EscapeDataString(token) }.Uri;
            var response = await Request(url, new Dictionary<string, string> { ["image"] = encoded }, cancellation).ConfigureAwait(false);
            var words = response["words_result"] as JArray;
            if (words == null)
                throw new InvalidOperationException("OCR 服务返回的结果格式不正确。");
            return words;
        }

        private static async Task<JObject> Request(Uri url, Dictionary<string, string> values, CancellationToken cancellation)
        {
            using (var content = EncodeForm(values))
            using (var response = await Client.PostAsync(url, content, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var result = await ReadJson(response.Content).ConfigureAwait(false);
                if (url.AbsolutePath == LocatedRecognitionEndpoint.AbsolutePath && (string)result["error_code"] == "6")
                    throw new InvalidOperationException("当前百度 OCR 应用未开通「通用文字识别（标准含位置版）」，无法在截图原位替换文字。");
                if (result["error_code"] != null)
                    throw new InvalidOperationException("OCR 服务返回错误：" + (string)result["error_msg"]);
                return result;
            }
        }

        private static HttpContent EncodeForm(Dictionary<string, string> values)
        {
            // The .NET Framework form encoder uses URI escaping, which rejects long Base64 image values.
            string body = string.Join("&", values.Select(pair =>
                WebUtility.UrlEncode(pair.Key) + "=" + WebUtility.UrlEncode(pair.Value)));
            var content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            return content;
        }

        private static async Task<JObject> ReadJson(HttpContent content)
        {
            // Match the legacy OCR client when a proxy reports a different charset for UTF-8 JSON.
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return JObject.Parse(await reader.ReadToEndAsync().ConfigureAwait(false));
        }
    }

    internal sealed class CaptureTextRegion
    {
        internal CaptureTextRegion(string text, Int32Rect bounds) { Text = text; Bounds = bounds; }
        internal string Text { get; }
        internal Int32Rect Bounds { get; }
    }
}
