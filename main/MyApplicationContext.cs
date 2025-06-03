using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using KimNotes.settings;
using KimNotes.utils;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace KimNotes
{
    internal class MyApplicationContext : ApplicationContext
    {
        private int openFormCount = 0;
        private static history historyForm = null;
        private static ConfigForm configForm = null;
        private HotKeyHandlerForm hotkeyHandler;

        // 自动更新
        private bool automaticUpdate = true;

        public MyApplicationContext()
        {
            UpdateApplicationVersion();
            // 防止程序重复启动的检查
            PreventMultipleInstances();
            //初始化配置
            InitConfig.InitSettings();
            // 读取配置并设置开机启动
            SetStartup();
            hotkeyHandler = new HotKeyHandlerForm(); // 初始化热键处理
            // 创建并显示初始窗体
            AddNewForm();
        }



        private void SetStartup()
        {
            bool enable = Convert.ToBoolean(InitConfig.GetConfigValue("checkBox1"));
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

        private void PreventMultipleInstances()
        {
            string appName = "小羊便签";
            Process currentProcess = Process.GetCurrentProcess();
            var runningProcess = Process.GetProcessesByName(currentProcess.ProcessName);

            foreach (var process in runningProcess)
            {
                if (process.Id != currentProcess.Id && process.MainModule.FileName == currentProcess.MainModule.FileName)
                {
                    // 检查是否是配置更新后的重启
                    if (Environment.GetCommandLineArgs().Contains("--config-update"))
                    {
                        // 如果是配置更新，关闭旧实例
                        process.Kill();
                        return;
                    }
                    else
                    {
                        MessageBox.Show($"{appName} 已经在运行中！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Environment.Exit(0); // 退出当前进程
                    }
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
        // 获取当前程序的版本号
        private void UpdateApplicationVersion()
        {
            if (automaticUpdate)
            {
                // 获取当前版本号
                string productVersion = Application.ProductVersion;
                
                // 获取程序集信息
                Assembly assembly = Assembly.GetExecutingAssembly();
                var guidAttribute = (GuidAttribute)assembly.GetCustomAttribute(typeof(GuidAttribute));
                Console.WriteLine($"当前版本号: {productVersion}");
                Console.WriteLine($"程序集 GUID: {guidAttribute.Value}");


            }
        }


    }
}