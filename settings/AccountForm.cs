using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes.settings
{
    internal sealed class AccountForm : AppWindow
    {
        private readonly CloudSync sync;
        internal const string RegisterUrl = CloudApi.Origin + "/login/?mode=register";
        private readonly CloudApi api;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private static readonly Color TextColor = Color.FromArgb(43, 47, 54);
        private static readonly Color MutedColor = Color.FromArgb(126, 136, 149);
        private static readonly Color AccentColor = Color.FromArgb(74, 127, 193);
        private readonly Label account, description, emailLabel, passwordLabel, status, registerHint;
        private readonly TextBox email, password;
        private readonly Button login, syncButton, logout;
        private readonly Panel chrome, body, loginPanel, actions, footer;
        private readonly AccountField emailField, passwordField;
        private readonly LinkLabel register;
        private readonly Font ownedFont = new Font("Microsoft YaHei UI", 9f);
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        private readonly Font smallFont = new Font("Microsoft YaHei UI", 8.5f);
        private readonly Dictionary<Control,Font> logicalFonts = new Dictionary<Control,Font>();
        private readonly Dictionary<Font,Font> deviceFonts = new Dictionary<Font,Font>();
        private int fontDpi;
        private bool working, released, layoutReady, arranging, refreshing, cloudRefreshQueued;
        private bool showLogin = true, showActions;

        internal AccountForm(CloudSync sync, CloudApi api = null)
        {
            SuspendLayout();
            this.sync = sync;
            this.api = api ?? new CloudApi();
            Text = "账号"; Icon = FormChrome.AppIcon; Font = ownedFont;
            BackColor = Color.White; StartPosition = FormStartPosition.CenterScreen;
            // Layout is rebuilt from logical dimensions at the window's current DPI.
            // Mixing that with WinForms' recursive scaling accumulates font and docking errors.
            AutoScaleMode = AutoScaleMode.None; MaximizeBox = false; MinimizeBox = false;
            chrome = FormChrome.Apply(this, Text, false, null, Color.White, TextColor);
            chrome.Dock = DockStyle.None;
            body = new Panel { BackColor = Color.White };
            account = Label("登录小羊账号", titleFont, TextColor);
            account.AutoEllipsis = true;
            description = Label("便签和图片，换台电脑也在。", smallFont, MutedColor);
            loginPanel = new Panel { TabIndex = 0 };
            emailLabel = Label("邮箱", smallFont, TextColor);
            passwordLabel = Label("密码", smallFont, TextColor);
            emailField = new AccountField(false) { TabIndex = 0 };
            passwordField = new AccountField(true) { TabIndex = 1 };
            email = emailField.Input; email.Name = "account-email"; email.MaxLength = 254;
            password = passwordField.Input; password.Name = "account-password"; password.MaxLength = 128;
            email.AccessibleName = "邮箱"; password.AccessibleName = "密码";
            login = new AccountButton(true) { Text = "登录并同步", TabIndex = 2, Name = "account-login" };
            login.Click += async (s,e) => await Login();
            loginPanel.Controls.AddRange(new Control[] {emailLabel,emailField,passwordLabel,passwordField,login});
            actions = new Panel { TabIndex = 1 };
            syncButton = new AccountButton(true) { Text = "立即同步", TabIndex = 0 };
            logout = new AccountButton(false) { Text = "退出账号", TabIndex = 1 };
            syncButton.Click += async (s,e) => await sync.SyncNow(true);
            logout.Click += async (s,e) => {
                if (working || MessageBox.Show(this,"退出后停止同步，本地副本会保留；重新登录原账号后可继续查看。","退出账号",MessageBoxButtons.OKCancel,MessageBoxIcon.Question) != DialogResult.OK) return;
                working = true; RefreshStatus();
                try { await sync.SignOut(); }
                catch (Exception error) { if (!IsDisposed) SetStatus(error.Message, true); }
                finally { working = false; if (!IsDisposed) RefreshStatus(); }
            };
            actions.Controls.AddRange(new Control[] {syncButton,logout});
            status = Label("", smallFont, MutedColor); status.Name = "account-status";
            status.TextChanged += (s,e) => ArrangeContent();
            footer = new Panel { TabIndex = 2 };
            footer.Paint += (s,e) => { using (var pen = new Pen(Color.FromArgb(236,239,243))) e.Graphics.DrawLine(pen,0,0,footer.Width,0); };
            registerHint = Label("还没有账号？", smallFont, MutedColor);
            register = new LinkLabel { Text = "注册账号", Name = "account-register", Font = smallFont,
                AutoSize = true, LinkColor = AccentColor, ActiveLinkColor = AccentColor,
                LinkBehavior = LinkBehavior.HoverUnderline, TabIndex = 0 };
            register.LinkClicked += (s,e) => {
                try { Process.Start(new ProcessStartInfo(RegisterUrl) { UseShellExecute = true }); }
                catch (Exception error) { Program.LogError(error); SetStatus("未能打开浏览器，请访问 note.kimlulu.com 注册。", true); }
            };
            footer.Controls.AddRange(new Control[] { registerHint, register });
            body.Controls.AddRange(new Control[] {account,description,loginPanel,actions,status,footer});
            Controls.Add(body); body.BringToFront();
            RememberFonts(this);
            layoutReady = true;
            sync.Changed += OnCloudChanged;
            Shown += (s,e) => { RefreshStatus(); if (loginPanel.Visible) email.Focus(); };
            ResumeLayout(true);
            RefreshStatus();
        }

        private static Label Label(string text, Font font, Color color) => new Label {
            Text = text, Font = font, ForeColor = color, AutoSize = false, UseMnemonic = false
        };

        private int Dpi(int value) => (int)Math.Round(value * DeviceDpi / 96f);

        private void RememberFonts(Control control)
        {
            logicalFonts.Add(control,control.Font);
            foreach (Control child in control.Controls) RememberFonts(child);
        }

        private void ApplyFonts()
        {
            var previous = new List<Font>();
            if (fontDpi != DeviceDpi) {
                previous.AddRange(deviceFonts.Values);
                deviceFonts.Clear();
                // WinForms' native HFONT is based on the system DPI. Scale its point size
                // from the original font, rather than from a previously scaled font.
                float scale = DeviceDpi / (96f * UiDpi.Factor);
                foreach (var pair in logicalFonts) if (!deviceFonts.ContainsKey(pair.Value))
                    deviceFonts.Add(pair.Value,new Font(pair.Value.FontFamily,pair.Value.SizeInPoints*scale,pair.Value.Style,GraphicsUnit.Point));
                fontDpi = DeviceDpi;
            }
            foreach (var pair in logicalFonts) {
                var font = deviceFonts[pair.Value];
                if (!ReferenceEquals(pair.Key.Font,font)) pair.Key.Font = font;
            }
            foreach (var font in previous) font.Dispose();
        }

        protected override CreateParams CreateParams => FormChrome.WithShadow(base.CreateParams);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            ArrangeContent();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // Run once more after WinForms has applied the suggested bounds and scaled children.
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)ArrangeContent);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ArrangeContent();
        }

        private void ArrangeContent()
        {
            if (!layoutReady || arranging || refreshing || IsDisposed) return;
            arranging = true;
            try {
                ApplyFonts();
                int width = Dpi(320), pad = Dpi(24), wide = width - pad * 2;
                int textHeight = TextRenderer.MeasureText("邮箱", emailLabel.Font).Height;
                int y = Dpi(18);
                account.Bounds = new Rectangle(pad,y,wide,TextRenderer.MeasureText("登录小羊账号",account.Font).Height + Dpi(3));
                y = account.Bottom + Dpi(6);
                description.Bounds = new Rectangle(pad,y,wide,textHeight);
                y = description.Bottom + Dpi(20);

                // Use the same view state as the visible controls. Session can become valid
                // while Login is still awaiting the first image upload or cloud download.
                bool needsLogin = showLogin;
                if (needsLogin) {
                    int h = Math.Max(Dpi(36),email.PreferredHeight + Dpi(14));
                    emailLabel.Bounds = new Rectangle(0,0,wide,textHeight);
                    emailField.Bounds = new Rectangle(0,emailLabel.Bottom + Dpi(6),wide,h);
                    passwordLabel.Bounds = new Rectangle(0,emailField.Bottom + Dpi(12),wide,textHeight);
                    passwordField.Bounds = new Rectangle(0,passwordLabel.Bottom + Dpi(6),wide,h);
                    login.Bounds = new Rectangle(0,passwordField.Bottom + Dpi(16),wide,Dpi(36));
                    loginPanel.Bounds = new Rectangle(pad,y,wide,login.Bottom);
                    y = loginPanel.Bottom + Dpi(12);
                }
                if (showActions) {
                    int gap = Dpi(10), half = (wide-gap)/2;
                    syncButton.Bounds = new Rectangle(0,0,half,Dpi(36));
                    logout.Bounds = needsLogin ? new Rectangle(0,0,wide,Dpi(32)) : new Rectangle(half+gap,0,wide-half-gap,Dpi(36));
                    actions.Bounds = new Rectangle(pad,y,wide,Dpi(36));
                    y = actions.Bottom + Dpi(12);
                }
                var statusSize = TextRenderer.MeasureText(status.Text,status.Font,new Size(wide,int.MaxValue),TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                status.Bounds = new Rectangle(pad,y,wide,Math.Max(textHeight*2,statusSize.Height));
                y = status.Bottom + Dpi(16);
                footer.Visible = needsLogin;
                if (needsLogin) {
                    footer.Bounds = new Rectangle(pad,y,wide,textHeight + Dpi(18));
                    int hintWidth = TextRenderer.MeasureText(registerHint.Text,registerHint.Font).Width;
                    int linkWidth = register.PreferredWidth;
                    int left = Math.Max(0,(wide-hintWidth-linkWidth-Dpi(2))/2);
                    registerHint.Bounds = new Rectangle(left,Dpi(15),hintWidth,textHeight);
                    register.Location = new Point(registerHint.Right+Dpi(2),Dpi(15));
                    footer.Height = Math.Max(registerHint.Bottom,register.Bottom);
                    y = footer.Bottom + Dpi(18);
                } else y += Dpi(6);
                int chromeHeight = Dpi(36);
                var size = new Size(width,chromeHeight+y);
                // WinForms can retain the pre-transition client size as a minimum after
                // WM_DPICHANGED. It must not stop the signed-in view from becoming smaller.
                if (!MinimumSize.IsEmpty) MinimumSize = Size.Empty;
                if (ClientSize != size) ClientSize = size;
                body.Bounds = new Rectangle(0,chromeHeight,width,y);
                chrome.Bounds = new Rectangle(0,0,width,chromeHeight);
                foreach (Control control in chrome.Controls) {
                    if (control is PictureBox) control.Bounds = new Rectangle(Dpi(10),Dpi(9),Dpi(18),Dpi(18));
                    else if (control is ToolIconButton) control.Bounds = new Rectangle(width-Dpi(38),Dpi(3),Dpi(32),Dpi(30));
                    else if (control is Label) control.Location = new Point(Dpi(35),(chromeHeight-control.PreferredSize.Height)/2);
                }
            } finally { arranging = false; }
        }

        private void SetStatus(string text, bool error = false)
        {
            status.ForeColor = error ? Color.FromArgb(182,74,64) : MutedColor;
            status.Text = text;
        }
        private void OnCloudChanged()
        {
            if (IsDisposed || Disposing || cloudRefreshQueued || !IsHandleCreated) return;
            cloudRefreshQueued = true;
            BeginInvoke((Action)(() => {
                cloudRefreshQueued = false;
                if (!IsDisposed) RefreshStatus();
            }));
        }
        private void RefreshStatus()
        {
            if (IsDisposed || refreshing) return;
            refreshing = true;
            body.SuspendLayout();
            try {
                bool signedIn = sync.Session != null && !sync.NeedsLogin;
                showLogin = !signedIn; showActions = sync.Session != null;
                account.Text = signedIn ? "已登录小羊账号" : "登录小羊账号";
                description.Text = signedIn ? sync.Session.Email : "便签和图片，换台电脑也在。";
                description.AutoEllipsis = true;
                loginPanel.Visible = showLogin; syncButton.Visible = signedIn; logout.Visible = showActions;
                actions.Visible = showActions; footer.Visible = showLogin;
                syncButton.Enabled = !sync.Busy && !working; logout.Enabled = !working; login.Enabled = !working && !Storage.Incognito;
                AcceptButton = signedIn ? syncButton : login;
                SetStatus(Storage.Incognito ? "无痕模式下不登录、不上传便签。" : sync.Session == null ?
                    (working ? "正在验证账号，请稍候…" : "登录后自动同步本机便签和图片。\n离线可编辑，联网后继续同步。") :
                    sync.Status + (sync.LastSyncUtc.HasValue ? "\n上次同步：" + sync.LastSyncUtc.Value.ToLocalTime().ToString("MM-dd HH:mm") : ""), sync.NeedsLogin);
            } finally { body.ResumeLayout(false); refreshing = false; ArrangeContent(); }
        }
        private async Task Login()
        {
            if (working || Storage.Incognito) return;
            if (string.IsNullOrWhiteSpace(email.Text) || string.IsNullOrEmpty(password.Text)) { SetStatus("请输入邮箱和密码。", true); (string.IsNullOrWhiteSpace(email.Text) ? email : password).Focus(); return; }
            working = true; login.Enabled = false; email.Enabled = false; passwordField.Enabled = false;
            login.Text = "正在登录…"; SetStatus("正在登录，随后自动同步…");
            try {
                string deviceId = Guid.NewGuid().ToString();
                var response = await api.Json("/auth/desktop/login",new {email=email.Text.Trim(),password=password.Text,deviceName=Environment.MachineName,deviceId},cancel:lifetime.Token);
                var session = CloudSession.FromResponse(response,deviceId);
                password.Clear();
                await sync.SignIn(session);
            } catch (Exception error) { Program.LogError(error); if (!IsDisposed) SetStatus(error is CloudApiException ? error.Message : "登录未完成，请检查网络后重试。", true); }
            finally { working = false; if (!IsDisposed) { login.Text = "登录并同步"; email.Enabled = true; passwordField.Enabled = true; login.Enabled = !Storage.Incognito; if (sync.Session != null && !sync.NeedsLogin) RefreshStatus(); } }
        }
        protected override void Dispose(bool disposing)
        {
            bool first = disposing && !released;
            if (disposing && !released) { released=true;sync.Changed-=OnCloudChanged;lifetime.Cancel();api.Dispose();lifetime.Dispose(); }
            base.Dispose(disposing);
            if (first) { foreach (var font in deviceFonts.Values) font.Dispose(); ownedFont.Dispose(); titleFont.Dispose(); smallFont.Dispose(); }
        }

        private sealed class AccountField : Panel
        {
            internal readonly TextBox Input;
            private readonly RevealButton reveal;
            private static readonly Color Fill = Color.FromArgb(247,249,251);
            internal AccountField(bool secret)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Input = new TextBox { BorderStyle = BorderStyle.None, BackColor = Fill, ForeColor = TextColor,
                    UseSystemPasswordChar = secret, TabIndex = 0 };
                Input.GotFocus += (s,e) => Invalidate();
                Input.LostFocus += (s,e) => Invalidate();
                Controls.Add(Input);
                if (secret) {
                    reveal = new RevealButton { TabIndex = 1, Name = "account-reveal", AccessibleName = "显示密码", BackColor = Fill };
                    reveal.Click += (s,e) => {
                        int start = Input.SelectionStart, length = Input.SelectionLength;
                        Input.UseSystemPasswordChar = !Input.UseSystemPasswordChar;
                        reveal.Revealed = !Input.UseSystemPasswordChar;
                        reveal.AccessibleName = reveal.Revealed ? "隐藏密码" : "显示密码";
                        Input.Focus(); Input.Select(start,length);
                    };
                    Controls.Add(reveal);
                }
                MouseDown += (s,e) => Input.Focus();
            }
            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (Input == null) return;
                int pad = (int)Math.Round(12 * DeviceDpi / 96f);
                int right = reveal == null ? pad : (int)Math.Round(38 * DeviceDpi / 96f);
                Input.Bounds = new Rectangle(pad,Math.Max(1,(Height-Input.PreferredHeight)/2),Math.Max(1,Width-pad-right),Input.PreferredHeight);
                if (reveal != null) reveal.Bounds = new Rectangle(Width-right,1,right-2,Math.Max(1,Height-2));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = ToolIconButton.RoundedPath(new Rectangle(0,0,Width-1,Height-1),Math.Max(4,(int)(7*DeviceDpi/96f))))
                using (var brush = new SolidBrush(Fill))
                using (var pen = new Pen(Input.ContainsFocus ? AccentColor : Color.FromArgb(225,230,236))) {
                    e.Graphics.FillPath(brush,path); e.Graphics.DrawPath(pen,path);
                }
            }
        }

        private sealed class AccountButton : Button
        {
            private readonly bool primary;
            private bool hover, pressed;
            internal AccountButton(bool primary)
            {
                this.primary = primary; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                MouseEnter += (s,e) => { hover=true; Invalidate(); };
                MouseLeave += (s,e) => { hover=pressed=false; Invalidate(); };
                MouseDown += (s,e) => { pressed=e.Button==MouseButtons.Left; Invalidate(); };
                MouseUp += (s,e) => { pressed=false; Invalidate(); };
            }
            protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? Color.White);
            protected override void OnPaint(PaintEventArgs e)
            {
                var fill = primary ? (!Enabled ? Color.FromArgb(158,185,219) : pressed ? Color.FromArgb(53,99,158) : hover ? Color.FromArgb(63,112,177) : AccentColor)
                    : hover ? Color.FromArgb(246,248,251) : Color.White;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = ToolIconButton.RoundedPath(new Rectangle(0,0,Width-1,Height-1),Math.Max(4,(int)(7*DeviceDpi/96f))))
                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(primary ? fill : Color.FromArgb(225,230,236))) {
                    e.Graphics.FillPath(brush,path); e.Graphics.DrawPath(pen,path);
                }
                TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,primary ? Color.White : !Enabled ? MutedColor : TextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),primary ? Color.White : AccentColor,fill);
            }
        }

        private sealed class RevealButton : Button
        {
            internal bool Revealed { get; set; }
            internal RevealButton()
            {
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var saved = e.Graphics.Save();
                float scale = 16*DeviceDpi/96f/24f;
                e.Graphics.TranslateTransform(Width/2f,Height/2f); e.Graphics.ScaleTransform(scale,scale); e.Graphics.TranslateTransform(-12,-12);
                using (var pen = new Pen(Enabled ? MutedColor : Color.LightGray,1.7f) { StartCap=LineCap.Round, EndCap=LineCap.Round })
                using (var eye = new GraphicsPath()) {
                    eye.AddBezier(2,12,7,4,17,4,22,12); eye.AddBezier(22,12,17,20,7,20,2,12);
                    e.Graphics.DrawPath(pen,eye); e.Graphics.DrawEllipse(pen,9,9,6,6);
                    if (Revealed) e.Graphics.DrawLine(pen,4,21,20,3);
                }
                e.Graphics.Restore(saved);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4));
            }
        }
    }
}
