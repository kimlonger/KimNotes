using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class Form1 : Form
    {
        private Point mouseDownLocation;

        public Form1()
        {
            InitializeComponent();

            // 确保窗体启用双缓冲
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer, true);
            this.UpdateStyles();

            // 启用 RichTextBox 的双缓冲
            richTextBox1.GetType().GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(richTextBox1, true, null);

            richTextBox1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            this.BackColor = Color.FromArgb(255, 242, 171);
            richTextBox1.BackColor = Color.FromArgb(255, 247, 209);
            richTextBox1.BorderStyle = BorderStyle.None;

            // 设置窗体为无边框
            this.FormBorderStyle = FormBorderStyle.None;

            this.MouseDown += new MouseEventHandler(Form1_MouseDown);
            this.MouseMove += new MouseEventHandler(Form1_MouseMove);

            SetFormRoundCorners(15); // 设置圆角

            // 注册 Paint 事件
            this.Paint += new PaintEventHandler(Form1_Paint);

            // 启用调整大小时自动重绘
            this.SetStyle(ControlStyles.ResizeRedraw, true);
        }

        // 允许通过边缘调整窗体大小
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTCLIENT = 1;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (m.Msg == WM_NCHITTEST)
            {
                int x = (int)(m.LParam.ToInt64() & 0xFFFF);
                int y = (int)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                Point pt = PointToClient(new Point(x, y));
                Size clientSize = ClientSize;

                // 边缘检测范围（5像素）
                int borderWidth = 5;

                bool left = pt.X <= borderWidth;
                bool right = pt.X >= clientSize.Width - borderWidth;
                bool top = pt.Y <= borderWidth;
                bool bottom = pt.Y >= clientSize.Height - borderWidth;

                if (top && left) m.Result = (IntPtr)HTTOPLEFT;
                else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
                else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (left) m.Result = (IntPtr)HTLEFT;
                else if (right) m.Result = (IntPtr)HTRIGHT;
                else if (top) m.Result = (IntPtr)HTTOP;
                else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                else
                {
                    m.Result = (IntPtr)HTCLIENT; // 其他区域保持可拖动
                }
                return;
            }
            base.WndProc(ref m);
        }
        private void Form1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                mouseDownLocation = e.Location;
            }
        }

        private void Form1_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                this.Left += e.X - mouseDownLocation.X;
                this.Top += e.Y - mouseDownLocation.Y;
            }
        }

        private void SetFormRoundCorners(int radius)
        {
            GraphicsPath path = new GraphicsPath();
            path.StartFigure();

            path.AddArc(new Rectangle(0, 0, radius, radius), 180, 90); // 左上角
            path.AddArc(new Rectangle(this.Width - radius, 0, radius, radius), 270, 90); // 右上角
            path.AddArc(new Rectangle(this.Width - radius, this.Height - radius, radius, radius), 0, 90); // 右下角
            path.AddArc(new Rectangle(0, this.Height - radius, radius, radius), 90, 90); // 左下角

            path.CloseFigure();

            this.Region = new Region(path);
        }

        protected override void OnResize(EventArgs e)
        {
            this.SuspendLayout(); // 暂停布局逻辑
            base.OnResize(e);
            SetFormRoundCorners(15); // 保持圆角
            this.Invalidate(); // 触发Paint事件重绘边框
            richTextBox1.Refresh(); // 强制RichTextBox立即重绘
            this.ResumeLayout(); // 恢复布局逻辑
        }

        // Paint事件中的修改确保边框和内容得到适当的重绘
        private void Form1_Paint(object sender, PaintEventArgs e)
        {
            using (Pen pen = new Pen(Color.FromArgb(255, 242, 171), 5)) // 5像素宽边框
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                int penInset = (int)(pen.Width / 2);

                Rectangle rect = new Rectangle(
                    penInset,
                    penInset,
                    this.ClientSize.Width - 1 - penInset * 2,
                    this.ClientSize.Height - 1 - penInset * 2
                );

                e.Graphics.DrawRectangle(pen, rect);
            }
        }
    }
}