using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KimNotes
{
    /// <summary>
    /// 自绘工具栏按钮：设计器与运行期用同一套 OnPaint 绘制矢量图标，保证所见即所得。
    /// </summary>
    public class ToolIconButton : Control
    {
        private bool hover;
        private bool pressed;
        private bool selected;
        private string iconId = "";
        public NoteTheme Theme { get; set; }

        public static Color IconColor = Color.FromArgb(85, 96, 107);
        public static Color IconHoverColor = Color.FromArgb(74, 127, 193);
        public static Color HoverBackColor = Color.FromArgb(221, 231, 243);
        public static Color PressedBackColor = Color.FromArgb(203, 216, 232);
        public static Color HoverBorderColor = Color.FromArgb(168, 196, 230);
        // 持久选中态：强调色浅染底 + 强调色描边，与强调色图标保持足够对比
        public static Color SelectedBackColor = Color.FromArgb(232, 240, 249);
        public static Color SelectedBorderColor = Color.FromArgb(168, 196, 230);
        // 强调色实心档（主操作按钮：完成标注）
        public static Color AccentColor = Color.FromArgb(74, 127, 193);
        public static Color AccentHoverColor = Color.FromArgb(63, 111, 174);
        public static Color AccentPressedColor = Color.FromArgb(53, 96, 156);

        [Category("外观")]
        [Description("矢量图标标识：bullet/bold/case/translate/add/pin/scissors/notes/todo")]
        public string IconId
        {
            get { return iconId; }
            set { iconId = value; Invalidate(); }
        }

        // 持久选中态（开关类按钮：固定/加粗/项目符号），配色随主题静态色
        [Category("外观")]
        [Description("开关型按钮的持久选中态")]
        public bool Selected
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; Invalidate(); }
        }

        // 主操作档：整颗按钮铺强调色、图标反白（如标注工具栏的「完成」）
        [Category("外观")]
        [Description("强调色实心背景，用于主操作按钮")]
        public bool Accent
        {
            get { return accent; }
            set { if (accent == value) return; accent = value; Invalidate(); }
        }
        private bool accent;

        public ToolIconButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
            Size = new Size(32, 32);
            MouseEnter += (s, e) => { hover = true; Invalidate(); };
            MouseLeave += (s, e) => { hover = false; pressed = false; Invalidate(); };
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } };
            MouseUp += (s, e) => { pressed = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            if (accent || hover || pressed || selected)
            {
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                // 圆角随按钮尺寸走：高分屏下按钮大一圈，写死 7px 会显得方
                int r = Math.Max(4, (int)(Math.Min(Width, Height) * 0.22f));
                using (var path = RoundedPath(rect, r))
                {
                    Color bg, bd;
                    if (accent)
                    {
                        bg = pressed ? AccentPressedColor : (hover ? AccentHoverColor : AccentColor);
                        bd = bg;
                    }
                    else
                    {
                        bg = Theme == null ? (selected ? SelectedBackColor : (pressed ? PressedBackColor : HoverBackColor))
                            : (selected ? ControlPaint.Light(Theme.Body, 0.2f) : ControlPaint.Dark(Theme.Body, pressed ? 0.14f : 0.06f));
                        bd = Theme == null ? (selected ? SelectedBorderColor : HoverBorderColor) : Theme.IconHover;
                    }
                    using (var brush = new SolidBrush(bg))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(bd))
                        g.DrawPath(pen, path);
                }
            }

            var color = accent ? Color.White
                : ((hover || pressed || selected) ? (Theme?.IconHover ?? IconHoverColor) : (Theme?.Icon ?? IconColor));
            // Every icon uses one 24-unit vector grid, including lettering and the pin.
            if (!Enabled) color = Color.FromArgb(100, color);
            float scale = Math.Min(Width, Height) * 0.83f / 24f;
            var transform = g.Save();
            g.TranslateTransform(Width / 2f, Height / 2f);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-12f, -12f);
            DrawVectorIcon(g, iconId, color);
            g.Restore(transform);
        }

        internal static GraphicsPath RoundedPath(Rectangle rect, int r)
        {
            var path = new GraphicsPath();
            int d = r * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Consistent 1.8-unit rounded strokes; optical bounds lie within the same 4..20 keyline.
        private static void DrawVectorIcon(Graphics g, string id, Color color)
        {
            using (var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(color))
            {
                switch (id)
                {
                    case "calendar":
                        g.DrawRectangle(pen, 4, 6, 16, 14);
                        g.DrawLine(pen, 4, 10, 20, 10);
                        g.DrawLine(pen, 8, 4, 8, 8);
                        g.DrawLine(pen, 16, 4, 16, 8);
                        g.DrawLine(pen, 8, 14, 10, 14);
                        g.DrawLine(pen, 14, 14, 16, 14);
                        break;
                    case "clock":
                        g.DrawEllipse(pen, 4, 4, 16, 16);
                        g.DrawLine(pen, 12, 7, 12, 12);
                        g.DrawLine(pen, 12, 12, 16, 14);
                        break;
                    case "bullet":
                        foreach (float y in new[] { 5f, 12f, 19f })
                        {
                            g.FillEllipse(brush, 3.5f, y - 1.2f, 2.4f, 2.4f);
                            g.DrawLine(pen, 9, y, 20, y);
                        }
                        break;
                    case "bold":
                        g.DrawLine(pen, 7, 5, 7, 19);
                        using (var path = new GraphicsPath())
                        {
                            path.AddLine(7, 5, 12, 5);
                            path.AddBezier(12, 5, 20, 5, 20, 12, 12, 12);
                            path.AddLine(12, 12, 7, 12);
                            path.StartFigure();
                            path.AddLine(7, 12, 12, 12);
                            path.AddBezier(12, 12, 21, 12, 21, 19, 12, 19);
                            path.AddLine(12, 19, 7, 19);
                            g.DrawPath(pen, path);
                        }
                        break;
                    case "case":
                        g.DrawLines(pen, new[] { new PointF(3.5f, 19), new PointF(8, 5), new PointF(12.5f, 19) });
                        g.DrawLine(pen, 5.7f, 12.5f, 10.3f, 12.5f);
                        g.DrawEllipse(pen, 14, 12, 6, 7);
                        g.DrawLine(pen, 20, 11.5f, 20, 19);
                        break;
                    case "translate":
                        g.DrawLine(pen, 4, 7, 14, 7);
                        g.DrawLine(pen, 9, 4, 9, 7);
                        g.DrawBezier(pen, 12.5f, 7, 12, 13, 8, 17, 4, 19);
                        g.DrawBezier(pen, 6, 10, 7, 13, 9, 15, 12, 16);
                        g.DrawLines(pen, new[] { new PointF(13, 20), new PointF(17, 10), new PointF(21, 20) });
                        g.DrawLine(pen, 14.7f, 16, 19.3f, 16);
                        break;
                    case "add":
                        g.DrawLine(pen, 12, 4, 12, 20);
                        g.DrawLine(pen, 4, 12, 20, 12);
                        break;
                    case "pin":
                        g.DrawPolygon(pen, new[] { new PointF(13, 4), new PointF(20, 11), new PointF(17, 12),
                            new PointF(15, 17), new PointF(7, 9), new PointF(12, 7) });
                        g.DrawLine(pen, 11, 13, 5, 19);
                        break;
                    case "scissors":
                        g.DrawEllipse(pen, 4, 4.5f, 5, 5);
                        g.DrawEllipse(pen, 4, 14.5f, 5, 5);
                        g.DrawLine(pen, 8.5f, 8.5f, 20, 18.5f);
                        g.DrawLine(pen, 8.5f, 15.5f, 20, 5.5f);
                        break;
                    case "notes":
                        g.DrawRectangle(pen, 5, 4, 14, 16);
                        g.DrawLine(pen, 8.5f, 8, 15.5f, 8);
                        g.DrawLine(pen, 8.5f, 12, 15.5f, 12);
                        g.DrawLine(pen, 8.5f, 16, 13, 16);
                        break;
                    case "todo":
                        g.DrawRectangle(pen, 4, 4, 16, 16);
                        g.DrawLines(pen, new[] { new PointF(8, 12), new PointF(11, 15), new PointF(16, 9) });
                        break;
                    case "more":
                        foreach (float x in new[] { 5f, 12f, 19f }) g.FillEllipse(brush, x - 1.2f, 10.8f, 2.4f, 2.4f);
                        break;
                    case "search":
                        g.DrawEllipse(pen, 4.5f, 4.5f, 11, 11);
                        g.DrawLine(pen, 14, 14, 20, 20);
                        break;
                    case "close":
                        g.DrawLine(pen, 6, 6, 18, 18);
                        g.DrawLine(pen, 18, 6, 6, 18);
                        break;
                    case "rect":
                        g.DrawRectangle(pen, 4, 5, 16, 14);
                        break;
                    case "arrow":
                        g.DrawLine(pen, 5, 19, 19, 5);
                        g.DrawLines(pen, new[] { new PointF(10, 5), new PointF(19, 5), new PointF(19, 14) });
                        break;
                    case "mosaic":
                        foreach (float x in new[] { 4.5f, 13.5f })
                            foreach (float y in new[] { 4.5f, 13.5f }) g.DrawRectangle(pen, x, y, 6, 6);
                        break;
                    case "text":
                        g.DrawLines(pen, new[] { new PointF(4, 7), new PointF(4, 5), new PointF(20, 5), new PointF(20, 7) });
                        g.DrawLine(pen, 12, 5, 12, 19);
                        g.DrawLine(pen, 8.5f, 19, 15.5f, 19);
                        break;
                    case "undo":
                        g.DrawLines(pen, new[] { new PointF(9, 5), new PointF(4, 10), new PointF(9, 15) });
                        g.DrawLine(pen, 4, 10, 13, 10);
                        g.DrawBezier(pen, 13, 10, 22, 10, 22, 19, 13, 19);
                        g.DrawLine(pen, 13, 19, 9, 19);
                        break;
                    case "check":
                        g.DrawLines(pen, new[] { new PointF(4, 12), new PointF(10, 18), new PointF(20, 6) });
                        break;
                    case "gear":
                        var points = new PointF[32];
                        for (int i = 0; i < points.Length; i++)
                        {
                            double angle = i * Math.PI / 16;
                            float radius = i % 4 < 2 ? 8f : 6.4f;
                            points[i] = new PointF(12 + (float)Math.Cos(angle) * radius, 12 + (float)Math.Sin(angle) * radius);
                        }
                        g.DrawPolygon(pen, points);
                        g.DrawEllipse(pen, 9, 9, 6, 6);
                        break;
                }
            }
        }
    }
}
