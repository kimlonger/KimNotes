using System;
using System.Text;
using System.Net;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    public class Translator
    {
        private static readonly HttpClient BatchClient = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        private string appId => KimNotes.utils.ServiceCredentials.Get("TRANSLATE_APP_ID");
        private string secretKey => KimNotes.utils.ServiceCredentials.Get("TRANSLATE_SECRET");

        // Each OCR line must remain associated with its own rectangle. Never guess
        // the association if the service merges, splits or reorders the result.
        internal async Task<IReadOnlyList<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string toLanguage, CancellationToken cancellation)
        {
            if (lines == null || lines.Count == 0) return new string[0];
            string query = string.Join("\n", lines);
            string salt = Guid.NewGuid().ToString("N");
            string id = appId;
            string sign = GenerateMD5(id + query + salt + secretKey);
            using (var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = query, ["from"] = "auto", ["to"] = toLanguage,
                ["appid"] = id, ["salt"] = salt, ["sign"] = sign
            }))
            using (var response = await BatchClient.PostAsync("https://fanyi-api.baidu.com/api/trans/vip/translate", content, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return ReadTranslationLines(json, lines);
            }
        }

        internal static IReadOnlyList<string> ReadTranslationLines(JObject json, IReadOnlyList<string> lines)
        {
            if (json["error_code"] != null)
                throw new InvalidOperationException("翻译服务返回错误：" + (string)json["error_code"] + " " + (string)json["error_msg"]);
            var result = json["trans_result"] as JArray;
            if (result == null || result.Count != lines.Count)
                throw new InvalidOperationException("翻译结果与识别文字数量不一致，无法准确替换。");
            var translated = new string[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                if (!string.Equals((string)result[i]["src"], lines[i], StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace((string)result[i]["dst"]))
                    throw new InvalidOperationException("翻译结果与识别文字不匹配，无法准确替换。");
                translated[i] = ((string)result[i]["dst"]).Trim();
            }
            return translated;
        }

        public string Translate(string text, string fromLanguage, string toLanguage)
        {
            HttpWebResponse response = null;
            Stream myResponseStream = null;
            StreamReader myStreamReader = null;

            try
            {
                string salt = new Random().Next(100000).ToString();
                string sign = GenerateMD5(appId + text + salt + secretKey);
                string url = BuildUrl(text, fromLanguage, toLanguage, salt, sign);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.ContentType = "text/html;charset=UTF-8";
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;

                response = (HttpWebResponse)request.GetResponse();
                myResponseStream = response.GetResponseStream();
                myStreamReader = new StreamReader(myResponseStream, Encoding.UTF8);

                string jsonResult = myStreamReader.ReadToEnd();

                // 解析 JSON 并提取翻译结果
                var transResultArray = JObject.Parse(jsonResult)["trans_result"];
                if (transResultArray == null)
                    return null;

                // 使用 StringBuilder 来构建所有翻译结果的字符串
                StringBuilder translationsBuilder = new StringBuilder();

                foreach (JToken translation in transResultArray)
                {
                    translationsBuilder.AppendLine(translation["dst"]?.ToString());
                }

                return translationsBuilder.ToString().Trim(); // 返回所有翻译结果
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during translation: {ex.Message}");
                return null;
            }
            finally
            {
                if (myStreamReader != null)
                    myStreamReader.Close();
                if (myResponseStream != null)
                    myResponseStream.Close();
                if (response != null)
                    response.Close();
            }
        }

        private string BuildUrl(string text, string fromLanguage, string toLanguage, string salt, string sign)
        {
            return $"https://fanyi-api.baidu.com/api/trans/vip/translate?q={WebUtility.UrlEncode(text)}&from={fromLanguage}&to={toLanguage}&appid={appId}&salt={salt}&sign={sign}";
        }

        private static string GenerateMD5(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));

                StringBuilder sb = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
