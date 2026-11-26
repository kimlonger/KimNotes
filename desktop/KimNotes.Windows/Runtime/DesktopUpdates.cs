using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
                shown = new UpdateWindow(candidate, current, UpdateNotice.IsSkipped(candidate, skipped));
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
            internal UpdateWindow(string version, string current, bool skipped) : base("小羊便签更新")
            {
                MinHeight = 0;
                Height = double.NaN;
                SizeToContent = SizeToContent.Height;
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;

                var layout = new StackPanel { Margin = new Thickness(14, 13, 14, 14) };
                layout.Children.Add(new TextBlock
                {
                    Text = "发现新版本 " + version,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold
                });
                var installed = new TextBlock
                {
                    Text = "当前版本 " + current,
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                installed.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
                layout.Children.Add(installed);

                var actions = new UniformGrid { Columns = 2, Margin = new Thickness(0, 13, 0, 0) };
                var skip = Settings.FeatureWindow.Button(skipped ? "取消跳过" : "跳过此版本", () =>
                {
                    InitConfig.SetConfigValue("skippedUpdateVersion", skipped ? "" : version);
                    Close();
                });
                skip.Margin = new Thickness(0, 0, 4, 0);
                actions.Children.Add(skip);
                var download = Settings.FeatureWindow.Button("前往下载", () =>
                {
                    Process.Start(new ProcessStartInfo(UpdateNotice.DownloadPage) { UseShellExecute = true });
                    Close();
                }, primary: true);
                download.Margin = new Thickness(4, 0, 0, 0);
                actions.Children.Add(download);
                layout.Children.Add(actions);
                Body.Children.Add(layout);
            }
        }
    }
}
