using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    internal sealed class FileTransferToast : Form
    {
        private const int IdleSeconds = 15;
        private static readonly bool NativeRoundedCorners = Environment.OSVersion.Version.Build >= 22000;
        private static FileTransferToast current;
        private readonly float scale = UiDpi.Factor;
        private readonly NoteTheme theme;
        private readonly Rectangle workArea;
        private readonly Font bodyFont = new Font("Microsoft YaHei UI", 9.5f);
        private readonly Font titleFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        private readonly Timer lifetime;
        private int idleSeconds;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        internal static void Show(Screen screen, NoteTheme theme, string headline, string detail,
            string actionLabel = null, Action action = null)
        {
            if (DesktopSession.Exiting) return;
            CloseCurrent();
            var toast = new FileTransferToast(screen, theme, headline, detail, actionLabel, action);
            current = toast;
            try { toast.Show(); }
            catch (Exception error) when (error is InvalidOperationException || error is Win32Exception)
            {
                toast.Dispose();
                Program.LogError(error);
            }
        }

        internal static void CloseCurrent()
        {
            if (current != null && !current.IsDisposed) current.Close();
        }

        private FileTransferToast(Screen screen, NoteTheme theme, string headline, string detail,
            string actionLabel, Action action)
        {
            this.theme = theme;
            Name = "file-transfer-toast";
            Text = "小羊便签";
            AccessibleName = Text + " · " + headline;
            Icon = FormChrome.AppIcon;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = theme.Chrome;
            Font = bodyFont;
            DoubleBuffered = true;

            bool hasAction = action != null && !string.IsNullOrEmpty(actionLabel);
            var body = new Panel
            {
                Dock = DockStyle.Fill, BackColor = Color.White,
                Padding = new Padding(Px(14), Px(8), Px(14), Px(8))
            };
            var caption = new Label
            {
                Name = "file-transfer-toast-headline", Text = headline, Font = titleFont,
                ForeColor = theme.Text, Dock = DockStyle.Top, Height = Px(26),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false
            };
            var description = new Label
            {
                Name = "file-transfer-toast-detail", Text = detail,
                ForeColor = theme.Icon, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, UseMnemonic = false
            };
            body.Controls.Add(description);
            if (hasAction)
            {
                var link = new LinkLabel
                {
                    Name = "file-transfer-toast-action", Text = actionLabel,
                    Dock = DockStyle.Bottom, Height = Px(23), TextAlign = ContentAlignment.MiddleRight,
                    LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = theme.IconHover,
                    ActiveLinkColor = theme.ChromeText, UseMnemonic = false
                };
                link.LinkClicked += (s, e) =>
                {
                    Close();
                    if (!DesktopSession.Exiting) action();
                };
                body.Controls.Add(link);
            }
            body.Controls.Add(caption);
            Controls.Add(body);
            FormChrome.ApplyStandardHeader(this, Text, theme);

            workArea = (screen ?? Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
            ClientSize = new Size(Math.Min(Px(270), workArea.Width), Math.Min(Px(hasAction ? 122 : 99), workArea.Height));
            PlaceInCorner();
            lifetime = new Timer { Interval = 1000 };
            lifetime.Tick += (s, e) =>
            {
                if (Bounds.Contains(Cursor.Position)) idleSeconds = 0;
                else if (++idleSeconds >= IdleSeconds) Close();
            };
        }

        private int Px(int size) => (int)Math.Round(size * scale);

        private void PlaceInCorner()
        {
            Location = new Point(Math.Max(workArea.Left, workArea.Right - Width - Px(12)),
                Math.Max(workArea.Top, workArea.Bottom - Height - Px(12)));
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                if (NativeRoundedCorners) parameters.Style |= 0x00040000; // WS_THICKFRAME lets DWM render the corners.
                return parameters;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            lifetime.Start();
        }

        protected override void OnLoad(EventArgs e)
        {
            PlaceInCorner(); // The native frame adjusts the actual bounds when the handle is created.
            base.OnLoad(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (NativeRoundedCorners || Width < 2 || Height < 2 || scale <= 0) return;
            using (var outline = ToolIconButton.RoundedPath(ClientRectangle, Px(9)))
            {
                var old = Region;
                Region = new Region(outline);
                old?.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (NativeRoundedCorners || Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var edge = new Pen(theme.Divider))
            using (var outline = ToolIconButton.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Px(9)))
                e.Graphics.DrawPath(edge, outline);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) // WM_MOUSEACTIVATE, keep the current application's focus.
            {
                message.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            if (NativeRoundedCorners && message.Msg == 0x0083) // WM_NCCALCSIZE, draw in the entire window.
            {
                message.Result = IntPtr.Zero;
                return;
            }
            if (NativeRoundedCorners && message.Msg == 0x0086) // WM_NCACTIVATE, suppress native-caption painting.
            {
                message.Result = DefWindowProc(message.HWnd, message.Msg, message.WParam, new IntPtr(-1));
                return;
            }
            base.WndProc(ref message);
            if (NativeRoundedCorners && message.Msg == 0x0084 &&
                message.Result.ToInt32() >= 10 && message.Result.ToInt32() <= 17)
                message.Result = new IntPtr(1); // Prevent resizing while preserving native rounding.
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lifetime?.Dispose();
                if (ReferenceEquals(current, this)) current = null;
            }
            base.Dispose(disposing);
            if (disposing)
            {
                bodyFont.Dispose();
                titleFont.Dispose();
            }
        }
    }
}
