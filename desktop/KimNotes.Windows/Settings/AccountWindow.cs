using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using KimNotes.utils;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Settings
{
    internal sealed class AccountWindow : FeatureWindow
    {
        private readonly CloudSync sync;
        private readonly CloudApi api = new CloudApi();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly TextBox email = new TextBox { MaxLength = 254 };
        private readonly PasswordBox password = new PasswordBox { MaxLength = 128 };
        private readonly TextBox visiblePassword = new TextBox { MaxLength = 128, Visibility = Visibility.Collapsed };
        private readonly StackPanel fields = new StackPanel();
        private readonly StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock accountName = Text("");
        private readonly TextBlock accountInfo = Text("", Muted);
        private readonly TextBlock status = Text("", Muted);
        private readonly Image avatar = new Image { Stretch = Stretch.UniformToFill };
        private readonly TitleLogo fallback = new TitleLogo { Width = 30, Height = 30 };
        private readonly Button login, synchronize, signOut, profile, register;
        private bool working, closed, reveal;
        private byte[] avatarBytes;

        internal AccountWindow(CloudSync sync) : base("账号", 301, 401)
        {
            this.sync = sync ?? throw new ArgumentNullException(nameof(sync));
            var body = new StackPanel { Margin = new Thickness(16, 13, 16, 12) };
            var identity = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var avatarPanel = new Grid
            {
                Width = 47, Height = 47, Margin = new Thickness(0, 0, 9, 0),
                Clip = new EllipseGeometry(new Point(23.5, 23.5), 23.5, 23.5)
            };
            avatarPanel.SetResourceReference(Panel.BackgroundProperty, DesktopCardTheme.Field);
            avatarPanel.Children.Add(avatar);
            avatarPanel.Children.Add(fallback);
            RenderOptions.SetBitmapScalingMode(fallback, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(avatarPanel, Dock.Left);
            identity.Children.Add(avatarPanel);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            accountName.FontSize = 15;
            accountName.FontWeight = FontWeights.SemiBold;
            accountName.TextTrimming = accountInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            names.Children.Add(accountName);
            names.Children.Add(accountInfo);
            identity.Children.Add(names);
            body.Children.Add(identity);

            profile = Button("编辑资料", () => Open("notes/?panel=profile"));
            profile.Margin = new Thickness(0, 0, 0, 8);
            profile.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(profile);
            Body.Content = Scroll(body);
            body.Children.Add(fields);
            AddCredentials();
            login = Button("登录并同步", async () => await Login(), true);
            login.Margin = new Thickness(0, 9, 0, 0);
            fields.Children.Add(login);
            body.Children.Add(actions);
            synchronize = Button("立即同步", async () => await Run(() => sync.SyncNow(true)), true);
            actions.Children.Add(synchronize);
            signOut = Button("退出账号", async () =>
            {
                if (MessageBox.Show(this, "退出后暂停同步并保留本地副本。", "退出账号", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) == MessageBoxResult.OK) await Run(sync.SignOut);
            });
            signOut.Margin = new Thickness(6, 0, 0, 0);
            actions.Children.Add(signOut);
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 9, 0, 0);
            body.Children.Add(status);
            register = Button("注册账号", () => Open("login/?mode=register"));
            register.HorizontalAlignment = HorizontalAlignment.Left;
            register.Margin = new Thickness(0, 7, 0, 0);
            body.Children.Add(register);

            sync.Changed += OnCloudChanged;
            Closed += (sender, args) =>
            {
                closed = true;
                sync.Changed -= OnCloudChanged;
                lifetime.Cancel();
                api.Dispose();
                lifetime.Dispose();
            };
            Loaded += (sender, args) => { if (fields.Visibility == Visibility.Visible) email.Focus(); };
            Refresh();
        }

        private void AddCredentials()
        {
            fields.Children.Add(Text("邮箱", Muted));
            email.Height = 29;
            email.VerticalContentAlignment = VerticalAlignment.Center;
            StyleInput(email);
            email.Margin = new Thickness(0, 3, 0, 7);
            AutomationProperties.SetName(email, "邮箱");
            fields.Children.Add(email);
            fields.Children.Add(Text("密码", Muted));
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
            var passwordContainer = new Grid();
            foreach (Control input in new Control[] { password, visiblePassword })
            {
                input.Height = 29;
                StyleInput(input);
                input.VerticalContentAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(input, "密码");
                passwordContainer.Children.Add(input);
            }
            Button show = null;
            show = Button("显示", () =>
            {
                reveal = !reveal;
                if (reveal) visiblePassword.Text = password.Password;
                else
                {
                    password.Password = visiblePassword.Text;
                    visiblePassword.Clear();
                }
                show.Content = reveal ? "隐藏" : "显示";
                visiblePassword.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
                password.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
                if (reveal) { visiblePassword.Focus(); visiblePassword.CaretIndex = visiblePassword.Text.Length; }
                else password.Focus();
            });
            show.Margin = new Thickness(5, 0, 0, 0);
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(show);
            row.Children.Add(passwordContainer);
            fields.Children.Add(row);
        }

        private void Refresh()
        {
            var session = sync.Session;
            bool signedIn = session != null && !sync.NeedsLogin;
            bool incognito = Storage.Incognito;
            profile.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            fields.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            actions.Visibility = session == null ? Visibility.Collapsed : Visibility.Visible;
            synchronize.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            register.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            login.IsEnabled = !working && !incognito;
            email.IsEnabled = password.IsEnabled = visiblePassword.IsEnabled = !working;
            synchronize.IsEnabled = !working && !incognito && !sync.Busy;
            signOut.IsEnabled = !working;
            login.IsDefault = !signedIn && !incognito;
            synchronize.IsDefault = signedIn && !incognito;
            login.Content = working ? "处理中…" : "登录并同步";
            accountName.Text = signedIn ? string.IsNullOrWhiteSpace(session.Nickname) ? "小羊用户" : session.Nickname : "登录小羊账号";
            accountInfo.Text = signedIn ? session.Email : "便签和图片，换台电脑也在。";
            var bytes = signedIn ? sync.AvatarImage : null;
            if (!ReferenceEquals(avatarBytes, bytes))
            {
                avatarBytes = bytes;
                avatar.Source = Avatar(bytes);
            }
            fallback.Visibility = avatar.Source == null ? Visibility.Visible : Visibility.Collapsed;
            if (incognito) SetStatus("无痕模式下暂停登录和同步。");
            else if (session == null)
                SetStatus(working ? "正在验证账号…" : "登录后自动同步本机便签、图片和待办。");
            else
                SetStatus(sync.Status + (sync.LastSyncUtc.HasValue
                    ? "\n上次同步 " + sync.LastSyncUtc.Value.ToLocalTime().ToString("MM-dd HH:mm") : ""), sync.NeedsLogin);
        }

        private void OnCloudChanged()
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => { if (!closed) Refresh(); }));
        }

        private void Open(string url)
        {
            var error = OpenWebsite(url);
            if (error != null) SetStatus(error, true);
        }

        private async Task Login()
        {
            if (working || Storage.Incognito) return;
            if (!api.IsConfigured) { SetStatus(DesktopEndpoints.MissingMessage, true); return; }
            var credential = reveal ? visiblePassword.Text : password.Password;
            if (string.IsNullOrWhiteSpace(email.Text) || string.IsNullOrEmpty(credential))
            {
                SetStatus("请输入邮箱和密码。", true);
                return;
            }
            await Run(async () =>
            {
                string deviceId = Guid.NewGuid().ToString();
                var response = await api.Json("/auth/desktop/login",
                    new { email = email.Text.Trim(), password = credential, deviceName = Environment.MachineName, deviceId },
                    cancel: lifetime.Token);
                if (closed) return;
                password.Clear();
                visiblePassword.Clear();
                await sync.SignIn(CloudSession.FromResponse(response, deviceId));
            });
        }

        private async Task Run(Func<Task> action)
        {
            if (working) return;
            working = true;
            Refresh();
            string errorMessage = null;
            try { await action(); }
            catch (Exception error)
            {
                if (!closed) errorMessage = error is CloudApiException ? error.Message : "操作未完成，请检查网络后重试。";
            }
            finally
            {
                working = false;
                if (!closed)
                {
                    Refresh();
                    if (errorMessage != null) SetStatus(errorMessage, true);
                }
            }
        }

        private void SetStatus(string value, bool error = false)
        {
            status.Text = value ?? "";
            status.SetResourceReference(TextBlock.ForegroundProperty, error ? DesktopCardTheme.Danger : DesktopCardTheme.Icon);
        }
    }
}
