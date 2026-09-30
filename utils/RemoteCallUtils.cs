using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            request.Timeout = 30000; // 30秒超时，避免无限等待
            String str = "image=" + HttpUtility.UrlEncode(base64);
            byte[] buffer = encoding.GetBytes(str);
            request.ContentLength = buffer.Length;
            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(buffer, 0, buffer.Length);
            }
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// 检查新版本，提示用户并下载安装（启动时每日一次与手动检查共用）
        /// </summary>
        /// <param name="skipToday">非空时，用户拒绝更新后记录该日期，当天不再提示</param>
        public static void CheckUpdateAndInstall(string skipToday = null)
        {
            List<string> list = getDownloadAppUrl();
            if (list.Count == 0)
            {
                return;
            }

            string newVersion = list[0];
            string downloadUrl = list[1];
            DialogResult result = MessageBox.Show(
                $"检测到新版本 {newVersion}，是否自动更新？",
                "小羊便签",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.None);
            if (result == DialogResult.Yes)
            {
                try
                {
                    // 下载新版本安装包到临时目录（带超时，避免下载卡死）
                    string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KimNotesUpdate.msi");
                    using (var client = new TimeoutWebClient(TimeSpan.FromMinutes(2)))
                    {
                        client.DownloadFile(downloadUrl, tempPath);
                    }
                    // 启动安装包
                    Process.Start(tempPath);
                    Environment.Exit(0); // 退出当前进程
                }
                catch (Exception ex)
                {
                    MessageBox.Show("自动更新失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else if (!string.IsNullOrEmpty(skipToday))
            {
                // 用户选择不更新，记录今天的日期
                InitConfig.SetConfigValue("lastUpdateCheck", skipToday);
            }
        }

        private class TimeoutWebClient : WebClient
        {
            private readonly TimeSpan timeout;
            public TimeoutWebClient(TimeSpan timeout) { this.timeout = timeout; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request != null) request.Timeout = (int)timeout.TotalMilliseconds;
                return request;
            }
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
                string url = "http://kimlulu.com:8088/index/getPluginByAppId";
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
                            string remoteVersionStr = data["version"]?.ToString();
                            string downloadUrl = data["downloadUrl"]?.ToString();

                            if (!string.IsNullOrEmpty(remoteVersionStr))
                            {
                                Version remoteVersion = new Version(remoteVersionStr);
                                Version localVersion = new Version(version);

                                if (remoteVersion > localVersion)
                                {
                                    return new List<string> { remoteVersionStr, downloadUrl };
                                }
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
