using System;
using System.Collections.Generic;
using System.IO;

namespace KimNotes.utils
{
    internal class InitConfig
    {
        private static string appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KimNotes"
        );
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
                File.Create(noteConfig).Close();
                InitData();
            }

            LoadConfigToMap();
        }

        /// <summary>
        /// 初始化默认配置数据
        /// </summary>
        public static void InitData()
        {
            using (StreamWriter sw = new StreamWriter(noteConfig))
            {
                sw.WriteLine($"checkBox1=True");
                sw.WriteLine($"checkBox2=false");
                sw.WriteLine($"checkBox3=False");
                sw.WriteLine($"shortcutKey=F1");
                sw.WriteLine($"notesPath={Path.Combine(appDataPath, "notes")}");
                sw.WriteLine($"imagesPath={Path.Combine(appDataPath, "images")}");
                sw.WriteLine($"theme=0");
            }
        }

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
            if (configMap.TryGetValue(key, out string value))
            {
                return value;
            }

            return null; // 如果键不存在，返回 null
        }

        /// <summary>
        /// 设置配置值并保存到文件
        /// </summary>
        /// <param name="key">配置项的键名</param>
        /// <param name="value">要设置的值</param>
        public static void SetConfigValue(string key, string value)
        {
            // 更新内存中的配置
            configMap[key] = value;

            // 将更新后的配置写入文件
            using (StreamWriter sw = new StreamWriter(noteConfig))
            {
                foreach (var pair in configMap)
                {
                    sw.WriteLine($"{pair.Key}={pair.Value}");
                }
            }
        }
    }
}