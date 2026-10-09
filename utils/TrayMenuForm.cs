using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    /// <summary>
    /// 托盘右键菜单：无边框卡片 + 主题色 hover，与便签三窗同族。
    /// 右键托盘图标弹出；点击任意菜单项执行动作；点击菜单外部（含其他程序 / 桌面）自动关闭。
    /// 关闭用「低级鼠标钩子」实现，可跨进程捕获鼠标按下 —— 弥补无边框窗体失焦事件不可靠的问题。
    /// </summary>
    internal sealed class TrayMenuForm : Form
    {
        private readonly List<Row> rows = new List<Row>();
        private readonly Color accent, accentSoft, accentDeep, hair, textMain;
        private readonly float s;

        private sealed class Row
        {
            public string Text;
            public Action Action;
            public bool SeparatorAfter;
        }

        public TrayMenuForm()
        {
            s = UiDpi.Factor;
            var th = NoteTheme.Current();
            accent = th.IconHover;
            accentSoft = Blend(accent, Color.White, 0.86f);
            accentDeep = ControlPaint.Dark(accent, 0.28f);
            hair = Blend(th.Divider, Color.White, 0.5f);
            textMain = Color.FromArgb(43, 47, 54);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            DoubleBuffered = true;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            KeyPreview = true;
            KeyDown += (se, ev) => { if (ev.KeyCode == Keys.Escape) Close(); };
            hookProc = HookCallback;
        }

        public void AddItem(string text, Action action, bool separatorAfter = false)
        {
            rows.Add(new Row { Text = text, Action = action, SeparatorAfter = separatorAfter });
        }

        /// <summary>在锚点（鼠标屏幕坐标）上方、右对齐弹出，并夹到工作区内，同时挂上鼠标钩子。</summary>
        public void ShowAt(Point anchor)
        {
            int w = CalcWidth();
            int h = Build(w);
            var wa = Screen.PrimaryScreen.WorkingArea;
            int x = anchor.X - w + Dpi(2);
            int y = anchor.Y - h - Dpi(2);
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            Bounds = new Rectangle(x, y, w, h);
            SetRoundedRegion();
            Show();
            Deactivate += OnDeactivate;   // 同进程失焦兜底
            InstallMouseHook();           // 跨进程点击外部关闭（主力）
        }

        private int Dpi(int v) => (int)Math.Round(v * s);

        private int CalcWidth()
        {
            int max = 0;
            foreach (var r in rows)
                max = Math.Max(max, TextRenderer.MeasureText(r.Text, Font).Width);
            return max + Dpi(18) * 2 + Dpi(10);
        }

        private int Build(int w)
        {
            SuspendLayout();
            Controls.Clear();
            int rowH = Dpi(38);
            int sep = Dpi(9);
            int padX = Dpi(18);
            int y = Dpi(6);

            foreach (var r in rows)
            {
                var panel = new Panel
                {
                    Location = new Point(0, y),
                    Size = new Size(w, rowH),
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand,
                    Tag = r
                };
                var lbl = new Label
                {
                    Text = r.Text,
                    AutoSize = false,
                    Bounds = new Rectangle(padX, 0, w - padX - Dpi(10), rowH),
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = textMain,
                    Font = Font,
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand,
                    Tag = r
                };
                panel.Controls.Add(lbl);
                Hook(panel, lbl, r);
                Controls.Add(panel);
                y += rowH;
                if (r.SeparatorAfter)
                {
                    var sp = new Sep(hair, padX)
                    {
                        Location = new Point(0, y),
                        Size = new Size(w, sep),
                        BackColor = Color.White
                    };
                    Controls.Add(sp);
                    y += sep;
                }
            }

            int h = y + Dpi(6);
            ResumeLayout();
            return h;
        }

        private void Hook(Panel panel, Label lbl, Row r)
        {
            EventHandler enter = (se, ev) => SetHover(r);
            EventHandler leave = (se, ev) =>
            {
                var p = (Control)se;
                if (!p.Bounds.Contains(PointToClient(Cursor.Position))) SetHover(null);
            };
            EventHandler click = (se, ev) =>
            {
                UninstallMouseHook();
                r.Action?.Invoke();
                if (!IsDisposed) Close();
            };
            panel.MouseEnter += enter;
            panel.MouseLeave += leave;
            panel.Click += click;
            lbl.MouseEnter += enter;
            lbl.MouseLeave += leave;
            lbl.Click += click;
        }

        private void SetHover(Row r)
        {
            foreach (Control c in Controls)
            {
                if (c is Panel p && p.Tag is Row rr)
                {
                    bool on = rr == r;
                    p.BackColor = on ? accentSoft : Color.Transparent;
                    if (p.Controls.Count > 0 && p.Controls[0] is Label l)
                        l.ForeColor = on ? accentDeep : textMain;
                }
            }
        }

        // 同进程失焦兜底（无边框窗体未必能激活，主要靠钩子）
        private void OnDeactivate(object sender, EventArgs e)
        {
            Deactivate -= OnDeactivate;
            if (!IsDisposed) Close();
        }

        // ---- 低级鼠标钩子：跨进程捕获屏幕上的鼠标按下，点在菜单外即关闭 ----
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly HookProc hookProc;
        private IntPtr hookId = IntPtr.Zero;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private void InstallMouseHook()
        {
            if (hookId != IntPtr.Zero) return;
            hookId = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
        }

        private void UninstallMouseHook()
        {
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN
                            || wParam == (IntPtr)WM_RBUTTONDOWN
                            || wParam == (IntPtr)WM_MBUTTONDOWN))
            {
                try
                {
                    var ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    var p = new Point(ms.pt.x, ms.pt.y);
                    // 点在菜单外才关；菜单内（含鼠标按下选中项）交给 Click 处理
                    if (!IsDisposed && !Bounds.Contains(p))
                        BeginInvoke((Action)(() => { if (!IsDisposed) Close(); }));
                }
                catch { }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UninstallMouseHook();
            base.OnFormClosing(e);
        }

        // 无边框圆角卡片 + 细线描边（投影走 CreateParams）
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Dpi(10)))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(hair)) g.DrawPath(p, path);
            }
            base.OnPaint(e);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW：悬浮卡片投影
                return cp;
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var path = new GraphicsPath();
            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>把窗口裁剪为圆角，四个圆角外的三角透明，与卡片描边一致。</summary>
        private void SetRoundedRegion()
        {
            using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Dpi(10)))
                Region = new Region(path);
        }

        private sealed class Sep : Control
        {
            private readonly Color line;
            private readonly int padX;
            public Sep(Color line, int padX)
            {
                this.line = line;
                this.padX = padX;
                DoubleBuffered = true;
                TabStop = false;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                int my = Height / 2;
                using (var p = new Pen(line)) e.Graphics.DrawLine(p, padX, my, Width - padX, my);
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
