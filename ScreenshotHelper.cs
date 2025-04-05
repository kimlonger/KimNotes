using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.IO;

namespace KimNotes
{
    public static class ScreenshotHelper
    {

        /// <summary>
        /// 启动交互式截图并返回截图窗体
        /// </summary>
        public static Form CaptureInteractive()
        {
            using (var overlay = new ScreenOverlay())
            {
                if (overlay.ShowDialog() != DialogResult.OK) return null;

                var area = overlay.SelectedArea;
                var screenshot = CaptureArea(area);
                return CreateScreenshotForm(screenshot, area);
            }
        }

        /// <summary>
        /// 截取指定屏幕区域
        /// </summary>
        private static Bitmap CaptureArea(Rectangle area)
        {
            var screenshot = new Bitmap(area.Width, area.Height);
            using (var g = Graphics.FromImage(screenshot))
            {
                g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            }
            return screenshot;
        }

        /// <summary>
        /// 创建显示截图的悬浮窗
        /// </summary>
        private static Form CreateScreenshotForm(Bitmap screenshot, Rectangle area)
        {
            var form = BuildBaseForm(area);
            var contextMenu = BuildContextMenu(screenshot, form); // 传入当前窗体实例
            var pb = BuildPictureBox(screenshot, contextMenu);

            // 构建嵌套面板结构
            var borderPanel = BuildNestedPanels(pb);
            form.Controls.Add(borderPanel);

            // 事件绑定
            BindEvents(form, pb, screenshot);

            return form;
        }

        /// <summary>
        /// 构建基础窗体结构
        /// </summary>
        private static Form BuildBaseForm(Rectangle area)
        {
            return new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = area.Location,
                ClientSize = new Size(area.Width + 6, area.Height + 6),
                Padding = new Padding(1)
            };
        }

        /// <summary>
        /// 构建右键菜单
        /// </summary>
        private static ContextMenuStrip BuildContextMenu(Bitmap screenshot, Form hostForm)
        {
            var menu = new ContextMenuStrip();
            // 复用保存逻辑
            menu.Items.Add(new ToolStripMenuItem("复制", null, (s, e) => Clipboard.SetImage(screenshot)));
            menu.Items.Add(new ToolStripMenuItem("另存为...", null, (s, e) => SaveWithDialog(screenshot)));
            menu.Items.Add(new ToolStripMenuItem("销毁", null, (s, e) =>
            {
                hostForm.Close(); // 直接关闭宿主窗体
            }));
            return menu;
        }

        /// <summary>
        /// 构建 PictureBox 控件
        /// </summary>
        private static PictureBox BuildPictureBox(Bitmap screenshot, ContextMenuStrip menu)
        {
            return new PictureBox
            {
                Image = screenshot,
                SizeMode = PictureBoxSizeMode.StretchImage,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ContextMenuStrip = menu
            };
        }

        /// <summary>
        /// 构建嵌套边框面板
        /// </summary>
        private static Panel BuildNestedPanels(Control content)
        {
            var borderPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(173, 216, 230),
                Padding = new Padding(1)
            };

            var innerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            innerPanel.Controls.Add(content);
            borderPanel.Controls.Add(innerPanel);

