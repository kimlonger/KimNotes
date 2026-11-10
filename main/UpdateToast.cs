using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes
{
    // A WinForms notification rather than a NotifyIcon balloon: the latter
    // cannot contain an interactive checkbox.
    internal sealed class UpdateToast : Form
    {
        private static UpdateToast current;
        private readonly string version;
        private readonly Action<bool> skipChanged;
        private readonly Action openDownload;
        private readonly Label appName;
        private readonly Label title;
        private readonly Label description;
        private readonly CheckBox skip;
        private readonly Button download;
        private readonly Button dismiss;
        private readonly Timer lifetime;
        private int idleSeconds;
        private bool restoring;
        private Action showSaveError = () => MessageBox.Show("无法保存选择，请重试。", "小羊便签", MessageBoxButtons.OK, MessageBoxIcon.Information);
        private static readonly bool NativeRoundedCorners = Environment.OSVersion.Version.Build >= 22000;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        internal static bool ShowForVersion(string version, bool initiallySkipped, Action<bool> skipChanged, Action openDownload)
        {
            if (current != null && !current.IsDisposed)
            {
                if (current.version == version) { current.Activate(); return false; }
                current.Close();
            }
            var toast = new UpdateToast(version, initiallySkipped, skipChanged, openDownload);
            current = toast;
            try { toast.Show(); return toast.Visible; }
            catch { current = null; toast.Dispose(); throw; }
        }

        internal static void CloseCurrent()
        {
            if (current != null && !current.IsDisposed) current.Close();
        }

        private UpdateToast(string version, bool initiallySkipped, Action<bool> skipChanged, Action openDownload)
        {
            this.version = version;
            this.skipChanged = skipChanged ?? throw new ArgumentNullException(nameof(skipChanged));
            this.openDownload = openDownload ?? throw new ArgumentNullException(nameof(openDownload));
            Text = "小羊便签更新提醒";
            AccessibleName = Text;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            ForeColor = Color.FromArgb(42, 54, 68);
            Font = SystemFonts.MessageBoxFont;
            TopMost = true;
            KeyPreview = true;

            appName = new Label { Text = "小羊便签", AutoEllipsis = true, ForeColor = Color.FromArgb(104, 121, 140) };
            title = new Label { Text = "发现新版本 v" + version, AutoEllipsis = true, Font = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold), AccessibleName = "发现新版本 " + version };
            description = new Label { Text = "前往官网下载新版本。", AutoEllipsis = true, ForeColor = Color.FromArgb(104, 121, 140) };
            skip = new CheckBox { Text = "跳过此版本", Checked = initiallySkipped, AutoSize = false, AccessibleName = "跳过此版本" };
            skip.CheckedChanged += OnSkipChanged;
            download = new Button { Text = "前往官网下载", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(76, 128, 194), ForeColor = Color.White, AccessibleName = "前往官网下载" };
            download.FlatAppearance.BorderSize = 0;
            download.Click += (s, e) => { openDownload(); Close(); };
            dismiss = new CloseButton { FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(104, 121, 140), AccessibleName = "关闭更新提醒", TabStop = true };
            dismiss.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            dismiss.FlatAppearance.BorderSize = 0;
            dismiss.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { appName, title, description, skip, download, dismiss });
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };
            lifetime = new Timer { Interval = 1000 };
            lifetime.Tick += (s, e) =>
            {
                if (ContainsFocus || Bounds.Contains(Cursor.Position)) idleSeconds = 0;
                else if (++idleSeconds >= 30) Close();
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // A real non-client frame lets Windows 11 DWM composite smooth corners.
                if (NativeRoundedCorners) cp.Style |= 0x00040000; // WS_THICKFRAME
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (NativeRoundedCorners)
            {
                int round = 2; // DWMWCP_ROUND
                try { DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Keep the native rounded frame, but draw the content in the whole window
            // and prevent the notification from being resized by dragging its border.
            if (NativeRoundedCorners && m.Msg == 0x0083)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (NativeRoundedCorners && m.Msg == 0x0086) // WM_NCACTIVATE
            {
                // Keep default activation handling, but do not paint a native
                // caption over the controls in our custom client area.
                m.Result = DefWindowProc(m.HWnd, m.Msg, m.WParam, new IntPtr(-1));
                return;
            }
            base.WndProc(ref m);
            if (NativeRoundedCorners && m.Msg == 0x0084 && m.Result.ToInt32() >= 10 && m.Result.ToInt32() <= 17)
                m.Result = new IntPtr(1);
        }

        protected override void OnLoad(EventArgs e)
        {
            LayoutToast();
            base.OnLoad(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            lifetime.Start();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            LayoutToast();
        }

        private void LayoutToast()
        {
            float scale = DeviceDpi / 96f;
            int P(int value) => (int)Math.Round(value * scale);
            ClientSize = new Size(P(336), P(164));
            appName.SetBounds(P(18), P(12), P(260), P(22));
            title.SetBounds(P(18), P(38), P(290), P(25));
            description.SetBounds(P(18), P(69), P(290), P(23));
            skip.SetBounds(P(18), P(117), P(140), P(28));
            download.SetBounds(P(174), P(115), P(144), P(32));
            dismiss.SetBounds(ClientSize.Width - P(34), P(9), P(28), P(28));
            // Older Windows does not support the DWM rounded-corner preference.
            if (!NativeRoundedCorners) using (var path = RoundedBorder(Width, Height, P(12)))
            {
                var old = Region;
                Region = new Region(path);
                old?.Dispose();
            }
            var area = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
            Location = new Point(Math.Max(area.Left, area.Right - Width - P(12)), Math.Max(area.Top, area.Bottom - Height - P(12)));
        }

        private void OnSkipChanged(object sender, EventArgs e)
        {
            if (restoring) return;
            try { skipChanged(skip.Checked); }
            catch
            {
                restoring = true;
                skip.Checked = !skip.Checked;
                restoring = false;
                showSaveError();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!NativeRoundedCorners)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(216, 226, 238)))
                using (var path = RoundedBorder(Width - 1, Height - 1, (int)Math.Round(12 * DeviceDpi / 96f)))
                    e.Graphics.DrawPath(pen, path);
            }
        }

        private static GraphicsPath RoundedBorder(int width, int height, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(width, height)));
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(width - d, 0, d, d, 270, 90);
            path.AddArc(width - d, height - d, d, d, 0, 90);
            path.AddArc(0, height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class CloseButton : Button
        {
            private bool hovered;

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hovered = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (hovered)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(242, 246, 250)))
                        e.Graphics.FillEllipse(brush, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
                }
                float size = Math.Min(ClientSize.Width, ClientSize.Height) * 0.28f;
                float x = ClientSize.Width / 2f, y = ClientSize.Height / 2f;
                using (var pen = new Pen(ForeColor, Math.Max(1.3f, DeviceDpi / 96f * 1.5f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    e.Graphics.DrawLine(pen, x - size, y - size, x + size, y + size);
                    e.Graphics.DrawLine(pen, x + size, y - size, x - size, y + size);
                }
                if (Focused && ShowFocusCues)
                    ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lifetime?.Dispose();
                title?.Font.Dispose();
                if (ReferenceEquals(current, this)) current = null;
            }
            base.Dispose(disposing);
        }
    }
}
