using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes.utils
{
    internal partial class ScreenOverlay : Form
    {
        private Point selectionStart;
        private Rectangle selectionRect;
        private Bitmap screenSnapshot;
        public Bitmap ScreenSnapshot => screenSnapshot;
        private Screen currentScreen;
        public Screen CurrentScreen => currentScreen;

        public Rectangle SelectedArea { get; private set; }
       
        public ScreenOverlay()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Cursor = Cursors.Cross;

            // 获取当前鼠标所在的屏幕
            currentScreen = Screen.FromPoint(Cursor.Position);
            
            // 设置窗口位置和大小以匹配当前屏幕
            this.StartPosition = FormStartPosition.Manual;
            this.Location = currentScreen.Bounds.Location;
            this.Size = currentScreen.Bounds.Size;
            
            // 只截取当前屏幕的内容
            screenSnapshot = new Bitmap(currentScreen.Bounds.Width, currentScreen.Bounds.Height);
            using (Graphics g = Graphics.FromImage(screenSnapshot))
            {
                g.CopyFromScreen(currentScreen.Bounds.Location, Point.Empty, currentScreen.Bounds.Size);
            }

            this.Paint += OverlayPaint;
            this.MouseDown += OverlayMouseDown;
            this.MouseMove += OverlayMouseMove;
            this.MouseUp += OverlayMouseUp;
            this.KeyPress += OverlayKeyPress;
            this.MouseEnter += OverlayMouseEnter;
        }

        private void OverlayMouseEnter(object sender, EventArgs e)
        {
            // 当鼠标进入窗口时，检查是否需要切换到其他屏幕
            var newScreen = Screen.FromPoint(Cursor.Position);
            if (newScreen != currentScreen)
            {
                currentScreen = newScreen;
                this.Location = currentScreen.Bounds.Location;
                this.Size = currentScreen.Bounds.Size;
                
                // 重新截取新屏幕的内容
                screenSnapshot.Dispose();
                screenSnapshot = new Bitmap(currentScreen.Bounds.Width, currentScreen.Bounds.Height);
                using (Graphics g = Graphics.FromImage(screenSnapshot))
                {
                    g.CopyFromScreen(currentScreen.Bounds.Location, Point.Empty, currentScreen.Bounds.Size);
                }
                
                this.Invalidate();
            }
        }

        // 添加边缘调整大小功能
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;

            if (m.Msg == WM_NCHITTEST)
            {
                // 始终将鼠标事件视为客户端区域
                m.Result = (IntPtr)1; // HTCLIENT
                return;
            }

            base.WndProc(ref m);
        }

        private void OverlayMouseDown(object sender, MouseEventArgs e)
        {
            selectionStart = e.Location;
            selectionRect = Rectangle.Empty;
        }

        private void OverlayMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            int x = Math.Min(selectionStart.X, e.X);
            int y = Math.Min(selectionStart.Y, e.Y);
            int width = Math.Abs(e.X - selectionStart.X);
            int height = Math.Abs(e.Y - selectionStart.Y);

            selectionRect = new Rectangle(x, y, width, height);
            this.Invalidate();
        }

        // 修改 OverlayMouseUp 方法
        private void OverlayMouseUp(object sender, MouseEventArgs e)
        {
            if (selectionRect.Width <= 0 || selectionRect.Height <= 0) return;

            // 转换为屏幕绝对坐标
            Point screenTopLeft = this.PointToScreen(selectionRect.Location);
            SelectedArea = new Rectangle(
                screenTopLeft.X,
                screenTopLeft.Y,
                selectionRect.Width,
                selectionRect.Height
            );
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void OverlayKeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Escape) this.Close();
        }

        private void OverlayPaint(object sender, PaintEventArgs e)
        {
            using (TextureBrush brush = new TextureBrush(screenSnapshot))
            {
                e.Graphics.FillRectangle(brush, this.ClientRectangle);
            }

            // 绘制选区
            if (!selectionRect.IsEmpty)
            {
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    e.Graphics.DrawRectangle(pen, selectionRect);
                }

                // 高亮选区外部区域
                using (Region region = new Region(this.ClientRectangle))
                {
                    region.Exclude(selectionRect);
                    e.Graphics.FillRegion(new SolidBrush(Color.FromArgb(128, Color.Black)), region);
                }
            }
        }

        // 在ScreenOverlay类中添加：
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // 显示坐标信息
            if (!selectionRect.IsEmpty)
            {
                string info = $"{selectionRect.Width} x {selectionRect.Height}";
                e.Graphics.DrawString(info, this.Font, Brushes.White,
                    selectionRect.X + 5, selectionRect.Y + 5);
            }
        }

        // 添加网格线辅助定位
        private void DrawGrid(Graphics g)
        {
            using (Pen gridPen = new Pen(Color.FromArgb(50, Color.White)))
            {
                for (int x = 0; x < this.Width; x += 50)
                    g.DrawLine(gridPen, x, 0, x, this.Height);
                for (int y = 0; y < this.Height; y += 50)
                    g.DrawLine(gridPen, 0, y, this.Width, y);
            }
        }
    }
}
