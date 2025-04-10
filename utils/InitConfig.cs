
using System.IO;
namespace KimNotes.utils
{
    internal class InitConfig
    {
        private static string noteConfig = "D:\\kimNotes\\config\\config.txt";

        public static void InitSettings()
        {
            if (!File.Exists(noteConfig))
            {
                // 如果配置文件不存在，则创建文件夹和文件，并调用数据初始化方法
                Directory.CreateDirectory(Path.GetDirectoryName(noteConfig));
                File.Create(noteConfig).Close();
                InitData();
            }
        }

        private static void InitData()
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

    }
}
