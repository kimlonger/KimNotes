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
        private Rectangle dragOriginal;
        private int dragMode;
        private ScreenshotToolbarPanel selectionTools;
        internal string SelectedAction { get; private set; } = "toolbar";
        internal Action<string> ActionRequested { get; set; }
        internal ScreenshotToolbarPanel SelectionTools => selectionTools;
        internal bool IsEditingSelection { get; set; }
        private const int LeftEdge = 1, RightEdge = 2, TopEdge = 4, BottomEdge = 8, MoveArea = 16;
        private int Grip => Math.Max(5, (int)Math.Round(UiDpi.FactorFor(IsHandleCreated ? Handle : IntPtr.Zero) * 6));
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
            MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left && selectionRect.Contains(e.Location)) ConfirmSelection(); };
            BuildSelectionTools();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Enter) { ConfirmSelection(); return true; }
            if (keys == Keys.Escape)
            {
                if (ActionRequested != null) { ActionRequested("close"); return true; }
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
                if (ActionRequested != null) { ActionRequested("close"); return; }
                selecting = false;
                Capture = false;
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (IsEditingSelection) return;
            if (e.Clicks > 1 && selectionRect.Contains(e.Location)) { ConfirmSelection(); return; }
            selectionStart = e.Location;
            dragOriginal = selectionRect;
            dragMode = HitSelection(e.Location);
            if (dragMode == 0) selectionRect = Rectangle.Empty;
            selecting = true;
            selectionTools.Visible = false;
            Capture = true;
            Invalidate();
        }

        private void UpdateSelection(Point end)
        {
            var previous = selectionRect;
            if (dragMode == MoveArea)
            {
                int x = Math.Max(0, Math.Min(SnapshotBounds.Width - dragOriginal.Width, dragOriginal.X + end.X - selectionStart.X));
                int y = Math.Max(0, Math.Min(SnapshotBounds.Height - dragOriginal.Height, dragOriginal.Y + end.Y - selectionStart.Y));
                selectionRect = new Rectangle(x, y, dragOriginal.Width, dragOriginal.Height);
            }
            else if (dragMode != 0)
            {
                int left = dragOriginal.Left, right = dragOriginal.Right, top = dragOriginal.Top, bottom = dragOriginal.Bottom;
                if ((dragMode & LeftEdge) != 0) left = Math.Max(0, Math.Min(right - 1, end.X));
                if ((dragMode & RightEdge) != 0) right = Math.Min(SnapshotBounds.Width, Math.Max(left + 1, end.X));
                if ((dragMode & TopEdge) != 0) top = Math.Max(0, Math.Min(bottom - 1, end.Y));
                if ((dragMode & BottomEdge) != 0) bottom = Math.Min(SnapshotBounds.Height, Math.Max(top + 1, end.Y));
                selectionRect = Rectangle.FromLTRB(left, top, right, bottom);
            }
            else
            {
                var rectangle = Rectangle.FromLTRB(Math.Min(selectionStart.X, end.X), Math.Min(selectionStart.Y, end.Y),
                    Math.Max(selectionStart.X, end.X), Math.Max(selectionStart.Y, end.Y));
                selectionRect = Rectangle.Intersect(rectangle, new Rectangle(Point.Empty, SnapshotBounds.Size));
            }
            SelectedAreaLocal = selectionRect;
            SelectedArea = new Rectangle(SnapshotBounds.X + selectionRect.X, SnapshotBounds.Y + selectionRect.Y, selectionRect.Width, selectionRect.Height);
            // Repaint just the affected pixels instead of copying the full virtual desktop per move.
            var dirty = previous.Width > 0 && previous.Height > 0 ? Rectangle.Union(previous, selectionRect) : selectionRect;
            dirty.Inflate(Grip + Font.Height + 10, Grip + Font.Height + 10);
            Invalidate(dirty);
        }

        private void OverlayMouseMove(object sender, MouseEventArgs e)
        {
            if (selecting && e.Button == MouseButtons.Left) UpdateSelection(e.Location);
            else
            {
                int hit = HitSelection(e.Location);
                Cursor = hit == MoveArea ? Cursors.SizeAll : hit == (LeftEdge | TopEdge) || hit == (RightEdge | BottomEdge) ? Cursors.SizeNWSE :
                    hit == (RightEdge | TopEdge) || hit == (LeftEdge | BottomEdge) ? Cursors.SizeNESW :
                    (hit & (LeftEdge | RightEdge)) != 0 ? Cursors.SizeWE : (hit & (TopEdge | BottomEdge)) != 0 ? Cursors.SizeNS : Cursors.Cross;
            }
        }

        private void OverlayMouseUp(object sender, MouseEventArgs e)
        {
            if (!selecting || e.Button != MouseButtons.Left) return;
            UpdateSelection(e.Location);
            selecting = false;
            Capture = false;
            if (selectionRect.Width <= 0 || selectionRect.Height <= 0) return;
            ShowSelectionTools();
        }

        private void ConfirmSelection()
        {
            if (IsDisposed || selectionRect.Width <= 0 || selectionRect.Height <= 0) return;
            selecting = false;
            Capture = false;
            SelectedAreaLocal = selectionRect;
            SelectedArea = new Rectangle(SnapshotBounds.X + selectionRect.X, SnapshotBounds.Y + selectionRect.Y,
                selectionRect.Width, selectionRect.Height);
            if (ActionRequested != null) { ActionRequested("toolbar"); return; }
            FinishCapture(true);
        }

        internal void FinishCapture(bool accepted)
        {
            if (IsDisposed || Disposing) return;
            selecting = false;
            Capture = false;
            DialogResult = accepted ? DialogResult.OK : DialogResult.Cancel;
            Close();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!selecting || Capture || IsDisposed || Disposing) return;
            selecting = false;
            if (selectionRect.Width > 0 && selectionRect.Height > 0) ShowSelectionTools();
        }

        private int HitSelection(Point point)
        {
            if (selectionRect.Width <= 0 || selectionRect.Height <= 0) return 0;
            var hit = selectionRect; hit.Inflate(Grip, Grip);
            if (!hit.Contains(point)) return 0;
            int edge = 0;
            if (Math.Abs(point.X - selectionRect.Left) <= Grip) edge |= LeftEdge;
            else if (Math.Abs(point.X - selectionRect.Right) <= Grip) edge |= RightEdge;
            if (Math.Abs(point.Y - selectionRect.Top) <= Grip) edge |= TopEdge;
            else if (Math.Abs(point.Y - selectionRect.Bottom) <= Grip) edge |= BottomEdge;
            return edge != 0 ? edge : selectionRect.Contains(point) ? MoveArea : 0;
        }

        private void BuildSelectionTools()
        {
            selectionTools = new ScreenshotToolbarPanel(action =>
            {
                if (IsDisposed || Disposing) return;
                if (ActionRequested != null) { SelectedAction = action; ActionRequested(action); return; }
                if (action == "close") { selecting = false; Capture = false; DialogResult = DialogResult.Cancel; Close(); return; }
                SelectedAction = action;
                ConfirmSelection();
            }) { Visible = false };
            Controls.Add(selectionTools);
        }

        private void ShowSelectionTools()
        {
            var wa = Screen.FromPoint(new Point(SnapshotBounds.Left + selectionRect.Right - 1, SnapshotBounds.Top + selectionRect.Bottom - 1)).WorkingArea;
            wa.Offset(-SnapshotBounds.Left, -SnapshotBounds.Top);
            wa = Rectangle.Intersect(wa, ClientRectangle);
            if (wa.Width <= 0 || wa.Height <= 0) wa = ClientRectangle;
            var screen = Screen.FromPoint(new Point(SnapshotBounds.Left + selectionRect.Right - 1, SnapshotBounds.Top + selectionRect.Bottom - 1));
            float scale = UiDpi.FactorForScreen(screen);
            selectionTools.Arrange(scale, wa.Width - 8);
            int gap = Grip + 3;
            selectionTools.Location = ScreenshotToolbarPanel.Place(selectionRect, selectionTools.Size, wa, gap);
            selectionTools.Visible = true;
            selectionTools.BringToFront();
            Invalidate();
        }

        private void OverlayPaint(object sender, PaintEventArgs e)
        {
            if (screenSnapshot == null) return;

            var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, screenSnapshot.Size));
            if (clip.Width > 0 && clip.Height > 0) e.Graphics.DrawImage(screenSnapshot, clip, clip, GraphicsUnit.Pixel);

            // Start with a uniform dimmed desktop. Partial paints during the first
            // drag must not be the first time a screen region receives its mask.
            using (Region region = new Region(this.ClientRectangle))
            using (SolidBrush dimBrush = new SolidBrush(Color.FromArgb(128, Color.Black)))
            {
                if (!selectionRect.IsEmpty) region.Exclude(selectionRect);
                e.Graphics.FillRegion(dimBrush, region);
            }
            if (!selectionRect.IsEmpty)
            {
                using (Pen pen = new Pen(Color.FromArgb(25, 186, 128), 2))
                {
                    e.Graphics.DrawRectangle(pen, selectionRect);
                    int size = Math.Max(5, Grip);
                    foreach (int x in new[] { selectionRect.Left, selectionRect.Left + selectionRect.Width / 2, selectionRect.Right })
                    foreach (int y in new[] { selectionRect.Top, selectionRect.Top + selectionRect.Height / 2, selectionRect.Bottom })
                    {
                        if (x == selectionRect.Left + selectionRect.Width / 2 && y == selectionRect.Top + selectionRect.Height / 2) continue;
                        var handle = new Rectangle(x - size / 2, y - size / 2, size, size);
                        e.Graphics.FillRectangle(Brushes.White, handle);
                        e.Graphics.DrawRectangle(pen, handle);
                    }
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
                var size = TextRenderer.MeasureText(info, Font);
                var box = new Rectangle(Math.Max(0, Math.Min(selectionRect.X, Width - size.Width - 8)),
                    Math.Max(0, selectionRect.Y - size.Height - 5), size.Width + 8, size.Height + 2);
                using (var brush = new SolidBrush(Color.FromArgb(210, 38, 43, 49))) e.Graphics.FillRectangle(brush, box);
                TextRenderer.DrawText(e.Graphics, info, Font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
