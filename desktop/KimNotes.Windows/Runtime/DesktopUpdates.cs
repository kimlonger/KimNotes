using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Runtime
{
    internal sealed class DesktopUpdates : IDisposable
    {
        private readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private bool checking;
        private bool disposed;
        private UpdateWindow shown;

        internal void Start()
        {
            if (UpdateNotice.VersionUrl == null) return;
            clock.Tick += (sender, args) => { clock.Interval = TimeSpan.FromHours(1); Check(false); };
            clock.Start();
        }

        internal async void Check(bool manual)
        {
            if (checking || disposed) return;
            if (UpdateNotice.VersionUrl == null)
            {
                if (manual) MessageBox.Show("本构建未配置更新地址。", "检查更新");
                return;
            }
            checking = true;
            try
            {
                string candidate = await UpdateNotice.FetchVersionAsync();
                if (disposed) return;
                string current = typeof(App).Assembly.GetName().Version.ToString(3);
                if (!UpdateNotice.IsNewer(candidate, current))
                {
                    if (manual) MessageBox.Show("当前已是最新版本。", "检查更新");
                    return;
                }
                string day = DateTime.Now.ToString("yyyy-MM-dd");
                string skipped = InitConfig.GetConfigValue("skippedUpdateVersion");
                if (!manual && !UpdateNotice.ShouldNotify(candidate, current, skipped,
                    InitConfig.GetConfigValue("lastUpdateNoticeVersion"), InitConfig.GetConfigValue("lastUpdateNoticeDate"), day)) return;
                if (shown != null) { shown.Activate(); return; }
                shown = new UpdateWindow(candidate, UpdateNotice.IsSkipped(candidate, skipped));
                shown.Closed += (sender, args) => shown = null;
                shown.Show();
                if (!manual) InitConfig.SetConfigValues(new Dictionary<string, string>
                    { { "lastUpdateNoticeVersion", candidate }, { "lastUpdateNoticeDate", day } });
            }
            catch (Exception error)
            {
                Program.LogError(error);
                if (manual && !disposed) MessageBox.Show("检查更新失败，请稍后重试。", "检查更新");
            }
            finally { checking = false; }
        }

        public void Dispose()
        {
            disposed = true;
            clock.Stop();
        }

        private sealed class UpdateWindow : CardWindow
        {
            internal UpdateWindow(string version, bool skipped) : base("小羊便签更新")
            {
                MinHeight = 130; Height = 145; Topmost = true;
                ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
                var layout = new StackPanel { Margin = new Thickness(14) };
                layout.Children.Add(new TextBlock { Text = "发现新版本 " + version, FontSize = 15, Margin = new Thickness(0, 0, 0, 8) });
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var download = ActionButton("前往下载");
                download.Click += (sender, args) =>
                {
                    Process.Start(new ProcessStartInfo(UpdateNotice.DownloadPage) { UseShellExecute = true });
                    Close();
                };
                var skip = ActionButton(skipped ? "取消跳过" : "跳过此版本");
                skip.Margin = new Thickness(8, 0, 0, 0);
                skip.Click += (sender, args) =>
                {
                    InitConfig.SetConfigValue("skippedUpdateVersion", skipped ? "" : version);
                    Close();
                };
                actions.Children.Add(download); actions.Children.Add(skip); layout.Children.Add(actions);
                Body.Children.Add(layout);
            }
        }
    }
}
