using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace KimNotes.utils
{
    internal static class ServiceCredentials
    {
        private static readonly Lazy<Dictionary<string, string>> Embedded =
            new Lazy<Dictionary<string, string>>(() =>
            {
                using (var resource = typeof(ServiceCredentials).Assembly.GetManifestResourceStream("KimNotes.baidu-credentials.json"))
                {
                    if (resource == null) return new Dictionary<string, string>();
                    using (var reader = new StreamReader(resource))
                        return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd()) ?? new Dictionary<string, string>();
                }
            });

        public static string Get(string key)
        {
            if (Embedded.Value.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
            throw new InvalidOperationException("当前构建未包含 OCR / 翻译服务配置。");
        }
    }
}
