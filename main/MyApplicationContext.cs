using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using KimNotes.settings;
using KimNotes.utils;
using Microsoft.Win32;

namespace KimNotes
{
    internal class MyApplicationContext : ApplicationContext
    {
        private const string RestartEventName = "KimNotes_RestartSignal";
        private int openFormCount = 0;
        private static history historyForm = null;
        private static ConfigForm configForm = null;
        private HotKeyHandlerForm hotkeyHandler;
        private EventWaitHandle restartEvent;
        private NotifyIcon trayIcon;
        private System.Windows.Forms.Timer reminderTimer;
        private HashSet<string> firedReminders;
        private string lastReminderFile;

        public MyApplicationContext()
        {
            // 防止程序重复启动的检查
            PreventMultipleInstances();
            //初始化配置
            InitConfig.InitSettings();
            // 监听配置更新重启信号，收到后优雅退出（触发各窗体保存）
            ListenForRestartSignal();
            ////版本更新
            UpdateApplicationVersion();
            // 读取配置并设置开机启动
            SetStartup();
            hotkeyHandler = new HotKeyHandlerForm(); // 初始化热键处理
            SetupTray(); // 托盘常驻：关窗不退出，缩到托盘
            SetupReminders(); // 待办到点提醒（聚合托盘提示）
            // 创建并显示初始窗体
            AddNewForm();
        }

        // 定时扫描带时间的未勾选待办，到点弹一个聚合托盘提示
        private void SetupReminders()
        {
            firedReminders = TodoUtils.LoadFired();

            trayIcon.BalloonTipClicked += (s, e) =>
            {
                if (!string.IsNullOrEmpty(lastReminderFile)) AddNewForm(lastReminderFile);
            };

            reminderTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
            reminderTimer.Tick += (s, e) => CheckReminders();
            reminderTimer.Start();
        }

        private void CheckReminders()
        {
            try
            {
                var now = DateTime.Now;
                var due = TodoUtils.ScanAll(InitConfig.GetConfigValue("notesPath"))
                    .Where(t => !t.Checked && t.Due.HasValue && t.Due.Value <= now
                                && !firedReminders.Contains(TodoUtils.FiredKey(t)))
                    .ToList();
                if (due.Count == 0) return;

                foreach (var item in due) firedReminders.Add(TodoUtils.FiredKey(item));
                TodoUtils.SaveFired(firedReminders);

                lastReminderFile = due[0].FileName;
                string body = string.Join("\n", due.Select(d => "· " + d.Text));
                trayIcon.BalloonTipTitle = "小羊便签 · 待办提醒";
                trayIcon.BalloonTipText = body;
                trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                trayIcon.ShowBalloonTip(10000);
            }
            catch
            {
                // 提醒失败不影响主功能
            }
        }

        // 托盘图标 + 右键菜单；左键新建便签
        private void SetupTray()
        {
            Icon icon = null;
            try
            {
                icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            var menu = new ContextMenuStrip();
            menu.Items.Add("新建便签", null, (s, e) => AddNewForm());
            menu.Items.Add("便签列表", null, (s, e) => AddNewForm2(InitConfig.GetConfigValue("notesPath")));
            menu.Items.Add("设置", null, (s, e) => AddNewForm3());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitApplication());

            trayIcon = new NotifyIcon
            {
                Icon = icon ?? SystemIcons.Application,
                Text = "小羊便签",
                Visible = true,
                ContextMenuStrip = menu
            };
            trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) AddNewForm();
            };
        }

        // 真正退出：Application.Exit 会触发各窗体 FormClosing 保存
        internal void ExitApplication()
        {
            if (trayIcon != null) trayIcon.Visible = false;
            Application.Exit();
        }

        private void ListenForRestartSignal()
        {
            try
            {
                // 在 UI 线程上捕获上下文，供回调切回 UI 线程
                var uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                restartEvent = new EventWaitHandle(false, EventResetMode.ManualReset, RestartEventName);
                // 上一轮重启遗留的触发状态要清掉，否则本实例启动即收到退出信号
                restartEvent.Reset();
                ThreadPool.RegisterWaitForSingleObject(restartEvent, (state, timedOut) =>
                {
                    // Application.Exit 会走 FormClosing 保存流程
                    uiContext.Post(_ => Application.Exit(), null);
                }, null, -1, false);
            }
            catch
            {
                // 信号量创建失败时忽略，仅失去优雅重启能力
            }
        }

        /// <summary>
        /// 通知旧实例优雅退出并重启（由当前实例调用，随后自行 Application.Exit）
        /// </summary>
        internal static void RestartApplication()
        {
            try
            {
                using (var evt = new EventWaitHandle(false, EventResetMode.ManualReset, RestartEventName))
                {
                    evt.Set();
                }
            }
            catch
            {
            }

            Process.Start(Application.ExecutablePath, "--config-update");
        }

        private void SetStartup()
        {
            bool enable = Convert.ToBoolean(InitConfig.GetConfigValue("checkBox1"));
            string appName = "小羊便签";
            string exePath = Application.ExecutablePath;

            using (var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (key == null) return;
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
                if (process.Id == currentProcess.Id) continue;

                string processPath;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch
                {
                    // 无法访问其他进程（如权限不同）时跳过
                    continue;
                }

                if (processPath == currentProcess.MainModule.FileName)
                {
                    // 配置更新重启：新实例已发信号让旧实例优雅退出，等待其结束
                    if (Environment.GetCommandLineArgs().Contains("--config-update"))
                    {
                        try
                        {
                            process.WaitForExit(5000);
                            if (!process.HasExited) process.Kill();
                        }
                        catch { }
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
            restartEvent?.Dispose();
            reminderTimer?.Stop();
            reminderTimer?.Dispose();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }
        public note AddNewForm(string fileName = null)
        {
            note form = new note(fileName);
            form.FormClosed += OnFormClosed;
            openFormCount++;
            form.Show();
            return form;
        }

        public void AddNewForm2(string path)
        {
            // 检查history窗体实例是否已存在
            if (historyForm == null || historyForm.IsDisposed)
            {
                historyForm = new history(path);
                historyForm.FormClosed += (sender, e) =>
                {
                    openFormCount = Math.Max(0, openFormCount - 1);
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
                    openFormCount = Math.Max(0, openFormCount - 1);
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
            // 托盘常驻：所有窗口关闭后不退出，缩到托盘等待唤醒
            openFormCount = Math.Max(0, openFormCount - 1);
        }
        // 检查并安装新版本（在线更新尚未上线，暂不启用启动自检）
        private void UpdateApplicationVersion()
        {
            return; // 开发中：后端未就绪，跳过启动自检
        }


    }
}