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

        public static Color IconColor = Color.FromArgb(85, 96, 107);
        public static Color IconHoverColor = Color.FromArgb(74, 127, 193);
        public static Color HoverBackColor = Color.FromArgb(221, 231, 243);
        public static Color PressedBackColor = Color.FromArgb(203, 216, 232);
        public static Color HoverBorderColor = Color.FromArgb(168, 196, 230);
        // 持久选中态：强调色浅染底 + 强调色描边，与强调色图标保持足够对比
        public static Color SelectedBackColor = Color.FromArgb(232, 240, 249);
        public static Color SelectedBorderColor = Color.FromArgb(168, 196, 230);

        // 固定图标用嵌入的线稿图钉 PNG（默认/悬停两色），其余图标为代码矢量
        private static Image _pinDef, _pinHov;
        private static Image PinDefault => _pinDef ?? (_pinDef = LoadPin("KimNotes.pin_def.png"));
        private static Image PinHover => _pinHov ?? (_pinHov = LoadPin("KimNotes.pin_hover.png"));
        private static Image LoadPin(string name)
        {
            using (var st = typeof(ToolIconButton).Assembly.GetManifestResourceStream(name))
                return new Bitmap(st);
        }

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

            if (hover || pressed || selected)
            {
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = RoundedPath(rect, 7))
                {
                    Color bg = selected ? SelectedBackColor : (pressed ? PressedBackColor : HoverBackColor);
                    Color bd = selected ? SelectedBorderColor : HoverBorderColor;
                    using (var brush = new SolidBrush(bg))
                        g.FillPath(brush, path);
                    using (var pen = new Pen(bd))
                        g.DrawPath(pen, path);
                }
            }

            var color = (hover || pressed || selected) ? IconHoverColor : IconColor;
            // 24 网格图标居中、占控件 75%（四周留白），与之前确认的版本比例一致
            float scale = Math.Min(Width, Height) * 0.75f / 24f;
            g.TranslateTransform(Width / 2f, Height / 2f);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-12f, -12f);
            if (iconId == "pin")
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage((hover || pressed || selected) ? PinHover : PinDefault, 0, 0, 24, 24);
            }
            else
            {
                DrawVectorIcon(g, iconId, color);
            }
            g.ResetTransform();
        }

        private static GraphicsPath RoundedPath(Rectangle rect, int r)
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

        // 统一 1.8 描边 + 圆头 + 抗锯齿，24 网格设计
        private static void DrawVectorIcon(Graphics g, string id, Color color)
        {
            using (var pen = new Pen(color, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            })
            using (var brush = new SolidBrush(color))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                var box = new RectangleF(0, 0, 24, 24);
                switch (id)
                {
                    case "bullet":
                        foreach (float y in new[] { 7f, 12f, 17f })
                        {
                            g.FillEllipse(brush, 4.6f, y - 1.3f, 2.6f, 2.6f);
                            g.DrawLine(pen, 9.5f, y, 19.5f, y);
                        }
                        break;
                    case "bold":
                        using (var f = new Font("Segoe UI", 13f, FontStyle.Bold))
                            g.DrawString("B", f, brush, box, sf);
                        break;
                    case "case":
                        using (var f = new Font("Segoe UI", 10.5f, FontStyle.Regular))
                            g.DrawString("Aa", f, brush, box, sf);
                        break;
                    case "translate":
                        using (var f = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular))
                            g.DrawString("译", f, brush, box, sf);
                        break;
                    case "add":
                        g.DrawLine(pen, 12, 5.5f, 12, 18.5f);
                        g.DrawLine(pen, 5.5f, 12, 18.5f, 12);
                        break;
                    case "scissors":
                        g.DrawEllipse(pen, 4.2f, 5.2f, 4.6f, 4.6f);
                        g.DrawEllipse(pen, 4.2f, 14.2f, 4.6f, 4.6f);
                        g.DrawLine(pen, 8.6f, 8.6f, 19, 16.5f);
                        g.DrawLine(pen, 8.6f, 15.4f, 19, 7.5f);
                        break;
                    case "notes":
                        g.DrawRectangle(pen, 6, 4.5f, 12, 15);
                        g.DrawLine(pen, 8.5f, 9, 15.5f, 9);
                        g.DrawLine(pen, 8.5f, 12, 15.5f, 12);
                        g.DrawLine(pen, 8.5f, 15, 13, 15);
                        break;
                    case "todo":
                        g.DrawRectangle(pen, 5, 5, 14, 14);
                        g.DrawLine(pen, 8.5f, 12, 11, 14.5f);
                        g.DrawLine(pen, 11, 14.5f, 15.5f, 9.5f);
                        break;
                    case "more": // 设置：三个点
                        foreach (float x in new[] { 6f, 12f, 18f })
                            g.FillEllipse(brush, x - 1.7f, 12 - 1.7f, 3.4f, 3.4f);
                        break;
                    case "search": // 搜索：放大镜
                        g.DrawEllipse(pen, 5.5f, 5.5f, 9.5f, 9.5f);
                        g.DrawLine(pen, 12.6f, 12.6f, 18.5f, 18.5f);
                        break;
                    case "close": // 关闭：叉
                        g.DrawLine(pen, 7, 7, 17, 17);
                        g.DrawLine(pen, 17, 7, 7, 17);
                        break;
                    case "gear": // 设置：齿轮
                        g.DrawEllipse(pen, 8.6f, 8.6f, 6.8f, 6.8f);
                        g.DrawEllipse(pen, 10.9f, 10.9f, 2.2f, 2.2f);
                        for (int i = 0; i < 8; i++)
                        {
                            float ang = i * (float)Math.PI / 4f;
                            float cx = 12f, cy = 12f;
                            float x1 = cx + (float)Math.Cos(ang) * 3.6f, y1 = cy + (float)Math.Sin(ang) * 3.6f;
                            float x2 = cx + (float)Math.Cos(ang) * 5.6f, y2 = cy + (float)Math.Sin(ang) * 5.6f;
                            g.DrawLine(pen, x1, y1, x2, y2);
                        }
                        break;
                }
            }
        }
    }
}
