using System;
using System.Collections.Generic;
using System.IO;

namespace KimNotes.utils
{
    internal class InitConfig
    {
        private static string appDataPath = Storage.Root;
        private static string noteConfig = Path.Combine(appDataPath, "config.txt");
        private static Dictionary<string, string> configMap = new Dictionary<string, string>();

        /// <summary>
        /// 初始化配置文件和加载配置项到字典
        /// </summary>
        public static void InitSettings()
        {
            if (!File.Exists(noteConfig))
            {
                // 如果配置文件不存在，则创建文件夹和文件，并调用数据初始化方法
                Directory.CreateDirectory(appDataPath);
                InitData();
            }

            LoadConfigToMap();
        }

        /// <summary>
        /// 初始化默认配置数据
        /// </summary>
        public static void InitData()
        {
            SaveMap(DefaultValues());
        }

        internal static Dictionary<string, string> DefaultValues() => new Dictionary<string, string>
        {
            { "checkBox1", "True" }, { "checkBox2", "False" }, { "checkBox3", "False" },
            { "shortcutKey", "F1" }, { "notesPath", Path.Combine(appDataPath, "notes") },
            { "imagesPath", Path.Combine(appDataPath, "images") }, { "theme", "0" }, { "remindMode", "0" }
        };

        /// <summary>
        /// 加载配置文件内容到字典
        /// </summary>
        private static void LoadConfigToMap()
        {
            configMap.Clear(); // 清空字典，确保重新加载时没有旧数据

            string[] lines = File.ReadAllLines(noteConfig);
            foreach (string line in lines)
            {
                // 只按第一个'='拆分，路径中含'='时不会丢失配置
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;

                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                configMap[key] = value; // 将键值对存入字典
            }
        }

        /// <summary>
        /// 根据键名获取配置值
        /// </summary>
        /// <param name="key">配置项的键名</param>
        /// <returns>返回对应的值，如果键不存在则返回 null</returns>
        public static string GetConfigValue(string key)
        {
            if (configMap.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            switch (key)
            {
                case "notesPath": return Path.Combine(appDataPath, "notes");
                case "imagesPath": return Path.Combine(appDataPath, "images");
                case "shortcutKey": return "F1";
                case "checkBox1": case "checkBox2": case "checkBox3": return "False";
                case "theme": case "remindMode": return "0";
                default: return null;
            }
        }

        /// <summary>
        /// 设置配置值并保存到文件
        /// </summary>
        /// <param name="key">配置项的键名</param>
        /// <param name="value">要设置的值</param>
        public static void SetConfigValue(string key, string value)
        {
            SetConfigValues(new Dictionary<string, string> { { key, value } });
        }

        /// <summary>
        /// 批量改配置并一次写盘。config.txt 只有这一个写出口：
        /// 整表来自内存字典，任何本程序不认识的键（theme、以后新增的）都不会被冲掉。
        /// </summary>
        public static void SetConfigValues(Dictionary<string, string> values)
        {
            var updated = new Dictionary<string, string>(configMap);
            foreach (var pair in values) updated[pair.Key] = pair.Value;
            SaveMap(updated);
            configMap = updated;
        }

        internal static Dictionary<string, string> Snapshot() => new Dictionary<string, string>(configMap);

        internal static void Restore(Dictionary<string, string> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            bool unchanged = configMap.Count == snapshot.Count;
            foreach (var pair in snapshot)
                if (!configMap.TryGetValue(pair.Key, out var value) || value != pair.Value) unchanged = false;
            if (unchanged) return;
            var restored = new Dictionary<string, string>(snapshot);
            SaveMap(restored);
            configMap = restored;
        }

        private static void SaveMap(Dictionary<string, string> values)
        {
            Storage.Write(noteConfig, temp =>
            {
              using (StreamWriter sw = new StreamWriter(temp))
              {
                foreach (var pair in values)
                {
                    sw.WriteLine($"{pair.Key}={pair.Value}");
                }
              }
            });
        }
    }
}
