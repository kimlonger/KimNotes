using System;
using System.Collections.Generic;
using System.IO;

namespace KimNotes.utils
{
    internal class InitConfig
    {
        private static string noteConfig = "D:\\kimNotes\\config\\config.txt";
        private static Dictionary<string, string> configMap = new Dictionary<string, string>();

        /// <summary>
        /// 初始化配置文件和加载配置项到字典
        /// </summary>
        public static void InitSettings()
        {
            if (!File.Exists(noteConfig))
            {
                // 如果配置文件不存在，则创建文件夹和文件，并调用数据初始化方法
                Directory.CreateDirectory(Path.GetDirectoryName(noteConfig));
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
                sw.WriteLine($"checkBox2=True");
                sw.WriteLine($"checkBox3=False");
                sw.WriteLine($"shortcutKey=F1");
                sw.WriteLine($"notesPath=D:\\kimNotes\\notes");
                sw.WriteLine($"imagesPath=D:\\kimNotes\\images");
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
                string[] parts = line.Split('=');
                if (parts.Length == 2)
                {
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();
                    configMap[key] = value; // 将键值对存入字典
                }
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
    }
}