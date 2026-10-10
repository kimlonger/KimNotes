using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private static history historyForm = null;
        private static ConfigForm configForm = null;
        private static TodoListForm todoForm = null;
        private NotifyIcon trayIcon;
        private TrayMenuForm trayMenu;
        private HotKeyHandlerForm hotkeyHandler;
        private EventWaitHandle restartEvent;
        private System.Windows.Forms.Timer reminderTimer;
        private readonly List<int> lastReminderIds = new List<int>();

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
            SetupTray(); // 托盘常驻：关掉所有窗口只是缩回托盘，进程不退出
            SetupReminders(); // 待办到点提醒（置顶小窗）
            // 创建并显示初始窗体
            AddNewForm();
        }

        // 托盘图标 + 右键菜单；左键单击 = 唤回已开的便签窗，全关了才新建
        private void SetupTray()
        {
            Icon icon;
            try
            {
                icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                icon = SystemIcons.Application;
            }

            trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "小羊便签",
                Visible = true
            };
            trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    var alive = Application.OpenForms.OfType<note>().FirstOrDefault();
                    if (alive == null)
                    {
                        AddNewForm();
                        return;
                    }
                    if (alive.WindowState != FormWindowState.Normal) alive.WindowState = FormWindowState.Normal;
                    alive.Activate();
                }
                else if (e.Button == MouseButtons.Right)
                {
                    ShowTrayMenu();
                }
            };
        }

        // 托盘右键弹出：与便签三窗同族的无边框卡片菜单，带主题色 hover
        private void ShowTrayMenu()
        {
            if (trayMenu != null && !trayMenu.IsDisposed)
            {
                trayMenu.Close();
                trayMenu = null;
            }
            trayMenu = new TrayMenuForm();
            trayMenu.AddItem("新建便签", () => AddNewForm());
            trayMenu.AddItem("便签列表", () => AddNewForm2(InitConfig.GetConfigValue("notesPath")));
            trayMenu.AddItem("待办清单", () => ShowTodoList(), true);
            trayMenu.AddItem("设置", () => AddNewForm3());
            trayMenu.AddItem("退出", () => ExitApplication());
            trayMenu.FormClosed += (s, e) => { if (trayMenu != null && trayMenu.IsDisposed) trayMenu = null; };
            trayMenu.ShowAt(Control.MousePosition);
        }

        // 定时扫描到点的未完成待办，弹一个聚合提醒窗
        private void SetupReminders()
        {
            reminderTimer = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
            reminderTimer.Tick += (s, e) => CheckReminders();
            reminderTimer.Start();
        }

        private void CheckReminders()
        {
            try
            {
                var due = TodoStore.TakeDue();
                if (due.Count == 0) return;

                lastReminderIds.Clear();
                lastReminderIds.AddRange(due.Select(d => d.Id));
                ShowReminderPopup(string.Join("\n", due.Select(d => "· " + d.Text)));
            }
            catch
            {
                // 提醒失败不影响主功能
            }
        }

        // 置顶提醒小窗：三窗同族无边框卡片（主题色标题条+小羊图标+✕），高度按内容自适应
        private void ShowReminderPopup(string body)
        {
            var th = NoteTheme.Current();
            float s = UiDpi.Factor;
            int pad = (int)(15 * s);
            int wide = (int)(330 * s);

            var f = new ReminderPopup
            {
                Text = "待办提醒",
                Icon = FormChrome.AppIcon,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f),
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual
            };
            var chromeBar = FormChrome.Apply(f, "待办提醒", false, null, th.Chrome, th.ChromeText);

            var lbl = new Label
            {
                AutoSize = false,
                ForeColor = th.Text,
                Font = new Font("Microsoft YaHei UI", 10f),
                Text = body
            };
            int textH = TextRenderer.MeasureText(lbl.Text, lbl.Font, new Size(wide, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            lbl.Bounds = new Rectangle(pad, chromeBar.Bottom + (int)(13 * s), wide,
                Math.Max(textH, lbl.Font.Height * 2));

            var view = MakePopupButton("查看", true, th.IconHover, s);
            var done = MakePopupButton("完成", false, th.IconHover, s);
            int by = lbl.Bottom + (int)(13 * s);
            view.Location = new Point(pad + wide - view.Width, by);
            done.Location = new Point(view.Left - (int)(9 * s) - done.Width, by);

            f.Controls.Add(lbl);
            f.Controls.Add(done);
            f.Controls.Add(view);
            f.ClientSize = new Size(wide + pad * 2, by + view.Height + pad);
            f.AcceptButton = view;

            done.Click += (s2, e2) =>
            {
                f.Close();
                foreach (int id in lastReminderIds)
                {
                    var item = TodoStore.Find(id);
                    if (item != null) TodoStore.SetDone(item, true);
                }
            };
            view.Click += (s2, e2) =>
            {
                f.Close();
                ShowTodoList(lastReminderIds.Count > 0 ? lastReminderIds[0] : -1);
            };

            var wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(wa.Right - f.Width - 16, wa.Bottom - f.Height - 16);
            f.Show();

            PlayReminderSound();
            ShakeWindow(f);
        }

        // 设置页「待办提醒」：remindMode=1 才响铃；老配置没这个键按 0（仅弹窗）
        private static bool ReminderSoundEnabled()
        {
            int mode;
            return int.TryParse(InitConfig.GetConfigValue("remindMode"), out mode) && mode == 1;
        }

        // 内嵌铃声：不依赖 Windows 声音方案与注册表事件（那些在不少机器上根本没配图录，等于没声）
        private const string ReminderSoundResource = "KimNotes.remind.wav";
        private static System.Media.SoundPlayer reminderPlayer;

        private static void PlayReminderSound()
        {
            if (!ReminderSoundEnabled()) return;
            try
            {
                if (reminderPlayer == null)
                {
                    using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ReminderSoundResource))
                    {
                        if (res == null) throw new FileNotFoundException(ReminderSoundResource);
                        var ms = new MemoryStream();
                        res.CopyTo(ms);
                        ms.Position = 0;
                        reminderPlayer = new System.Media.SoundPlayer(ms);
                    }
                }
                reminderPlayer.Play();
                return;
            }
            catch
            {
                // 内嵌资源读不到才退回系统声音
            }
            System.Media.SystemSounds.Exclamation.Play();
        }

        // 提醒窗按钮：尺寸按字体实测，任何缩放下都不会切字
        private static Button MakePopupButton(string text, bool primary, Color accent, float s)
        {
            var font = new Font("Microsoft YaHei UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                Font = font,
                Size = new Size(TextRenderer.MeasureText(text, font).Width + (int)(24 * s), font.Height + (int)(12 * s)),
                BackColor = primary ? accent : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(43, 47, 54)
            };
            b.FlatAppearance.BorderColor = primary ? accent : Color.FromArgb(223, 228, 234);
            return b;
        }

        // 无边框提醒卡片保留柔和投影，与其他小窗一致
        private sealed class ReminderPopup : Form
        {
            protected override CreateParams CreateParams
            {
                get { return FormChrome.WithShadow(base.CreateParams); }
            }
        }

        // 提醒小窗抖动：左右衰减摆动约 1 秒，抓眼球但不吵
        private static void ShakeWindow(Form f)
        {
            var origin = f.Location;
            int tick = 0;
            var shake = new System.Windows.Forms.Timer { Interval = 18 };
            shake.Tick += (s, e) =>
            {
                tick++;
                if (f.IsDisposed || tick > 55)
                {
                    shake.Stop();
                    shake.Dispose();
                    return;
                }
                int amp = Math.Max(2, 12 - tick / 5);
                f.Location = new Point(origin.X + (int)Math.Round(Math.Sin(tick * 1.1) * amp), origin.Y);
            };
            f.FormClosed += (s, e) => { shake.Stop(); shake.Dispose(); };
            shake.Start();
        }

        // 真正退出：Application.Exit 会触发各窗体 FormClosing 保存
        internal void ExitApplication()
        {
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
            // 先隐形再释放，否则进程结束后任务栏会留一个点不动的死图标
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            hotkeyHandler?.Dispose();
            restartEvent?.Dispose();
            reminderTimer?.Stop();
            reminderTimer?.Dispose();
            base.Dispose(disposing);
        }
        public note AddNewForm(string fileName = null)
        {
            note form = new note(fileName);
            form.Show();
            return form;
        }

        public void AddNewForm2(string path)
        {
            // 检查history窗体实例是否已存在
            if (historyForm == null || historyForm.IsDisposed)
            {
                historyForm = new history(path);
                historyForm.FormClosed += (sender, e) => historyForm = null; // 确保再次可以打开
                historyForm.Show();
            }
            else
            {
                historyForm.Focus(); // 如果已经打开，则让它获得焦点
            }
        }

        public void AddNewForm3()
        {            // 检查ConfigForm窗体实例是否已存在
            if (configForm == null || configForm.IsDisposed)
            {
                configForm = new ConfigForm();
                configForm.FormClosed += (sender, e) => configForm = null; // 确保再次可以打开
                configForm.Show();
            }
            else
            {
                configForm.Focus(); // 如果已经打开，则让它获得焦点
            }
        }

        /// <summary>待办清单：全局只有一张，任何入口都是激活+置前；可指定要高亮闪一下的待办。</summary>
        public void ShowTodoList(int highlightId = -1)
        {
            if (todoForm == null || todoForm.IsDisposed)
            {
                todoForm = new TodoListForm();
                todoForm.FormClosed += (sender, e) => todoForm = null; // 确保再次可以打开
                todoForm.Show();
            }
            todoForm.BringForward(highlightId);
        }

        // 检查并安装新版本（在线更新尚未上线，暂不启用启动自检）
        private void UpdateApplicationVersion()
        {
            return; // 开发中：后端未就绪，跳过启动自检
        }


    }
}