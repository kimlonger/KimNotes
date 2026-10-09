using System;
using System.Text;
using System.Net;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace KimNotes
{
    public class Translator
    {
        private readonly string appId = "20230223001572312";
        private readonly string secretKey = "GAw1LbA6FiF9YrMxs99z";

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