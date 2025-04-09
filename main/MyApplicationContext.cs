using KimNotes.settings;
using KimNotes.utils;
using Microsoft.Win32;
using System.IO;
using System.Windows.Forms;

namespace KimNotes
{
    internal class MyApplicationContext : ApplicationContext
    {
        private int openFormCount = 0;
        private static history historyForm = null;
        private static ConfigForm configForm = null;
        private HotKeyHandlerForm hotkeyHandler;
        private static string noteConfig = "D:\\kimNotes\\config\\config.txt";

        public MyApplicationContext()
        {
            //初始化配置
            InitConfig.InitSettings();
            // 读取配置并设置开机启动
            SetStartupFromConfig();
            hotkeyHandler = new HotKeyHandlerForm(); // 初始化热键处理
            // 创建并显示初始窗体
            AddNewForm();
        }

        private void SetStartupFromConfig()
        {
            bool startup = true;
            foreach (var line in File.ReadAllLines(noteConfig))
            {
                var parts = line.Split('=');
                if (parts[0] == "checkBox1")
                {
                    bool.TryParse(parts[1], out startup);
                    break;
                }
            }
            SetStartup(startup);
        }

        private void SetStartup(bool enable)
        {
            string appName = "小羊便签";
            string exePath = Application.ExecutablePath;

            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (enable)
                {
                    key.SetValue(appName, exePath);
                }
                else
                {
                    key.DeleteValue(appName, false);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            hotkeyHandler?.Dispose();
            base.Dispose(disposing);
        }
        public void AddNewForm(string fileName = null)
        {
            note form = new note(fileName);
            form.FormClosed += OnFormClosed;
            openFormCount++;
            form.Show();
        }

        public void AddNewForm2(string path)
        {
            // 检查history窗体实例是否已存在
            if (historyForm == null || historyForm.IsDisposed)
            {
                historyForm = new history(path);
                historyForm.FormClosed += (sender, e) =>
                {
                    openFormCount--;
                    if (openFormCount == 0)
                    {
                        ExitThread();
                    }
                    historyForm = null; // 确保再次可以打开
                };
                openFormCount++;
                historyForm.Show();
            }
            else
            {
                historyForm.Focus(); // 如果已经打开，则让它获得焦点
            }
        }

        public void AddNewForm3()
        {
            // 检查ConfigForm窗体实例是否已存在
            if (configForm == null || configForm.IsDisposed)
            {
                configForm = new ConfigForm();
                configForm.FormClosed += (sender, e) =>
                {
                    openFormCount--;
                    if (openFormCount == 0)
                    {
                        ExitThread();
                    }
                    configForm = null; // 确保再次可以打开
                };
                openFormCount++;
                configForm.Show();
            }
            else
            {
                configForm.Focus(); // 如果已经打开，则让它获得焦点
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            openFormCount--;
            // 检查是否还有其他打开的窗体
            if (openFormCount == 0)
            {
                ExitThread(); // 所有窗体都关闭时退出应用程序
            }
        }
    }
}