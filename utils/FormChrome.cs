using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    /// <summary>
    /// 统一窗体 chrome：无边框卡片 + 白色标题条（图标+标题+[齿轮]+关闭）+ 投影圆角 + 可选边缘拉伸。
    /// 供 历史页 / 设置页 复用，与便签主窗观感一致。
    /// </summary>
    public static class FormChrome
    {
        private const int HeaderHeight = 36;
        internal const int NoteHeaderHeight = 28;
        internal const int NoteHeaderButtonSize = 26;
        internal const int NoteHeaderLogoSize = 18;
        private const int WindowCornerPreference = 33;
        private const int RoundedCornerPreference = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private static bool TryEnableSmoothCorners(Form form)
        {
            // Remote sessions may acknowledge the preference without applying it.
            if (Environment.OSVersion.Version.Build < 22000 || SystemInformation.TerminalServerSession) return false;
            int preference = RoundedCornerPreference;
            return DwmSetWindowAttribute(form.Handle, WindowCornerPreference, ref preference, sizeof(int)) == 0;
        }

        internal static void ApplyWindowCorners(Form form, int logicalRadius = 8, float? fixedScale = null)
        {
            Size previousSize = Size.Empty;
            int previousRadius = 0;
            bool smoothCorners = false;

            void UpdateRegion()
            {
                if (smoothCorners || !form.IsHandleCreated || form.IsDisposed || form.Disposing) return;
                Size size = form.Size;
                int limit = Math.Min(size.Width, size.Height) / 2;
                if (limit == 0) return;

                float scale = fixedScale ?? form.DeviceDpi / 96f;
                int radius = Math.Min(limit, Math.Max(1, (int)Math.Round(logicalRadius * scale)));
                if (previousSize == size && previousRadius == radius) return;

                // Applying a native window region can send size notifications synchronously.
                previousSize = size;
                previousRadius = radius;
                using (var outline = ToolIconButton.RoundedPath(new Rectangle(Point.Empty, size), radius))
                {
                    var previous = form.Region;
                    form.Region = new Region(outline);
                    previous?.Dispose();
                }
            }

            void ApplyForHandle()
            {
                previousSize = Size.Empty;
                smoothCorners = TryEnableSmoothCorners(form);
                if (smoothCorners && form.Region is Region previous)
                {
                    form.Region = null;
                    previous.Dispose();
                }
                UpdateRegion();
            }

            form.HandleCreated += (s, e) => ApplyForHandle();
            form.SizeChanged += (s, e) => UpdateRegion();
            form.DpiChanged += (s, e) => UpdateRegion();
            if (form.IsHandleCreated) ApplyForHandle();
        }

        // 小羊图标取自运行中的 exe（ApplicationIcon），纯代码窗体没有设计器 resx 可用
        private static Icon _appIcon;
        public static Icon AppIcon =>
            _appIcon ?? (_appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath));

        internal static Bitmap CreateIconImage(Icon icon)
        {
            if (icon == null) return null;
            // .NET Framework 的 ToBitmap 会把 ICO 中的 PNG 数据当成 DIB，产生彩色噪点。
            // 从 Windows 已解码的 HICON 复制像素；返回的位图独立拥有像素，不接管原图标。
            using (var decoded = Icon.FromHandle(icon.Handle))
                return decoded.ToBitmap();
        }

        public static CreateParams WithShadow(CreateParams cp)
        {
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }

        internal static Panel ApplyStandardHeader(Form form, string title, NoteTheme theme, ToolIconButton extraButton = null)
        {
            float scale = UiDpi.Factor;
            var padding = form.Padding;
            int top = (int)Math.Round(CompactLayout.NoteFramePadding * scale);
            form.Padding = new Padding(padding.Left, top, padding.Right, padding.Bottom);
            var chrome = Apply(form, title, false, null, theme.Chrome, theme.ChromeText, extraButton, true, scale);
            ApplyHeaderTheme(chrome, theme);
            return chrome;
        }

        internal static void ApplyHeaderTheme(Panel chrome, NoteTheme theme)
        {
            chrome.BackColor = theme.Chrome;
            if (chrome.Tag is Label title) title.ForeColor = theme.ChromeText;
            foreach (Control control in chrome.Controls)
            {
                if (!(control is ToolIconButton button)) continue;
                button.Theme = theme;
                button.Invalidate();
            }
        }

        public static Panel Apply(Form f, string title, bool showGear, EventHandler onGear, Color chromeColor, Color chromeTextColor)
            => Apply(f, title, showGear, onGear, chromeColor, chromeTextColor, null);

        /// <param name="extraButton">额外标题栏按钮（如待办清单的 📌 置顶开关），排在齿轮左侧。</param>
        public static Panel Apply(Form f, string title, bool showGear, EventHandler onGear, Color chromeColor, Color chromeTextColor, ToolIconButton extraButton)
            => Apply(f, title, showGear, onGear, chromeColor, chromeTextColor, extraButton, true);

        /// <param name="showIcon">标题栏左侧是否放应用图标；列表类小窗传 false 只留标题。</param>
        public static Panel Apply(Form f, string title, bool showGear, EventHandler onGear, Color chromeColor, Color chromeTextColor, ToolIconButton extraButton, bool showIcon)
            => Apply(f, title, showGear, onGear, chromeColor, chromeTextColor, extraButton, showIcon, null);

        private static Panel Apply(Form f, string title, bool showGear, EventHandler onGear, Color chromeColor,
            Color chromeTextColor, ToolIconButton extraButton, bool showIcon, float? noteScale)
        {
            f.FormBorderStyle = FormBorderStyle.None;
            float scale = noteScale ?? 1f;
            int headerHeight = noteScale.HasValue ? (int)Math.Round(NoteHeaderHeight * scale) : HeaderHeight;
            int iconSize = (int)Math.Round(NoteHeaderLogoSize * scale);
            int buttonSize = noteScale.HasValue ? (int)Math.Round(NoteHeaderButtonSize * scale) : 34;
            Color divider = noteScale.HasValue ? Color.FromArgb(227, 232, 239) : Color.FromArgb(232, 237, 243);

            var chrome = new Panel { Dock = DockStyle.Top, Height = headerHeight, BackColor = chromeColor };
            chrome.Paint += (s, e) =>
            {
                using (var pen = new Pen(divider))
                    e.Graphics.DrawLine(pen, 0, chrome.Height - 1, chrome.Width, chrome.Height - 1);
            };
            chrome.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    Win32ApiHelper.ReleaseCapture();
                    Win32ApiHelper.SendMessage(f.Handle, Win32ApiHelper.WM_NCLBUTTONDOWN, Win32ApiHelper.HT_CAPTION, 0);
                }
            };

            PictureBox icon = null;
            if (showIcon)
            {
                icon = new PictureBox { Size = new Size(iconSize, iconSize), SizeMode = PictureBoxSizeMode.Zoom };
                var ic = f.Icon ?? AppIcon;
                if (ic != null)
                {
                    var image = CreateIconImage(ic);
                    icon.Image = image;
                    icon.Disposed += (s, e) => image.Dispose();
                }
            }
            var lbl = new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = chromeTextColor,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            var labelFont = lbl.Font;
            lbl.Disposed += (s, e) => labelFont.Dispose();
            var close = new ToolIconButton { IconId = "close", Size = new Size(buttonSize, buttonSize) };
            close.Click += (s, e) => f.Close();

            ToolIconButton gear = null;
            if (showGear)
            {
                gear = new ToolIconButton { IconId = "gear", Size = new Size(buttonSize, buttonSize) };
                if (onGear != null) gear.Click += onGear;
            }

            if (icon != null) chrome.Controls.Add(icon);
            chrome.Controls.Add(lbl);
            if (extraButton != null)
            {
                if (noteScale.HasValue) extraButton.Size = close.Size;
                chrome.Controls.Add(extraButton);
            }
            if (gear != null) chrome.Controls.Add(gear);
            chrome.Controls.Add(close);
            chrome.Tag = lbl; // 供换主题时同步标题文字色
            Action layout = () =>
            {
                // Match the note's client margins without shifting the body or resize hit areas.
                int left = (int)(10 * scale);
                int right = (int)(6 * scale);
                if (noteScale.HasValue)
                {
                    left += f.Padding.Top - f.Padding.Left;
                    right += f.Padding.Top - f.Padding.Right;
                }
                Layout(chrome, icon, lbl, extraButton, gear, close, left, right, (int)(7 * scale), (int)(2 * scale));
            };
            chrome.Resize += (s, e) => layout();
            f.Controls.Add(chrome);
            layout();

            return chrome;
        }

        private static void Layout(Panel chrome, PictureBox icon, Label lbl, ToolIconButton extra, ToolIconButton gear,
            ToolIconButton close, int left, int right, int labelGap, int buttonGap)
        {
            int cy = (chrome.Height - close.Height) / 2;
            close.Location = new Point(chrome.Width - right - close.Width, cy);
            int x = close.Left - buttonGap;
            if (gear != null)
            {
                gear.Location = new Point(x - gear.Width, cy);
                x = gear.Left;
            }
            if (extra != null)
            {
                extra.Location = new Point(x - extra.Width, cy);
            }
            if (icon != null)
            {
                icon.Location = new Point(left, (chrome.Height - icon.Height) / 2);
                lbl.Location = new Point(icon.Right + labelGap, (chrome.Height - lbl.Height) / 2);
            }
            else
            {
                lbl.Location = new Point(15, (chrome.Height - lbl.Height) / 2);
            }
        }

        /// <summary>边缘命中测试：返回系统拉伸码，0 表示不在边缘。</summary>
        public static int HitTest(Form f, Point clientPt, int border)
        {
            bool l = clientPt.X <= border, r = clientPt.X >= f.ClientSize.Width - border;
            bool t = clientPt.Y <= border, b = clientPt.Y >= f.ClientSize.Height - border;
            if (l && t) return 13;
            if (r && t) return 14;
            if (l && b) return 16;
            if (r && b) return 17;
            if (l) return 10;
            if (r) return 11;
            if (t) return 12;
            if (b) return 15;
            return 0;
        }
    }
}
