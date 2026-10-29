using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KimNotes.utils
{
    internal sealed class ScreenshotToolbarPanel : Panel
    {
        internal readonly Dictionary<string, ToolIconButton> Buttons = new Dictionary<string, ToolIconButton>();
        private readonly ToolTip tips = new ToolTip();
        private readonly List<Control> items = new List<Control>();
        private readonly Action<string> invoke;
        private int cornerRadius = 8;

        internal ScreenshotToolbarPanel(Action<string> invoke)
        {
            this.invoke = invoke;
            DoubleBuffered = true;
            BackColor = Color.White;
            Cursor = Cursors.Default;
            Add("rect", "矩形标注"); Add("arrow", "箭头标注"); Add("mosaic", "马赛克"); Add("text", "文字标注");
            Separator();
            Add("ocr", "识别文字 OCR"); Add("eyedropper", "拾色"); Add("notes", "插入最近打开的便签");
            Add("undo", "撤销标注 (Ctrl+Z)"); Add("save", "另存图片"); Add("copy", "复制图片");
            Separator();
            Add("close", "取消 / 关闭"); Add("check", "复制并贴图");
            Buttons["close"].GlyphColor = Color.FromArgb(218, 85, 91);
            Buttons["check"].GlyphColor = Color.FromArgb(25, 171, 120);
            Buttons["undo"].Enabled = false;
        }

        private void Add(string id, string label)
        {
            var button = new ToolIconButton { IconId = id, IconSize = 18, AccessibleName = label, BackColor = Color.White };
            button.Click += (s, e) => invoke(id);
            tips.SetToolTip(button, label);
            Buttons.Add(id, button); items.Add(button); Controls.Add(button);
        }

        private void Separator()
        {
            var line = new Panel { BackColor = Color.FromArgb(231, 235, 240), Tag = "separator" };
            items.Add(line); Controls.Add(line);
        }

        internal void SetHint(string id, string text)
        {
            Buttons[id].AccessibleName = text;
            tips.SetToolTip(Buttons[id], text);
        }

        internal void Arrange(float scale, int maximumWidth)
        {
            int available = Math.Max(60, maximumWidth);
            float renderScale = Math.Min(scale, available / 327f);
            int pad = Math.Max(2, (int)Math.Round(6 * renderScale));
            int gap = Math.Max(1, (int)Math.Round(renderScale));
            int divider = Math.Max(2, (int)Math.Round(8 * renderScale));
            int side = Math.Max(1, Math.Min((int)Math.Round(24 * renderScale),
                (available - pad * 2 - divider * 2 - (Buttons.Count - 1) * gap) / Buttons.Count));
            int width = pad * 2 + Buttons.Count * side + (Buttons.Count - 1) * gap + 2 * divider;
            int x = pad, y = pad;
            SuspendLayout();
            foreach (var item in items)
            {
                bool separator = item.Tag as string == "separator";
                if (item is ToolIconButton button) button.IconScaleOverride = side / 24f;
                item.Visible = true;
                item.Bounds = separator ? new Rectangle(x + divider / 2, y + side / 5, Math.Max(1, (int)renderScale), side * 3 / 5)
                    : new Rectangle(x, y, side, side);
                x += separator ? divider : side + gap;
            }
            Size = new Size(width, y + side + pad);
            cornerRadius = Math.Max(2, Math.Min(Height / 2, (int)(8 * renderScale)));
            using (var path = ToolIconButton.RoundedPath(new Rectangle(0, 0, Width, Height), cornerRadius))
            {
                var previous = Region; Region = new Region(path); previous?.Dispose();
            }
            ResumeLayout();
            Invalidate();
        }

        internal static Point Place(Rectangle target, Size toolbar, Rectangle workArea, int gap)
        {
            int x = target.Right - toolbar.Width;
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - toolbar.Width));
            int y = target.Bottom + gap;
            if (y + toolbar.Height > workArea.Bottom) y = target.Top - gap - toolbar.Height;
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - toolbar.Height));
            return new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = ToolIconButton.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), cornerRadius))
            using (var pen = new Pen(Color.FromArgb(210, 218, 227))) e.Graphics.DrawPath(pen, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class ScreenshotToolbarForm : AppWindow
    {
        internal readonly ScreenshotToolbarPanel Tools;
        internal ScreenshotToolbarForm(Action<string> invoke)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            Tools = new ScreenshotToolbarPanel(invoke);
            Controls.Add(Tools);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; cp.ClassStyle |= 0x00020000; return cp; }
        }
        internal void Follow(Form owner)
        {
            if (IsDisposed || owner.IsDisposed) return;
            var screen = Screen.FromRectangle(owner.Bounds);
            var wa = screen.WorkingArea;
            // Enter the target monitor before sizing; its native DPI transition must
            // finish before applying the toolbar's absolute logical dimensions.
            if (!IsHandleCreated || Screen.FromControl(this).DeviceName != screen.DeviceName)
                Location = new Point(wa.Left + 4, wa.Top + 4);
            var handle = Handle;
            float scale = UiDpi.FactorFor(owner.Handle);
            Tools.Arrange(scale, wa.Width - 8);
            ClientSize = Tools.Size;
            var previous = Region; Region = Tools.Region.Clone(); previous?.Dispose();
            Location = ScreenshotToolbarPanel.Place(owner.Bounds, Size, wa, (int)(8 * scale));
        }
    }
}
