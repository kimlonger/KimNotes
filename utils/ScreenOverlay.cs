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
        public Rectangle SnapshotBounds { get; private set; }
        private bool selecting;
        public Rectangle SelectedArea { get; private set; }
        public Rectangle SelectedAreaLocal { get; private set; }

        public ScreenOverlay() : this(SystemInformation.VirtualScreen, null) { }

        // Injectable snapshot keeps geometry tests independent of the real desktop.
        internal ScreenOverlay(Rectangle bounds, Bitmap snapshot)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
            if (snapshot != null && snapshot.Size != bounds.Size) throw new ArgumentException("快照尺寸与屏幕边界不一致。");
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SnapshotBounds = bounds;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            screenSnapshot = snapshot ?? new Bitmap(bounds.Width, bounds.Height);
            try
            {
                if (snapshot == null)
                    using (var graphics = Graphics.FromImage(screenSnapshot))
                        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            catch { screenSnapshot.Dispose(); screenSnapshot = null; throw; }
            Paint += OverlayPaint;
            MouseDown += OverlayMouseDown;
            MouseMove += OverlayMouseMove;
            MouseUp += OverlayMouseUp;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape)
            {
                selecting = false;
                Capture = false;
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref message, keys);
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
            if (e.Button == MouseButtons.Right)
            {
                selecting = false;
                Capture = false;
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            selectionStart = e.Location;
            selectionRect = Rectangle.Empty;
            selecting = true;
            Capture = true;
        }

        private void UpdateSelection(Point end)
        {
            var rectangle = Rectangle.FromLTRB(Math.Min(selectionStart.X, end.X), Math.Min(selectionStart.Y, end.Y),
                Math.Max(selectionStart.X, end.X), Math.Max(selectionStart.Y, end.Y));
            selectionRect = Rectangle.Intersect(rectangle, new Rectangle(Point.Empty, SnapshotBounds.Size));
            Invalidate();
        }

        private void OverlayMouseMove(object sender, MouseEventArgs e)
        {
            if (selecting && e.Button == MouseButtons.Left) UpdateSelection(e.Location);
        }

        private void OverlayMouseUp(object sender, MouseEventArgs e)
        {
            if (!selecting || e.Button != MouseButtons.Left) return;
            UpdateSelection(e.Location);
            selecting = false;
            Capture = false;
            if (selectionRect.Width <= 0 || selectionRect.Height <= 0) return;
            SelectedAreaLocal = selectionRect;
            SelectedArea = new Rectangle(SnapshotBounds.X + selectionRect.X, SnapshotBounds.Y + selectionRect.Y,
                selectionRect.Width, selectionRect.Height);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OverlayPaint(object sender, PaintEventArgs e)
        {
            if (screenSnapshot == null) return;

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
                using (SolidBrush dimBrush = new SolidBrush(Color.FromArgb(128, Color.Black)))
                {
                    region.Exclude(selectionRect);
                    e.Graphics.FillRegion(dimBrush, region);
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
    }
}
