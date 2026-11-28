using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KimNotes.utils;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Settings
{
    internal sealed partial class AccountWindow : FeatureWindow
    {
        private readonly CloudSync sync;
        private readonly CloudApi api = new CloudApi();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly TextBox email = new TextBox { MaxLength = 254 };
        private readonly PasswordBox password = new PasswordBox { MaxLength = 128 };
        private readonly TextBox visiblePassword = new TextBox { MaxLength = 128, Visibility = Visibility.Collapsed };
        private readonly StackPanel fields = new StackPanel();
        private readonly TextBlock accountName = Text("");
        private readonly TextBlock accountInfo = Text("", Muted);
        private readonly TextBlock status = Text("", Muted);
        private readonly TextBlock lastSync = Text("", Muted);
        private readonly TextBlock syncLabel = Text("同步状态");
        private readonly Border statusSurface = new Border();
        private readonly Image avatar = new Image { Stretch = Stretch.UniformToFill };
        private readonly TitleLogo fallback = new TitleLogo { Width = 30, Height = 30 };
        private readonly Button login, synchronize, signOut, profile, register;
        private bool working, closed, reveal, openingProfile;
        private CancellationTokenSource registrationRequest;
        private byte[] avatarBytes;
        private CancellationToken credentialScope;

        internal AccountWindow(CloudSync sync) : base("账号", 301, 401)
        {
            MinHeight = Height;
            this.sync = sync ?? throw new ArgumentNullException(nameof(sync));
            credentialScope = sync.AccountLifetimeToken;
            profile = IconAction("编辑资料", ProfilePencil, async () => await EditProfile());
            login = Button("登录", async () => await Login(), true);
            synchronize = Button("立即同步", async () => await Run(() => sync.SyncNow(true)), true);
            signOut = Button("退出账号", async () =>
            {
                var account = sync.AccountLifetimeToken;
                if (DesktopDialog.Confirm(this, "退出账号", "退出后会暂停同步，已保存的内容仍留在本机。", "退出账号", destructive: true) &&
                    !closed && !account.IsCancellationRequested) await Run(sync.SignOut);
            });
            register = Button("注册账号", async () =>
            {
                if (registrationRequest != null)
                {
                    register.IsEnabled = false;
                    registrationRequest.Cancel();
                    return;
                }
                await Register(OpenWebsite);
            });
            Body.Content = BuildCard();

            sync.Changed += OnCloudChanged;
            Closed += (sender, args) =>
            {
                closed = true;
                CloseRememberedLoginMenu();
                sync.Changed -= OnCloudChanged;
                lifetime.Cancel();
                api.Dispose();
                lifetime.Dispose();
            };
            Loaded += (sender, args) => { if (fields.Visibility == Visibility.Visible) email.Focus(); };
            Refresh();
        }

        private void Refresh()
        {
            var activeScope = sync.AccountLifetimeToken;
            if (credentialScope != activeScope)
            {
                credentialScope = activeScope;
                ResetRememberedLogin();
                email.Clear();
                password.Clear();
                visiblePassword.Clear();
                if (reveal) SetPasswordVisibility(false, focus: false);
            }
            var session = sync.Session;
            bool signedIn = session != null && !sync.NeedsLogin;
            bool incognito = Storage.Incognito;
            if (incognito) ResetRememberedLogin();
            bool awaitingRegistration = registrationRequest != null;
            profile.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            profile.IsEnabled = signedIn && !incognito && !working && !openingProfile;
            fields.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            login.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            signOut.Visibility = session == null ? Visibility.Collapsed : Visibility.Visible;
            synchronize.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            syncLabel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            register.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            SetAccountLayout(signedIn, session != null);
            login.IsEnabled = !working && !incognito && !awaitingRegistration;
            fields.IsEnabled = login.IsEnabled;
            if (signedIn || !fields.IsEnabled) CloseRememberedLoginMenu();
            register.Content = awaitingRegistration ? "取消自动登录" : "注册账号";
            register.IsEnabled = !working && !incognito && registrationRequest?.IsCancellationRequested != true;
            synchronize.IsEnabled = !working && !incognito && !sync.Busy;
            signOut.IsEnabled = !working;
            login.IsDefault = !signedIn && login.IsEnabled;
            synchronize.IsDefault = signedIn && !incognito;
            login.Content = working ? "登录中…" : "登录";
            synchronize.Content = sync.Busy ? "同步中…" : "立即同步";
            accountName.Text = signedIn ? string.IsNullOrWhiteSpace(session.Nickname) ? "小羊用户" : session.Nickname : "登录小羊便签";
            accountInfo.Text = signedIn ? session.Email : "";
            accountInfo.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            var bytes = signedIn ? sync.AvatarImage : null;
            if (!ReferenceEquals(avatarBytes, bytes))
            {
                avatarBytes = bytes;
                avatar.Source = Avatar(bytes);
            }
            fallback.Visibility = avatar.Source == null ? Visibility.Visible : Visibility.Collapsed;
            DateTime? previousSync = signedIn && !incognito ? sync.LastSyncUtc : null;
            lastSync.Text = previousSync.HasValue ? "上次同步 " + previousSync.Value.ToLocalTime().ToString("MM-dd HH:mm") : "";
            lastSync.Visibility = previousSync.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (incognito) SetStatus("无痕模式下已暂停登录和同步。");
            else SetStatus(session == null ? null : sync.Status, sync.NeedsLogin);
        }

        private void OnCloudChanged()
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => { if (!closed) Refresh(); }));
        }

        private async Task Register(Func<string, string> openWebsite)
        {
            if (closed || working || registrationRequest != null || Storage.Incognito ||
                (sync.Session != null && !sync.NeedsLogin)) return;
            var account = sync.AccountLifetimeToken;
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account))
            {
                registrationRequest = request;
                Refresh();
                string errorMessage = null;
                bool completed = false;
                try
                {
                    await DesktopRegistrationHandoff.Open(DesktopEndpoints.WebsiteRoot, path =>
                    {
                        string error = openWebsite(path);
                        if (error == null) Hide();
                        return error;
                    }, api, sync, request.Token);
                    completed = true;
                }
                catch (Exception) when (request.IsCancellationRequested) { }
                catch (Exception error)
                {
                    errorMessage = error is InvalidOperationException || error is TimeoutException
                        ? error.Message : "自动登录未完成。注册成功后，可在这里用新账号登录。";
                }
                finally
                {
                    registrationRequest = null;
                    if (!closed)
                    {
                        Refresh();
                        if (completed && sync.Session != null && !sync.NeedsLogin && !Storage.Incognito)
                        {
                            if (!IsVisible) Show();
                            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                            Activate();
                        }
                        else if (!account.IsCancellationRequested)
                        {
                            if (errorMessage != null) SetStatus(errorMessage, true);
                            if (IsActive) email.Focus();
                        }
                    }
                }
            }
        }

        private async Task EditProfile()
        {
            if (closed || !profile.IsEnabled || sync.Session == null || sync.NeedsLogin || Storage.Incognito) return;
            var account = sync.AccountLifetimeToken;
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, account))
            {
                string errorMessage = null;
                openingProfile = true;
                profile.IsEnabled = false;
                try
                {
                    await ProfileBrowserHandoff.Open(DesktopEndpoints.WebsiteRoot, OpenWebsite,
                        sync.IssueBrowserProfileCode, request.Token);
                }
                catch (Exception) when (request.IsCancellationRequested) { }
                catch (CloudApiException error)
                {
                    errorMessage = error.Status == 404 ? "官网暂未支持从便签登录。" : error.Message;
                }
                catch (Exception error)
                {
                    errorMessage = error is InvalidOperationException || error is TimeoutException
                        ? error.Message : "无法打开资料页，请稍后重试。";
                }
                finally
                {
                    openingProfile = false;
                    if (!closed)
                    {
                        Refresh();
                        if (errorMessage != null && !account.IsCancellationRequested) SetStatus(errorMessage, true);
                    }
                }
            }
        }

        private async Task Login()
        {
            if (working || registrationRequest != null || Storage.Incognito) return;
            if (!api.IsConfigured) { SetStatus(DesktopEndpoints.MissingMessage, true); return; }
            string address = email.Text.Trim();
            var credential = reveal ? visiblePassword.Text : password.Password;
            if (address.Length == 0 || string.IsNullOrEmpty(credential))
            {
                SetStatus("请输入邮箱和密码。", true);
                return;
            }
            bool remember = rememberPassword.IsChecked == true;
            await Run(async () =>
            {
                string deviceId = Guid.NewGuid().ToString();
                using (var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, sync.AccountLifetimeToken))
                {
                    var response = await api.Json("/auth/desktop/login",
                        new { email = address, password = credential, deviceName = Environment.MachineName, deviceId },
                        cancel: request.Token);
                    if (request.IsCancellationRequested) return;
                    password.Clear();
                    visiblePassword.Clear();
                    var signedInSession = CloudSession.FromResponse(response, deviceId);
                    await sync.SignIn(signedInSession, request.Token);
                    if (remember && !closed && !Storage.Incognito && !sync.NeedsLogin &&
                        sync.Session?.SessionId == signedInSession.SessionId)
                        RememberedLogins.Save(address, credential);
                }
            });
        }

        private async Task Run(Func<Task> action)
        {
            if (working) return;
            var requestScope = sync.AccountLifetimeToken;
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
                    if (errorMessage != null && requestScope == credentialScope) SetStatus(errorMessage, true);
                }
            }
        }

        private void SetStatus(string value, bool error = false)
        {
            status.Text = value ?? "";
            status.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
            statusSurface.Visibility = status.Visibility == Visibility.Visible || syncLabel.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
            status.SetResourceReference(TextBlock.ForegroundProperty, error ? DesktopCardTheme.Danger : DesktopCardTheme.Icon);
        }
    }
}
