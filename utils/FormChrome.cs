using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes
{
    /// <summary>
    /// 统一窗体 chrome：无边框卡片 + 白色标题条（图标+标题+[齿轮]+关闭）+ 投影圆角 + 可选边缘拉伸。
    /// 供 历史页 / 设置页 复用，与便签主窗观感一致。
    /// </summary>
    public static class FormChrome
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // 小羊图标取自运行中的 exe（ApplicationIcon），纯代码窗体没有设计器 resx 可用
        private static Icon _appIcon;
        public static Icon AppIcon =>
            _appIcon ?? (_appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath));

        public static CreateParams WithShadow(CreateParams cp)
        {
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }

        public static Panel Apply(Form f, string title, bool showGear, EventHandler onGear, Color chromeColor, Color chromeTextColor)
            => Apply(f, title, showGear, onGear, chromeColor, chromeTextColor, null);

        /// <param name="extraButton">额外标题栏按钮（如待办清单的 📌 置顶开关），排在齿轮左侧。</param>
        public static Panel Apply(Form f, string title, bool showGear, EventHandler onGear, Color chromeColor, Color chromeTextColor, ToolIconButton extraButton)
            => Apply(f, title, showGear, onGear, chromeColor, chromeTextColor, extraButton, true);

        /// <param name="showIcon">标题栏左侧是否放应用图标；列表类小窗传 false 只留标题。</param>
        public static Panel Apply(Form f, string title, bool showGear, EventHandler onGear, Color chromeColor, Color chromeTextColor, ToolIconButton extraButton, bool showIcon)
        {
            f.FormBorderStyle = FormBorderStyle.None;

            var chrome = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = chromeColor };
            chrome.Paint += (s, e) =>
            {
                using (var pen = new Pen(ControlPaint.Dark(chromeColor, 0.08f)))
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
                icon = new PictureBox { Size = new Size(18, 18), SizeMode = PictureBoxSizeMode.Zoom };
                var ic = f.Icon ?? AppIcon;
                if (ic != null) icon.Image = ic.ToBitmap();
            }
            var lbl = new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = chromeTextColor,
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            var close = new ToolIconButton { IconId = "close", Size = new Size(34, 34) };
            close.Click += (s, e) => f.Close();

            ToolIconButton gear = null;
            if (showGear)
            {
                gear = new ToolIconButton { IconId = "gear", Size = new Size(34, 34) };
                if (onGear != null) gear.Click += onGear;
            }

            if (icon != null) chrome.Controls.Add(icon);
            chrome.Controls.Add(lbl);
            if (extraButton != null) chrome.Controls.Add(extraButton);
            if (gear != null) chrome.Controls.Add(gear);
            chrome.Controls.Add(close);
            chrome.Tag = lbl; // 供换主题时同步标题文字色
            chrome.Resize += (s, e) => Layout(chrome, icon, lbl, extraButton, gear, close);
            f.Controls.Add(chrome);
            Layout(chrome, icon, lbl, extraButton, gear, close);

            f.Load += (s, e) =>
            {
                try { int v = 2; DwmSetWindowAttribute(f.Handle, 33, ref v, 4); } catch { } // Win11 圆角
            };
            return chrome;
        }

        private static void Layout(Panel chrome, PictureBox icon, Label lbl, ToolIconButton extra, ToolIconButton gear, ToolIconButton close)
        {
            int cy = (chrome.Height - close.Height) / 2;
            close.Location = new Point(chrome.Width - 6 - close.Width, cy);
            int x = close.Left - 2;
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
                icon.Location = new Point(10, (chrome.Height - icon.Height) / 2);
                lbl.Location = new Point(icon.Right + 7, (chrome.Height - lbl.Height) / 2);
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
