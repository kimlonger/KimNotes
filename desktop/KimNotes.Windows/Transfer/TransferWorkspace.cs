using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Transfer
{
    internal sealed class TransferWorkspace : IDisposable
    {
        private readonly Action showAccount;
        private readonly CloudSync cloud;
        private readonly FileTransferReception reception;
        private readonly Dispatcher dispatcher;
        private TransferWindow window;
        private TransferSession session;
        private Window notice;
        private bool disposed;

        internal bool CanOpenToolbox => !disposed && reception.IsSignedIn;

        internal TransferWorkspace(CloudSync cloud, Action showAccount)
        {
            this.cloud = cloud;
            this.showAccount = showAccount;
            dispatcher = Dispatcher.CurrentDispatcher;
            reception = new FileTransferReception(cloud, PresentIncomingAsync);
            cloud.Changed += AccountChanged;
            ApplyBackgroundPreference();
            reception.Start();
        }

        internal void Show()
        {
            if (disposed) return;
            if (CanOpenToolbox)
                EnsureWindow().Present();
            else
                showAccount?.Invoke();
        }

        internal void ApplyBackgroundPreference()
        {
            if (disposed) return;
            reception.SetEnabled(!Storage.Incognito);
            reception.SetBackgroundEnabled(InitConfig.FileTransferBackgroundReceive);
            if (!reception.IsSignedIn)
            {
                AccountChanged();
                return;
            }
            if (window != null)
            {
                window.ShowInTaskbar = InitConfig.ShowTaskbarIcons;
            }
        }

        private TransferWindow EnsureWindow()
        {
            if (session?.HasEnded == true) window?.CloseForSession();
            if (window != null) return window;

            TransferWindow currentWindow = null;
            var currentSession = new TransferSession(reception, (offer, token) => currentWindow.ConfirmAsync(offer, token));
            currentWindow = new TransferWindow(currentSession);
            window = currentWindow;
            session = currentSession;
            currentSession.Finished += NotifyBackground;
            currentWindow.IsVisibleChanged += (sender, args) => UpdateListener();
            currentWindow.StateChanged += (sender, args) => UpdateListener();
            currentWindow.Closed += (sender, args) =>
            {
                currentSession.Finished -= NotifyBackground;
                currentSession.Dispose();
                if (!ReferenceEquals(window, currentWindow)) return;
                window = null;
                session = null;
                if (!disposed)
                {
                    reception.SetSuspended(false);
                    UpdateListener();
                }
            };
            return currentWindow;
        }

        private void UpdateListener()
        {
            if (!disposed) reception.SetToolActive(window?.IsListening == true);
        }

        private Task PresentIncomingAsync(IFileTransferConnection connection, IncomingTransfer offer, CancellationToken token)
        {
            if (!dispatcher.CheckAccess())
                return dispatcher.InvokeAsync(() => PresentIncomingAsync(connection, offer, token)).Task.Unwrap();

            if (disposed || token.IsCancellationRequested || !reception.IsSignedIn) return Task.CompletedTask;
            if (!EnsureWindow().CanShowIncoming)
                return FileTransferProtocol.ReceiveAsync(connection, offer, _ => null, null, token);

            return session.ReceiveIncomingAsync(connection, offer, token);
        }

        private void AccountChanged()
        {
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(AccountChanged));
                return;
            }
            if (disposed || (reception.IsSignedIn && session?.HasEnded != true)) return;
            notice?.Close();
            window?.CloseForSession();
        }

        private void NotifyBackground()
        {
            if (disposed || window == null || (window.IsVisible && window.WindowState != WindowState.Minimized) || !reception.IsSignedIn)
                return;

            if (session.Succeeded)
            {
                string folder = session.SavedFolder;
                ShowNotice(session.IsReceiving ? "文件已接收" : "文件已传输", session.FileLabel,
                    folder == null ? null : "打开文件夹", folder == null ? (Action)null : () =>
                    {
                        if (TransferWindow.OpenDirectory(folder)) return;
                        Show();
                        session?.SetStatus("保存文件夹已不可用。");
                    });
            }
            else if (session.Failed)
                ShowNotice(session.IsReceiving ? "接收失败" : "传输失败", session.Status, "查看", Show);
            else if (session.Declined)
                ShowNotice("对方没有接收", session.FileLabel, "查看", Show);
        }

        private void ShowNotice(string title, string details, string button, Action action)
        {
            notice?.Close();
            DesktopCardTheme.Ensure();
            var notification = new Window
            {
                Title = title,
                Width = 286,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            var content = new StackPanel();
            var titleText = new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
            content.Children.Add(titleText);
            if (!string.IsNullOrWhiteSpace(details))
            {
                var detailText = new TextBlock
                {
                    Text = details, TextWrapping = TextWrapping.Wrap, MaxHeight = 54,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                detailText.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
                content.Children.Add(detailText);
            }
            if (action != null)
            {
                var open = new Button { Content = button, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0), MinHeight = 26 };
                open.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Field);
                open.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
                open.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Line);
                open.Click += (sender, args) =>
                {
                    notification.Close();
                    action();
                };
                content.Children.Add(open);
            }
            var frame = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 9, 11, 9),
                BorderThickness = new Thickness(1), Child = content
            };
            frame.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Surface);
            frame.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            notification.Content = frame;
            notification.Measure(new Size(notification.Width, double.PositiveInfinity));
            var area = SystemParameters.WorkArea;
            notification.Left = area.Right - notification.Width - 14;
            notification.Top = area.Bottom - notification.DesiredSize.Height - 14;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            timer.Tick += (sender, args) => notification.Close();
            notification.Closed += (sender, args) =>
            {
                timer.Stop();
                if (ReferenceEquals(notice, notification)) notice = null;
            };
            notice = notification;
            notification.Show();
            timer.Start();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cloud.Changed -= AccountChanged;
            reception.Dispose();
            notice?.Close();
            window?.CloseForSession();
        }
    }
}
