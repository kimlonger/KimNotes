using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Web;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes.utils
{
    internal class RemoteCallUtils
    {
        public static string getAccessToken()
        {
            string baseUrl = "https://aip.baidubce.com/oauth/2.0/token";
            string accessToken = "";
            // 构造请求参数
            Dictionary<string, Object> parameters = new Dictionary<string, Object>();
            parameters.Add("grant_type", "client_credentials");
            parameters.Add("client_id", "rCP48lkqW40vKeEPwwRgevqb");
            parameters.Add("client_secret", "jsU28uaxvzE0tU9gMxQ1U4nTLYWZSPIM");
            // 将参数拼接到请求地址中
            baseUrl += "?" + string.Join("&", parameters.Select(x => $"{x.Key}={x.Value}"));
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = client.GetAsync(baseUrl).Result;
                if (response.IsSuccessStatusCode)
                {
                    // 读取响应内容
                    string responseBody = response.Content.ReadAsStringAsync().Result;
                    // 解析 JSON 响应
                    var jsonResponse = JObject.Parse(responseBody);
                    // 提取 access_token
                    accessToken = jsonResponse["access_token"]?.ToString();

                }
            }
            return accessToken;

        }
        //ocr接口调用
        public static string generalBasic(string base64)
        {
            string token = getAccessToken();
            string host = "https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic?access_token=" + token;
            Encoding encoding = Encoding.Default;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(host);
            request.Method = "post";
            request.KeepAlive = true;
            String str = "image=" + HttpUtility.UrlEncode(base64);
            byte[] buffer = encoding.GetBytes(str);
            request.ContentLength = buffer.Length;
            request.GetRequestStream().Write(buffer, 0, buffer.Length);
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
            string result = reader.ReadToEnd();
            return result;
        }

        //查询版本信息
        public static List<string> getDownloadAppUrl()
        {
            try
            {
                // 获取当前版本号
                string version = Application.ProductVersion;
                // 获取GUID
                Assembly assembly = Assembly.GetExecutingAssembly();
                var guidAttribute = (GuidAttribute)assembly.GetCustomAttribute(typeof(GuidAttribute));
                string url = "http://localhost:8088/index/getPluginByAppId";
                Dictionary<string, Object> parameters = new Dictionary<string, Object>();
                parameters.Add("appId", guidAttribute.Value);
                url += "?" + string.Join("&", parameters.Select(x => $"{x.Key}={x.Value}"));
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(3); // 设置超时时间3秒
                    HttpResponseMessage response = client.GetAsync(url).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        string responseBody = response.Content.ReadAsStringAsync().Result;
                        var jsonResponse = JObject.Parse(responseBody);
                        var data = jsonResponse["data"];
                        if (data != null)
                        {
                            string remoteVersion = data["version"]?.ToString();
                            string downloadUrl = data["downloadUrl"]?.ToString();
                            if (!string.IsNullOrEmpty(remoteVersion) && remoteVersion != version)
                            {
                                return new List<string> { remoteVersion, downloadUrl };
                            }
                        }
                        // 版本相同或无数据
                        return new List<string>();
                    }
                    else
                    {
                        // 通讯失败
                        return new List<string>();
                    }
                }
            }
            catch
            {
                // 超时或其他异常
                return new List<string>();
            }
        }
    }
}