            return borderPanel;
        }

        /// <summary>
        /// 绑定窗体事件
        /// </summary>
        private static void BindEvents(Form form, PictureBox pb, Bitmap screenshot)
        {
            // 双击保存
            pb.DoubleClick += (s, e) => SaveAndClose(form, screenshot);

            // 左键拖动
            pb.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && e.Clicks == 1)
                {
                    StartFormDrag(form);
                }
            };

            // 滚轮缩放
            pb.MouseWheel += (s, e) => ZoomForm(form, e.Delta > 0 ? 1.1f : 0.9f);

            // 释放资源
            form.FormClosed += (s, e) => screenshot.Dispose();
        }

        /// <summary>
        /// 统一窗体拖动逻辑
        /// </summary>
        private static void StartFormDrag(Form form)
        {
            Win32ApiHelper.ReleaseCapture();
            Win32ApiHelper.SendMessage(
                form.Handle,
                Win32ApiHelper.WM_NCLBUTTONDOWN,
                Win32ApiHelper.HT_CAPTION,
                0
            );
        }

        /// <summary>
        /// 统一保存并关闭逻辑
        /// </summary>
        private static void SaveAndClose(Form form, Bitmap screenshot)
        {
            try
            {
                SaveToDefaultPath(screenshot);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"自动保存失败: {ex.Message}");
            }
            form.Close();
        }

        /// <summary>
        /// 保存到默认路径
        /// </summary>
        private static void SaveToDefaultPath(Bitmap screenshot)
        {
            const string saveDir = @"D:\kimNotes\image";
            Directory.CreateDirectory(saveDir);
            string fileName = $"{DateTime.Now:yyyyMMddHHmmssfff}.png";
            screenshot.Save(Path.Combine(saveDir, fileName), ImageFormat.Png);
        }

        /// <summary>
        /// 通过对话框保存
        /// </summary>
        private static void SaveWithDialog(Bitmap screenshot)
        {
            using (var dialog = new SaveFileDialog()) // 显式 using 块
            {
                dialog.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
                dialog.Title = "保存截图";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var format = dialog.FileName.EndsWith(".jpg") ?
                            ImageFormat.Jpeg : ImageFormat.Png;
                        screenshot.Save(dialog.FileName, format);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存失败: {ex.Message}");
                    }
                }
            } // 自动释放 dialog
        }

        /// <summary>
        /// 窗体缩放逻辑
        /// </summary>
        private static void ZoomForm(Form form, float scaleFactor)
        {
            form.SuspendLayout();
            form.Width = (int)(form.Width * scaleFactor);
            form.Height = (int)(form.Height * scaleFactor);
            form.ResumeLayout();
        }

    }

    /// <summary>
    /// 截图选区覆盖层
    /// </summary>
    internal class ScreenOverlay : Form
    {
        private Point selectionStart;
        private Rectangle selectionRect;
        private readonly Bitmap screenSnapshot;

        public Rectangle SelectedArea { get; private set; }

        public ScreenOverlay()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Cursor = Cursors.Cross;

            // 截取全屏作为背景
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            screenSnapshot = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(screenSnapshot))
            {
                g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
            }

            this.Paint += OverlayPaint;
            this.MouseDown += OverlayMouseDown;
            this.MouseMove += OverlayMouseMove;
            this.MouseUp += OverlayMouseUp;
            this.KeyPress += OverlayKeyPress;
        }

        // 添加边缘调整大小功能
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
                int borderWidth = 10; // 边缘检测宽度
                Point pos = new Point(m.LParam.ToInt32());
                pos = this.PointToClient(pos);

                if (pos.X <= borderWidth && pos.Y <= borderWidth)
                    m.Result = (IntPtr)HTTOPLEFT;
                else if (pos.X >= this.ClientSize.Width - borderWidth && pos.Y <= borderWidth)
                    m.Result = (IntPtr)HTTOPRIGHT;
                else if (pos.X <= borderWidth && pos.Y >= this.ClientSize.Height - borderWidth)
                    m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (pos.X >= this.ClientSize.Width - borderWidth && pos.Y >= this.ClientSize.Height - borderWidth)
                    m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (pos.X <= borderWidth)
                    m.Result = (IntPtr)HTLEFT;
                else if (pos.X >= this.ClientSize.Width - borderWidth)
                    m.Result = (IntPtr)HTRIGHT;
                else if (pos.Y <= borderWidth)
                    m.Result = (IntPtr)HTTOP;
                else if (pos.Y >= this.ClientSize.Height - borderWidth)
                    m.Result = (IntPtr)HTBOTTOM;
                else
                    m.Result = (IntPtr)HTCLIENT;
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