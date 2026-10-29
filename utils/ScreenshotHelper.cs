using System;
using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.IO;
using KimNotes.utils;
using System.ComponentModel;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Collections.Generic;

namespace KimNotes
{
    public static partial class ScreenshotHelper
    {
        private const int PasteBorder = 1;
        private static int captureInProgress;
        private static readonly Color PasteBorderColor = Color.FromArgb(90, 90, 90);

        private sealed class Annotation : IDisposable
        {
            private readonly Action<Graphics> draw;
            public IDisposable Resource { get; private set; }

            public Annotation(Action<Graphics> draw, IDisposable resource = null)
            {
                this.draw = draw;
                Resource = resource;
            }

            public void Draw(Graphics graphics) => draw(graphics);

            public void Dispose()
            {
                Resource?.Dispose();
                Resource = null;
            }
        }

        private sealed class ScreenshotState : IDisposable
        {
            public Action CleanupAnnotation { get; set; }
            public string ImagePath { get; set; }
            public bool Incognito { get; set; }
            public Size OriginalSize { get; set; }
            public string HistoryId { get; set; }
            public bool SuppressHistory { get; set; }
            public bool AutoMenu { get; set; } = true;
            public Action FinishAnnotation { get; set; }
            public Action<string> SelectAnnotationTool { get; set; }
            public ScreenshotToolbarForm Toolbar { get; set; }
            public ScreenshotToolbarPanel InlineToolbar { get; set; }
            public Action CompleteCapture { get; set; }
            public string InitialAction { get; set; }
            public bool HistoryDirty { get; set; } = true;
            public float Scale { get; set; } = 1.0f;
            public float MinScale { get; set; } = 0.1f;
            public float MaxScale { get; set; } = 3.0f;
            public bool IsColorPicking { get; set; }
            public bool IsAnnotating { get; set; }
            public bool ApplyingScale { get; set; }
            public ToolTip PickerToolTip { get; } = new ToolTip();

            public ScreenshotState(Size originalSize)
            {
                OriginalSize = originalSize;
            }

            public void Dispose()
            {
                CleanupAnnotation?.Invoke();
                CleanupAnnotation = null;
                Toolbar?.Dispose();
                Toolbar = null;
                InlineToolbar = null;
                CompleteCapture = null;
                PickerToolTip.Dispose();
            }
        }

        private sealed class ScreenshotForm : Form
        {
            internal ScreenshotForm()
            {
                DoubleBuffered = true;
                BackColor = Color.White;
                SetStyle(ControlStyles.ResizeRedraw, true);
            }
            protected override void OnDpiChanged(DpiChangedEventArgs e)
            {
                // A pinned screenshot is a pixel canvas, not a dialog in logical
                // units. Let tool chrome change DPI without stretching the image.
                e.Cancel = true;
                base.OnDpiChanged(e);
                if (Tag is ScreenshotState state && !state.IsAnnotating)
                {
                    var picture = GetPictureBox(this);
                    if (picture != null) ApplyScale(this, picture, state);
                }
                (Tag as ScreenshotState)?.Toolbar?.Follow(this);
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x02e4 && m.LParam != IntPtr.Zero) // WM_GETDPISCALEDSIZE
                {
                    System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 0, Width);
                    System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 4, Height);
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var picture = Controls.OfType<ScaledPictureBox>().FirstOrDefault();
                if (picture?.Image == null) { base.OnPaintBackground(e); return; }
                // Match the frozen image even before child controls paint, avoiding
                // a white frame when the first editing surface becomes visible.
                e.Graphics.Clear(BackColor);
                DrawScreenshotImage(e.Graphics, picture.Image, picture.Bounds);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    (Tag as ScreenshotState)?.Dispose();
                    Tag = null;
                    ContextMenuStrip?.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class ScaledPictureBox : PictureBox
        {
            private float _scale = 1.0f;

            internal ScaledPictureBox()
            {
                SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }

            protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
            {
                // WinForms may scale child bounds again while changing monitor.
                // Only source pixels and the explicit wheel zoom define this size.
                if (Image != null)
                {
                    width = Math.Max(1, (int)Math.Round(Image.Width * _scale));
                    height = Math.Max(1, (int)Math.Round(Image.Height * _scale));
                }
                base.SetBoundsCore(x, y, width, height, specified);
            }

            public float ScaleFactor => _scale;

            public void SetScale(float scale)
            {
                _scale = Math.Max(0.01f, scale);
                UpdateScaledSize();
                Invalidate();
            }

            public void UpdateScaledSize()
            {
                if (Image == null) return;
                Size = new Size(
                    Math.Max(1, (int)Math.Round(Image.Width * _scale)),
                    Math.Max(1, (int)Math.Round(Image.Height * _scale)));
            }

            private float DpiScale => UiDpi.FactorFor(Handle);

            protected override void Dispose(bool disposing)
            {
                if (disposing) { var image = Image; Image = null; image?.Dispose(); }
                base.Dispose(disposing);
            }

            // 标注时底边接着工具栏条，这条边线由工具栏条继续画，画两遍就成双线
            public bool SuppressBottomBorder;

            protected override void OnPaint(PaintEventArgs pe)
            {
                if (Image == null)
                {
                    base.OnPaint(pe);
                    return;
                }

                DrawScreenshotImage(pe.Graphics, Image, ClientRectangle);

                DrawScreenshotBorder(pe.Graphics, Width, Height, DpiScale, SuppressBottomBorder);
            }
        }

        private static void DrawScreenshotImage(Graphics graphics, Image image, Rectangle bounds)
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            graphics.InterpolationMode = bounds.Width >= image.Width && bounds.Height >= image.Height
                ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            if (bounds.Size == image.Size)
            {
                graphics.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                return;
            }
            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                graphics.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        /// <summary>
        /// 截图边界描边：无边框窗体全靠这条线区分截图与桌面，
        /// 标注层盖住图片重绘时也要补画一遍，否则用户看不出边界在哪。
        /// skipBottom 用于标注态：底边接着工具栏条，那条边线交给工具栏条画，免得画两遍成双线。
        /// </summary>
        private static void DrawScreenshotBorder(Graphics g, int w, int h, float s, bool skipBottom = false)
        {
            int border = Math.Max(1, (int)Math.Round(PasteBorder * s));
            using (var brush = new SolidBrush(PasteBorderColor))
            {
                g.FillRectangle(brush, 0, 0, w, border); // top
                g.FillRectangle(brush, 0, 0, border, h); // left
                g.FillRectangle(brush, w - border, 0, border, h); // right
                if (!skipBottom)
                    g.FillRectangle(brush, 0, h - border, w, border); // bottom
            }
        }

        /// <summary>
        /// 标注工具栏容器：截图窗内的一个子面板，贴在图片下沿、与窗同宽。
        /// 外圈只描左/右/下（上边开口，由图片自身的左右边线接过来，一圈框到底）；
        /// 顶边另画一条浅色分隔线，把图片和按钮区分开但不打断外框。
        /// </summary>
        private sealed class ToolbarStripPanel : Panel
        {
            private static readonly Color DividerColor = Color.FromArgb(227, 232, 239);

            public int FramePen { get; set; }

            public ToolbarStripPanel()
            {
                BackColor = Color.White;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (FramePen <= 0) return;
                var r = ClientRectangle;
                using (var brush = new SolidBrush(PasteBorderColor))
                {
                    e.Graphics.FillRectangle(brush, r.Left, r.Top, FramePen, r.Height); // left
                    e.Graphics.FillRectangle(brush, r.Right - FramePen, r.Top, FramePen, r.Height); // right
                    e.Graphics.FillRectangle(brush, r.Left, r.Bottom - FramePen, r.Width, FramePen); // bottom
                }
                // 图片与按钮之间的浅分隔线，两端落在外框内侧
                using (var brush = new SolidBrush(DividerColor))
                    e.Graphics.FillRectangle(brush, r.Left + FramePen, r.Top, r.Width - FramePen * 2, FramePen);
            }
        }

        /// <summary>
        /// 启动交互式截图并返回截图窗体
        /// </summary>
        public static Form CaptureInteractive(string path, bool flag)
        {
            if (System.Threading.Interlocked.Exchange(ref captureInProgress, 1) != 0) return null;
            try
            {
            using (var overlay = new ScreenOverlay()) return RunCaptureSession(overlay, path, flag);
            }
            finally { System.Threading.Interlocked.Exchange(ref captureInProgress, 0); }
        }

        private static Form RunCaptureSession(ScreenOverlay overlay, string path, bool incognito)
        {
            Form viewer = null;
            bool completed = false;
            bool closingOverlay = false;
            overlay.ActionRequested = action =>
            {
                if (completed || closingOverlay || overlay.IsDisposed || overlay.Disposing) return;
                if (action == "close" && viewer == null) { overlay.FinishCapture(false); return; }
                if (overlay.SelectedAreaLocal.Width <= 0 || overlay.SelectedAreaLocal.Height <= 0) return;
                try
                {
                    if (viewer == null)
                    {
                        var area = overlay.SelectedArea;
                        viewer = CreateScreenshotForm(CropFromSnapshot(overlay.ScreenSnapshot, overlay.SelectedAreaLocal), area);
                        var state = (ScreenshotState)viewer.Tag;
                        state.ImagePath = path; state.Incognito = incognito; state.AutoMenu = false;
                        state.InlineToolbar = overlay.SelectionTools;
                        state.CompleteCapture = () =>
                        {
                            if (completed) return;
                            completed = true;
                            viewer.Owner = null;
                            overlay.ActionRequested = null;
                            state.InlineToolbar = null; state.CompleteCapture = null;
                            var region = viewer.Region; viewer.Region = null; region?.Dispose();
                            overlay.FinishCapture(true);
                        };
                        viewer.FormClosed += (s, e) => { if (!completed && !closingOverlay && !overlay.IsDisposed) { closingOverlay = true; overlay.FinishCapture(false); } };
                        viewer.Location = area.Location;
                        overlay.IsEditingSelection = true;
                        // Use managed ownership only. Show(IWin32Window) also caches
                        // a dialog-owner reference which survives Owner = null.
                        viewer.Owner = overlay;
                        viewer.Show();
                        // A toolbar can lie inside a full-screen selection; leave its
                        // original overlay control visible and clickable through the viewer.
                        var toolbarRect = viewer.RectangleToClient(overlay.RectangleToScreen(overlay.SelectionTools.Bounds));
                        if (viewer.ClientRectangle.IntersectsWith(toolbarRect))
                        {
                            var region = new Region(viewer.ClientRectangle); region.Exclude(toolbarRect); viewer.Region = region;
                        }
                    }
                    var current = (ScreenshotState)viewer.Tag;
                    if (action == "toolbar")
                    {
                        current.CompleteCapture?.Invoke();
                        ShowScreenshotToolbar(viewer);
                    }
                    else RunScreenshotAction(viewer, action);
                }
                catch (Exception e)
                {
                    Program.LogError(e);
                    if (!overlay.IsDisposed && !overlay.Disposing)
                        MessageBox.Show(overlay, e.Message, "截图操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            overlay.FormClosing += (s, e) =>
            {
                if (!completed && !closingOverlay && viewer != null && !viewer.IsDisposed)
                {
                    closingOverlay = true;
                    viewer.Close();
                    if (!viewer.IsDisposed) { e.Cancel = true; closingOverlay = false; }
                }
            };
            try
            {
                var result = overlay.ShowDialog();
                return result == DialogResult.OK && viewer != null && !viewer.IsDisposed ? viewer : null;
            }
            finally { overlay.ActionRequested = null; }
        }

        /// <summary>
        /// 从全屏快照中裁剪指定区域（考虑DPI缩放）
        /// </summary>
        private static Bitmap CropFromSnapshot(Bitmap fullscreenSnapshot, Rectangle localArea)
        {
            int x = localArea.X;
            int y = localArea.Y;
            int width = localArea.Width;
            int height = localArea.Height;

            // 确保裁剪区域在图像范围内
            Rectangle bounds = new Rectangle(0, 0, fullscreenSnapshot.Width, fullscreenSnapshot.Height);
            Rectangle crop = Rectangle.Intersect(new Rectangle(x, y, width, height), bounds);
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(localArea), "截图区域不在屏幕快照内。");
            }
            return fullscreenSnapshot.Clone(crop, PixelFormat.Format32bppArgb);
        }

        /// <summary>
        /// 创建显示截图的悬浮窗
        /// </summary>
        private static Form CreateScreenshotForm(Bitmap screenshot, Rectangle area)
        {
            var form = BuildBaseForm(area);
            // 确保窗体显示在所选区域的屏幕上
            var screen = Screen.FromRectangle(area);
            form.Location = new Point(
                area.X - screen.Bounds.X + screen.Bounds.Left,
                area.Y - screen.Bounds.Y + screen.Bounds.Top
            );
            var contextMenu = BuildContextMenu(form);
            var pb = BuildPictureBox(screenshot, contextMenu);
            form.Controls.Add(pb);
            form.ContextMenuStrip = contextMenu;

            var state = new ScreenshotState(screenshot.Size);
            form.Tag = state;
            ApplyScale(form, pb, state);
            ClampToScreen(form);

            BindEvents(form, pb);
            form.Activated += (s, e) =>
            {
                if (state.SuppressHistory) return;
                recentViewer.SetTarget(form);
                recentHistoryId = state.HistoryId;
            };
            form.Shown += (s, e) => RememberScreenshot(form);
            form.FormClosing += (s, e) =>
            {
                state.FinishAnnotation?.Invoke();
                if (!RememberScreenshot(form))
                {
                    e.Cancel = MessageBox.Show(form, "贴图记录暂时无法保存。仍要关闭吗？关闭后无法唤回尚未保存的图片或修改。", "贴图尚未保存",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes;
                }
            };
            form.Shown += (sender, args) => form.BeginInvoke((Action)(() =>
            {
                if (form.IsDisposed || !form.Visible || !state.AutoMenu || state.IsAnnotating) return;
                ShowScreenshotToolbar(form);
                var action = state.InitialAction;
                state.InitialAction = null;
                if (action != null && action != "toolbar") RunScreenshotAction(form, action);
            }));
            form.LocationChanged += (s, e) => state.Toolbar?.Follow(form);
            form.SizeChanged += (s, e) => state.Toolbar?.Follow(form);
            return form;
        }

        /// <summary>
        /// 构建基础窗体结构
        /// </summary>
        private static Form BuildBaseForm(Rectangle area)
        {
            return new ScreenshotForm
            {
                AutoScaleMode = AutoScaleMode.None,
                FormBorderStyle = FormBorderStyle.None,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = area.Location,
                ClientSize = new Size(area.Width, area.Height),
                Padding = Padding.Empty,
                KeyPreview = true
            };
        }

        /// <summary>
        /// 构建右键菜单（动态获取当前图像）
        /// </summary>
        private static ContextMenuStrip BuildContextMenu(Form hostForm)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("显示工具栏", null, (s, e) => ShowScreenshotToolbar(hostForm)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("插入便签", null, (s, e) =>
            {
                var image = GetCurrentImage(hostForm);
                if (image == null) return;
                InsertIntoLatestNote(image);
                hostForm.Close();
            }));
            menu.Items.Add(new ToolStripMenuItem("复制", null, (s, e) =>
                Clipboard.SetImage(GetCurrentImage(hostForm))));
            menu.Items.Add(new ToolStripMenuItem("标注", null, (s, e) =>
                AnnotateImages(GetCurrentImage(hostForm), hostForm)));
            menu.Items.Add(new ToolStripMenuItem("OCR", null, (s, e) =>
                ScanOCR(GetCurrentImage(hostForm))));
            menu.Items.Add(new ToolStripMenuItem("另存", null, (s, e) =>
                SaveWithDialog(GetCurrentImage(hostForm))));

            var pickColorItem = new ToolStripMenuItem("拾色", null, (s, e) =>
                StartColorPick(hostForm));
            var resetZoomItem = new ToolStripMenuItem("重置 100%", null, (s, e) =>
                ResetZoom(hostForm));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(pickColorItem);
            menu.Items.Add(resetZoomItem);

            menu.Items.Add(new ToolStripSeparator());
            var previous = new ToolStripMenuItem("上一张", null, (s, e) => NavigateWindow(hostForm, 1)) { ShortcutKeyDisplayString = "←" };
            var next = new ToolStripMenuItem("下一张", null, (s, e) => NavigateWindow(hostForm, -1)) { ShortcutKeyDisplayString = "→" };
            menu.Items.Add(previous); menu.Items.Add(next);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("关闭贴图", null, (s, e) => hostForm.Close())
                { ToolTipText = "关闭当前浮窗，保留历史，可再次唤回" });
            menu.Items.Add(new ToolStripMenuItem("销毁贴图", null, (s, e) => DestroyScreenshot(hostForm))
                { ToolTipText = "删除当前贴图及其历史缓存，无法再次唤回；不删除另存的图片" });
            // 新增标注模式判断
            menu.Opening += (s, e) =>
            {
                var pb = GetPictureBox(hostForm);
                e.Cancel = pb?.ContextMenuStrip == null; // 当标注模式时禁用菜单
                var state = hostForm.Tag as ScreenshotState;
                var entries = ScreenshotArchive.List(state?.Incognito ?? Storage.Incognito);
                int index = Array.IndexOf(entries, state?.HistoryId);
                previous.Enabled = state?.SuppressHistory == false && index >= 0 && index < entries.Length - 1;
                next.Enabled = state?.SuppressHistory == false && index > 0;
            };
            return menu;
        }

        internal static note InsertIntoLatestNote(Image image)
        {
            // Resolve when the menu is clicked: screenshots may stay open while notes are opened or closed.
            var destination = Application.OpenForms.OfType<note>()
                .Where(n => !n.IsDisposed && n.Visible)
                .OrderByDescending(n => n.OpenedOrder)
                .FirstOrDefault() ?? Program.AppContext.AddNewForm();
            destination.WindowState = FormWindowState.Normal;
            destination.Activate();
            destination.AppendImage(image);
            return destination;
        }

        private static Bitmap GetCurrentImage(Form hostForm)
        {
            var pb = GetPictureBox(hostForm);
            return pb?.Image as Bitmap;
        }

        private static ScaledPictureBox GetPictureBox(Form form)
        {
            return form.Controls.OfType<ScaledPictureBox>().FirstOrDefault();
        }

        private static void AnnotateImages(Bitmap screenshot, Form hostForm)
        {

            var pb = GetPictureBox(hostForm);
            var state = hostForm.Tag as ScreenshotState;
            if (pb == null || state == null || state.IsAnnotating) return;

            float originalScale = state.Scale;
            Point originalFormLocation = hostForm.Location;
            state.IsAnnotating = true;
            state.IsColorPicking = false;
            pb.Cursor = Cursors.Default;
            Bitmap originalImage = new Bitmap(screenshot);
            // 按添加顺序绘制标注（原 Stack 遍历是反序，后画的会垫底）
            var annotations = new List<Annotation>();
            var currentMode = AnnotationMode.None;
            Point? rectStart = null;
            Rectangle currentRect = Rectangle.Empty;
            Bitmap mosaicPreviewSource = null;
            TransparentInputBox activeInputBox = null;

            // 工具栏：白底圆角卡片 + 线稿矢量图标（与便签工具栏同一血统），尺寸全按窗口 DPI 量算
            // 截图窗可能落在另一块缩放的屏幕上，按窗口自己的 DPI 量
            float S = UiDpi.FactorFor(hostForm.Handle);
            var toolbarWindow = state.Toolbar;
            if (state.InlineToolbar == null && (toolbarWindow == null || toolbarWindow.IsDisposed))
            {
                toolbarWindow = new ScreenshotToolbarForm(action => RunScreenshotAction(hostForm, action));
                state.Toolbar = toolbarWindow;
            }
            var toolsPanel = state.InlineToolbar ?? toolbarWindow.Tools;
            var tools = toolsPanel.Buttons;
            var buttons = new[] { tools["rect"], tools["arrow"], tools["mosaic"], tools["text"], tools["undo"], tools["check"] };
            toolsPanel.SetHint("check", "保存标注、复制并贴图");

            var originalMenu = pb.ContextMenuStrip;
            pb.ContextMenuStrip = null;

            var workingArea = Screen.FromControl(hostForm).WorkingArea;
            if (state.InlineToolbar == null) toolsPanel.Arrange(S, workingArea.Width - 8);
            int stripHeight = toolsPanel.Height + (int)(8 * S);
            state.Scale = state.InlineToolbar != null ? originalScale : Math.Max(0.01f, Math.Min(originalScale, Math.Min((workingArea.Width - 16f) / originalImage.Width,
                (workingArea.Height - stripHeight - 16f) / originalImage.Height)));
            pb.SetScale(state.Scale);
            float annotationScale = pb.ScaleFactor;
            int imageW = pb.Width, imageH = pb.Height;

            Color originalBackColor = hostForm.BackColor;

            hostForm.SuspendLayout();
            hostForm.BackColor = Color.White;
            pb.Location = Point.Empty;
            pb.SuppressBottomBorder = false;
            hostForm.ClientSize = new Size(imageW, imageH);
            hostForm.ResumeLayout();
            pb.Invalidate();
            if (state.InlineToolbar == null) ClampToScreen(hostForm);

            // 底部放不下工具栏就把整窗上提，保证按钮都在屏内
            {
                var wa = Screen.FromControl(hostForm).WorkingArea;
                int overflow = hostForm.Bottom - wa.Bottom;
                if (overflow > 0 && state.InlineToolbar == null) hostForm.Top = Math.Max(wa.Top, hostForm.Top - overflow);
            }

            // 标注层设置（保持原始代码逻辑）
            var annotationLayer = new PictureBox
            {
                Size = pb.Size,
                Location = pb.Location,
                BackColor = Color.Transparent,
                Dock = DockStyle.None
            };

            // 笔画粗细按屏幕缩放补偿：截图位图 1px == 屏上 1px，但内容在高分屏下本身大一圈
            Pen redPen = new Pen(Color.Red, 2 * S / annotationScale);

            // 四个绘图工具互斥高亮，选中态就是当前画笔
            var drawTools = new[] { buttons[0], buttons[1], buttons[2], buttons[3] };
            Action<int, AnnotationMode> PickTool = (idx, mode) =>
            {
                if (activeInputBox != null) SaveAnnotation(activeInputBox);
                rectStart = null;
                currentRect = Rectangle.Empty;
                mosaicPreviewSource?.Dispose();
                mosaicPreviewSource = null;
                annotationLayer.Capture = false;
                currentMode = mode;
                for (int i = 0; i < drawTools.Length; i++)
                    drawTools[i].Selected = i == idx;
                pb.ContextMenuStrip = null;   // 标注期间右键不是菜单
            };

            Action undoLast = () =>
            {
                if (rectStart.HasValue)
                {
                    rectStart = null;
                    currentRect = Rectangle.Empty;
                    annotationLayer.Capture = false;
                    mosaicPreviewSource?.Dispose();
                    mosaicPreviewSource = null;
                    annotationLayer.Invalidate();
                    return;
                }
                if (activeInputBox != null) { var input = activeInputBox; activeInputBox = null; input.Dispose(); return; }
                if (annotations.Count == 0) return;
                annotations[annotations.Count - 1].Dispose();
                annotations.RemoveAt(annotations.Count - 1);
                annotationLayer.Invalidate();
            };
            state.SelectAnnotationTool = id =>
            {
                if (id == "undo") { undoLast(); return; }
                int index = Array.IndexOf(new[] { "rect", "arrow", "mosaic", "text" }, id);
                if (index >= 0) PickTool(index, new[] { AnnotationMode.Rectangle, AnnotationMode.Arrow, AnnotationMode.Mosaic, AnnotationMode.Text }[index]);
            };
            buttons[4].Enabled = true;

            KeyEventHandler annotationKeys = null;
            bool cleaned = false;
            Action cleanup = () =>
            {
                if (cleaned) return;
                cleaned = true;
                hostForm.KeyDown -= annotationKeys;
                var input = activeInputBox;
                activeInputBox = null;
                input?.Dispose();
                annotationLayer.Dispose();
                foreach (var button in drawTools) button.Selected = false;
                buttons[4].Enabled = false;
                toolsPanel.SetHint("check", "复制并贴图");
                redPen.Dispose();
                originalImage.Dispose();
                mosaicPreviewSource?.Dispose();
                mosaicPreviewSource = null;
                foreach (var annotation in annotations) annotation.Dispose();
                annotations.Clear();
                state.CleanupAnnotation = null;
                state.FinishAnnotation = null;
                state.SelectAnnotationTool = null;
                state.IsAnnotating = false;
            };
            state.CleanupAnnotation = cleanup;

            Action finishAnnotation = () =>
            {
                if (cleaned) return;

                // 先提交未确认的文本框
                if (activeInputBox != null)
                {
                    SaveAnnotation(activeInputBox);
                    activeInputBox = null;
                }

                var result = new Bitmap(originalImage);
                try
                {
                  using (var g = Graphics.FromImage(result))
                  {
                    foreach (var action in annotations)
                        action.Draw(g);
                  }
                }
                catch { result.Dispose(); throw; }

                // 先移除并释放标注层，避免它在 pb.Image 更新后把图片画到自身上
                var previous = pb.Image;
                pb.Image = result;
                state.HistoryDirty = true;
                previous?.Dispose();
                pb.Invalidate();
                cleanup();
                pb.SuppressBottomBorder = false;
                hostForm.BackColor = originalBackColor;

                // 还原图片位置与窗体尺寸（ApplyScale 会把 pb 归位到左上角并按图片重设客户区）
                state.Scale = originalScale;
                if (pb.ScaleFactor != originalScale) ApplyScale(hostForm, pb, state);
                if (hostForm.Location != originalFormLocation) hostForm.Location = originalFormLocation;

                // 恢复右键菜单
                pb.ContextMenuStrip = originalMenu;
                if (state.InlineToolbar == null) ClampToScreen(hostForm);
                ShowScreenshotToolbar(hostForm);
            };

            state.FinishAnnotation = finishAnnotation;
            if (toolbarWindow != null)
            {
                toolbarWindow.Follow(hostForm);
                if (!toolbarWindow.Visible) toolbarWindow.Show(hostForm);
            }

            // 退出不能只靠工具栏最右那个对勾：窄截图时它可能点不到
            annotationKeys = (s, e) =>
            {
                if (!state.IsAnnotating) return;
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    finishAnnotation();   // Esc 第一次=结束标注并保留内容，再按才关窗
                    return;
                }
                if (e.Control && e.KeyCode == Keys.Z)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    undoLast();
                }
            };
            hostForm.KeyDown += annotationKeys;

            annotationLayer.Paint += (s, e) =>
            {
                var baseImage = originalImage;
                if (baseImage == null) return;
                e.Graphics.DrawImage(baseImage, annotationLayer.ClientRectangle);
                var savedTransform = e.Graphics.Save();
                e.Graphics.ScaleTransform(annotationScale, annotationScale);
                foreach (var action in annotations)
                    action.Draw(e.Graphics);

                // 修改后的绘制逻辑
                if (currentMode == AnnotationMode.Rectangle && currentRect != Rectangle.Empty)
                {
                    e.Graphics.DrawRectangle(redPen, currentRect);
                }
                else if (currentMode == AnnotationMode.Arrow && currentRect != Rectangle.Empty)
                {
                    DrawArrow(e.Graphics, currentRect.Location,
                        new Point(currentRect.Right, currentRect.Bottom), S / annotationScale);
                }
                else if (currentMode == AnnotationMode.Mosaic && currentRect != Rectangle.Empty)
                {
                    // 绘制马赛克选择区域
                    e.Graphics.DrawRectangle(redPen, currentRect);

                    // 预览马赛克效果
                    Rectangle previewRect = new Rectangle(
                        Math.Min(currentRect.X, currentRect.Right),
                        Math.Min(currentRect.Y, currentRect.Bottom),
                        Math.Abs(currentRect.Width),
                        Math.Abs(currentRect.Height));

                    if (previewRect.Width > 5 && previewRect.Height > 5)
                    {
                        // 绘制半透明预览
                        using (Bitmap previewBitmap = new Bitmap(previewRect.Width, previewRect.Height))
                        {
                            using (Graphics previewG = Graphics.FromImage(previewBitmap))
                            {
                                // 复制原始区域
                                previewG.DrawImage(
                                    mosaicPreviewSource ?? baseImage,
                                    new Rectangle(0, 0, previewRect.Width, previewRect.Height),
                                    previewRect,
                                    GraphicsUnit.Pixel);
                            }

                            // 应用马赛克预览效果
                            ApplyMosaicEffect(previewBitmap, 10);

                            // 半透明绘制预览
                            e.Graphics.DrawImage(previewBitmap, previewRect);
                        }
                    }
                }

                e.Graphics.Restore(savedTransform);
                DrawScreenshotBorder(e.Graphics, annotationLayer.Width, annotationLayer.Height, S);
            };

            annotationLayer.MouseDown += (s, e) =>
            {
                // 新增：当存在激活文本框时，右键确认
                if (activeInputBox != null && e.Button == MouseButtons.Right)
                {
                    SaveAnnotation(activeInputBox);
                    activeInputBox = null;
                    return; // 阻止后续处理
                }
                if (e.Button != MouseButtons.Left) return;
                if (currentMode == AnnotationMode.Rectangle)
                {
                    rectStart = ToImagePoint(e.Location);
                }
                if (currentMode == AnnotationMode.Arrow)
                {
                    rectStart = ToImagePoint(e.Location);
                }
                if (currentMode == AnnotationMode.Mosaic)
                {
                    rectStart = ToImagePoint(e.Location);
                    mosaicPreviewSource?.Dispose();
                    mosaicPreviewSource = new Bitmap(originalImage);
                    using (var graphics = Graphics.FromImage(mosaicPreviewSource))
                        foreach (var action in annotations) action.Draw(graphics);
                }
                else if (currentMode == AnnotationMode.Text && activeInputBox == null)
                {
                    // 创建文本框时禁用右键菜单
                    activeInputBox = new TransparentInputBox
                    {
                        Location = new Point(
                        Math.Max(0, e.X - 15),
                        Math.Max(0, e.Y - 10)
                    ),
                    };
                    activeInputBox.ConfirmRequested += () =>
                    {
                        SaveAnnotation(activeInputBox);
                        activeInputBox = null;
                    };
                    activeInputBox.LostFocus += (_, __) =>
                    {
                        if (activeInputBox != null)
                        {
                            SaveAnnotation(activeInputBox);
                            activeInputBox = null;
                        }
                    };
                    activeInputBox.MouseDown += (_, me) =>
                    {
                        if (me.Button == MouseButtons.Right)
                        {
                            SaveAnnotation(activeInputBox);
                            activeInputBox = null;
                        }
                    };

                    annotationLayer.Controls.Add(activeInputBox);
                    hostForm.Activate();
                    activeInputBox.Focus();
                }
                if (rectStart.HasValue) annotationLayer.Capture = true;
            };

            Point ToImagePoint(Point point) => new Point(
                Math.Max(0, Math.Min(originalImage.Width, (int)Math.Round(point.X / annotationScale))),
                Math.Max(0, Math.Min(originalImage.Height, (int)Math.Round(point.Y / annotationScale))));

            void UpdateDrag(Point point)
            {
                if (!rectStart.HasValue) return;
                var start = rectStart.Value;
                currentRect = currentMode == AnnotationMode.Arrow
                    ? new Rectangle(start.X, start.Y, point.X - start.X, point.Y - start.Y)
                    : Rectangle.FromLTRB(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y), Math.Max(start.X, point.X), Math.Max(start.Y, point.Y));
                annotationLayer.Invalidate();
            }

            annotationLayer.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) UpdateDrag(ToImagePoint(e.Location));
            };

            annotationLayer.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || !rectStart.HasValue) return;
                var end = ToImagePoint(e.Location);
                UpdateDrag(end);
                var start = rectStart.Value;
                var finalRect = currentRect;
                rectStart = null;
                annotationLayer.Capture = false;
                if (currentMode == AnnotationMode.Rectangle && finalRect.Width > 2 && finalRect.Height > 2)
                    annotations.Add(new Annotation(g => g.DrawRectangle(redPen, finalRect)));
                else if (currentMode == AnnotationMode.Arrow && (Math.Abs(end.X - start.X) > 2 || Math.Abs(end.Y - start.Y) > 2))
                    annotations.Add(new Annotation(g => DrawArrow(g, start, end, S / annotationScale)));
                else if (currentMode == AnnotationMode.Mosaic && finalRect.Width > 5 && finalRect.Height > 5)
                {
                    // Sample a separate composite: GDI+ cannot read the bitmap currently being drawn into.
                    using (var composite = new Bitmap(originalImage))
                    {
                        using (var graphics = Graphics.FromImage(composite))
                            foreach (var action in annotations) action.Draw(graphics);
                        var patch = composite.Clone(finalRect, PixelFormat.Format32bppArgb);
                        try
                        {
                            ApplyMosaicEffect(patch, 10);
                            annotations.Add(new Annotation(g => g.DrawImageUnscaled(patch, finalRect.Location), patch));
                        }
                        catch { patch.Dispose(); throw; }
                    }
                }
                currentRect = Rectangle.Empty;
                mosaicPreviewSource?.Dispose();
                mosaicPreviewSource = null;
                annotationLayer.Invalidate();
            };
            void SaveAnnotation(TransparentInputBox inputBox)
            {
                // 防止重复保存/释放（输入框自身与标注层的右键事件会各触发一次）
                if (inputBox == null || inputBox.IsDisposed) return;

                if (ReferenceEquals(activeInputBox, inputBox)) activeInputBox = null;
                var text = inputBox.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    var pos = inputBox.Location;
                    float scale = inputBox._scale;
                    var font = new Font(inputBox.Font.FontFamily,
                        inputBox.Font.SizeInPoints * inputBox.DeviceDpi / 72f * scale / annotationScale,
                        inputBox.Font.Style, GraphicsUnit.Pixel);
                    var bounds = new RectangleF((pos.X + 2f) / annotationScale, pos.Y / annotationScale,
                        inputBox.Width / annotationScale, inputBox.Height / annotationScale);
                    var color = inputBox.ForeColor;
                    annotations.Add(new Annotation(g =>
                    {
                        using (var brush = new SolidBrush(color))
                        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                            g.DrawString(text, font, brush, bounds, format);
                    }, font));
                    annotationLayer.Invalidate();
                }
                if (!annotationLayer.IsDisposed)
                {
                    annotationLayer.Controls.Remove(inputBox);
                }
                inputBox.Dispose();
            }

            // Attach only after every drawing handler is ready, so the first frame
            // contains the image rather than a blank annotation surface.
            hostForm.Controls.Add(annotationLayer);
            annotationLayer.BringToFront();
            annotationLayer.Invalidate();

        }

        private static void DrawArrow(Graphics g, Point start, Point end, float s)
        {
            using (Pen redPen = new Pen(Color.Red, 2 * s))
            {
                // 绘制主线
                g.DrawLine(redPen, start, end);

                // 计算箭头角度
                float angle = (float)Math.Atan2(end.Y - start.Y, end.X - start.X);

                // 绘制箭头头部
                float arrowSize = 10 * s;
                PointF[] arrowPoints =
                {
            end,
            new PointF(
                end.X - arrowSize * (float)Math.Cos(angle - Math.PI/6),
                end.Y - arrowSize * (float)Math.Sin(angle - Math.PI/6)),
            new PointF(
                end.X - arrowSize * (float)Math.Cos(angle + Math.PI/6),
                end.Y - arrowSize * (float)Math.Sin(angle + Math.PI/6))
        };

                g.FillPolygon(Brushes.Red, arrowPoints);
            }
        }

        enum AnnotationMode
        {
            None,
            Rectangle,
            Text,
            Arrow, // 添加箭头模式
            Mosaic // 添加马赛克模式
        }

        /// <summary>
        /// 构建 PictureBox 控件
        /// </summary>
        private static ScaledPictureBox BuildPictureBox(Bitmap screenshot, ContextMenuStrip menu)
        {
            var pb = new ScaledPictureBox
            {
                Image = screenshot,
                BackColor = Color.White,
                SizeMode = PictureBoxSizeMode.Normal,
                Location = Point.Empty,
                Margin = Padding.Empty,
                ContextMenuStrip = menu
            };
            pb.SetScale(1.0f);
            return pb;
        }

        /// <summary>
        /// 绑定窗体事件（使用动态图像获取）
        /// </summary>
        private static void BindEvents(Form form, ScaledPictureBox pb)
        {
            var state = form.Tag as ScreenshotState;
            if (state == null) return;
            bool isSyncing = false;

            void SyncLayout()
            {
                if (isSyncing || form.IsDisposed || form.Disposing || pb.IsDisposed) return;
                isSyncing = true;
                try { ApplyScale(form, pb, state); ClampToScreen(form); }
                finally { isSyncing = false; }
            }

            pb.DoubleClick += (s, e) =>
            {
                if (state.IsAnnotating || state.IsColorPicking) return;
                if (state.CompleteCapture != null) { state.CompleteCapture(); ShowScreenshotToolbar(form); return; }
                var img = GetCurrentImage(form);
                if (state.Incognito)
                {
                    form.Close();
                }
                else
                {
                    if (img != null) SaveAndClose(form, img);
                }
            };

            pb.MouseDown += (s, e) =>
            {
                if (state.IsColorPicking)
                {
                    if (e.Button == MouseButtons.Left && e.Clicks == 1)
                    {
                        PickColorAt(form, pb, state, e.Location);
                    }
                    else if (e.Button == MouseButtons.Right)
                    {
                        state.IsColorPicking = false;
                        state.PickerToolTip.Hide(form);
                        pb.Cursor = Cursors.Default;
                    }
                    return;
                }

                if (e.Button == MouseButtons.Left && e.Clicks == 1 && state.InlineToolbar == null)
                {
                    StartFormDrag(form);
                }
            };
            pb.MouseMove += (s, e) =>
            {
                if (state.IsColorPicking)
                {
                    PreviewColorAt(form, pb, state, e.Location);
                }
            };

            void HandleZoom(MouseEventArgs e)
            {
                if (e is HandledMouseEventArgs handled)
                {
                    if (handled.Handled) return;
                    handled.Handled = true;
                }
                if (e.Delta == 0) return;
                if (state.IsAnnotating || state.InlineToolbar != null)
                {
                    return;
                }
                const float step = 0.1f;
                float minScale = state.MinScale;
                float maxScale = state.MaxScale;

                var newScale = state.Scale + (e.Delta > 0 ? step : -step);
                newScale = Math.Max(minScale, Math.Min(maxScale, newScale));
                if (Math.Abs(newScale - state.Scale) < 0.0001f) return;

                state.Scale = newScale;
                ApplyScale(form, pb, state);
                ClampToScreen(form);
            }

            pb.MouseWheel += (s, e) => HandleZoom(e);
            form.MouseWheel += (s, e) => HandleZoom(e);
            form.KeyDown += (s, e) =>
            {
                if (!state.IsAnnotating && state.InlineToolbar == null && !state.IsColorPicking && !state.SuppressHistory &&
                    (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right))
                {
                    NavigateWindow(form, e.KeyCode == Keys.Left ? 1 : -1);
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode == Keys.Escape)
                {
                    // 标注中交给标注层处理（Esc=结束标注），别把整张截图一起关掉
                    if (state.IsAnnotating) return;
                    if (state.IsColorPicking)
                    {
                        state.IsColorPicking = false;
                        state.PickerToolTip.Hide(form);
                        pb.Cursor = Cursors.Default;
                        e.Handled = e.SuppressKeyPress = true;
                        return;
                    }
                    form.Close();
                    return;
                }
            };
            form.Shown += (s, e) => SyncLayout();
            form.ClientSizeChanged += (s, e) =>
            {
                if (isSyncing || state.ApplyingScale) return;
                // 标注模式下窗体尺寸包含工具栏，不能按图片大小回缩
                if (state.IsAnnotating) return;
                if (form.ClientSize != pb.Size)
                {
                    SyncLayout();
                }
            };
            form.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && state.InlineToolbar == null)
                {
                    StartFormDrag(form);
                }
            };

        }
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

        private static void SaveAndClose(Form form, Bitmap screenshot)
        {
            try
            {
                SaveToDefaultPath(screenshot, ((ScreenshotState)form.Tag).ImagePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"自动保存失败: {ex.Message}");
                return;
            }
            form.Close();
        }

        private static void SaveToDefaultPath(Bitmap screenshot, string imagePath)
        {
            string saveDir = imagePath;
            Directory.CreateDirectory(saveDir);
            string fileName = DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".png";
            Storage.Write(Path.Combine(saveDir, fileName), temp => screenshot.Save(temp, ImageFormat.Png));
        }

        private static void SaveWithDialog(Bitmap screenshot)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
                dialog.Title = "保存截图";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        ExportImage(screenshot, dialog.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存失败: {ex.Message}");
                    }
                }
            }
        }

        internal static void ExportImage(Bitmap screenshot, string path)
        {
            string extension = Path.GetExtension(path);
            var format = string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Jpeg : ImageFormat.Png;
            Storage.Write(path, temp => screenshot.Save(temp, format));
        }

        private static void ScanOCR(Bitmap screenshot)
        {
            // OCR runs asynchronously; closing the screenshot must not dispose its input underneath it.
            var input = new Bitmap(screenshot);
            Label waitLabel = new Label
            {
                Text = "识别中，请耐心等待...",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };

            // 尺寸按文字实测量算（写死 200×100 在高缩放下会把提示文字挤掉）
            float ocrScale = UiDpi.Factor;
            var ocrTextSize = TextRenderer.MeasureText(waitLabel.Text, waitLabel.Font);
            Form waitForm = new Form
            {
                Text = "文字识别",
                AutoScaleMode = AutoScaleMode.None,
                ClientSize = new Size(
                    Math.Max(ocrTextSize.Width + (int)(48 * ocrScale), (int)(200 * ocrScale)),
                    Math.Max(ocrTextSize.Height + (int)(40 * ocrScale), (int)(70 * ocrScale))),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false
            };

            waitForm.Controls.Add(waitLabel);

            BackgroundWorker worker = new BackgroundWorker();

            worker.DoWork += (sender, e) =>
            {
                string base64String;
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    input.Save(memoryStream, ImageFormat.Png);
                    byte[] imageBytes = memoryStream.ToArray();
                    base64String = Convert.ToBase64String(imageBytes);
                }

                e.Result = RemoteCallUtils.generalBasic(base64String);
            };

            worker.RunWorkerCompleted += (sender, e) =>
            {
                waitForm.Close();
                waitForm.Dispose();
                input.Dispose();
                worker.Dispose();
                
                if (e.Error != null)
                {
                    MessageBox.Show($"识别失败: {e.Error.Message}");
                    return;
                }

                try
                {
                    string combinedText = ParseOcrResponse((string)e.Result);
                    if (string.IsNullOrWhiteSpace(combinedText))
                    {
                        MessageBox.Show("图片中未识别到文字。", "文字识别", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    
                    // 通过全局上下文创建 note 窗体，保证窗体计数与生命周期一致
                    var newNote = Program.AppContext.AddNewForm();

                    // 将识别结果插入到新窗体
                    newNote.SetText(combinedText);
                    
                    // 复制到剪贴板
                    if (!string.IsNullOrEmpty(combinedText))
                    {
                        Clipboard.SetText(combinedText);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"处理结果时出错: {ex.Message}");
                }
            };

            waitForm.Show();
            worker.RunWorkerAsync();
        }

        internal static string ParseOcrResponse(string response)
        {
            var json = JObject.Parse(response);
            if (json["error_code"] != null)
                throw new InvalidOperationException("OCR 服务返回错误：" + (string)json["error_msg"]);
            var words = json["words_result"] as JArray;
            if (words == null) throw new InvalidOperationException("OCR 服务返回的结果格式不正确。");
            return string.Join(Environment.NewLine, words.Select(item => (string)item["words"])
                .Where(word => !string.IsNullOrWhiteSpace(word)));
        }

        private static void ApplyScale(Form form, ScaledPictureBox pb, ScreenshotState state)
        {
            if (state.ApplyingScale || form.IsDisposed || pb.IsDisposed) return;
            state.ApplyingScale = true;
            form.SuspendLayout();
            try
            {
                pb.SetScale(state.Scale);
                pb.Location = Point.Empty;
                form.ClientSize = pb.Size;
            }
            finally { form.ResumeLayout(false); state.ApplyingScale = false; }
            // Full invalidation is required: resizing an HWND can preserve old
            // pixels, and fast wheel messages otherwise outrun WM_PAINT.
            if (form.Visible && form.IsHandleCreated)
                RepaintScreenshot(form);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);
        private static void RepaintScreenshot(Form form) => RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, 0x0185); // INVALIDATE | ERASE | UPDATENOW | ALLCHILDREN

        private static void ResetZoom(Form form)
        {
            var pb = GetPictureBox(form);
            var state = form.Tag as ScreenshotState;
            if (pb == null || state == null) return;

            state.Scale = 1.0f;
            ApplyScale(form, pb, state);
            ClampToScreen(form);
        }

        private static void StartColorPick(Form form)
        {
            var pb = GetPictureBox(form);
            var state = form.Tag as ScreenshotState;
            if (pb == null || state == null || state.IsAnnotating) return;

            state.IsColorPicking = true;
            pb.Cursor = Cursors.Cross;
        }

        private static void PickColorAt(Form form, ScaledPictureBox pb, ScreenshotState state, Point location)
        {
            if (!(pb.Image is Bitmap bmp)) return;

            int x = (int)Math.Floor(location.X / Math.Max(0.0001f, state.Scale));
            int y = (int)Math.Floor(location.Y / Math.Max(0.0001f, state.Scale));
            x = Math.Max(0, Math.Min(x, bmp.Width - 1));
            y = Math.Max(0, Math.Min(y, bmp.Height - 1));

            Color c = bmp.GetPixel(x, y);
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            string text = $"{hex}  RGB({c.R},{c.G},{c.B})";

            Clipboard.SetText(hex);
            state.PickerToolTip.Show(
                text,
                form,
                form.PointToClient(Cursor.Position),
                1200);

            state.IsColorPicking = false;
            pb.Cursor = Cursors.Default;
        }

        private static void PreviewColorAt(Form form, ScaledPictureBox pb, ScreenshotState state, Point location)
        {
            if (!(pb.Image is Bitmap bmp)) return;

            int x = (int)Math.Floor(location.X / Math.Max(0.0001f, state.Scale));
            int y = (int)Math.Floor(location.Y / Math.Max(0.0001f, state.Scale));
            x = Math.Max(0, Math.Min(x, bmp.Width - 1));
            y = Math.Max(0, Math.Min(y, bmp.Height - 1));

            Color c = bmp.GetPixel(x, y);
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            string text = $"{hex}  RGB({c.R},{c.G},{c.B})";

            state.PickerToolTip.Show(
                text,
                form,
                form.PointToClient(Cursor.Position),
                200);
        }

        private static void ClampToScreen(Form form)
        {
            var screen = Screen.FromRectangle(new Rectangle(form.Location, form.Size));
            var bounds = screen.WorkingArea;

            int x = form.Left;
            int y = form.Top;

            if (form.Width >= bounds.Width)
            {
                x = bounds.Left;
            }
            else
            {
                x = Math.Min(Math.Max(form.Left, bounds.Left), bounds.Right - form.Width);
            }

            if (form.Height >= bounds.Height)
            {
                y = bounds.Top;
            }
            else
            {
                y = Math.Min(Math.Max(form.Top, bounds.Top), bounds.Bottom - form.Height);
            }

            form.Location = new Point(x, y);
        }

        // Process one locked pixel buffer instead of allocating a Graphics object for every mosaic block.
        private static void ApplyMosaicEffect(Bitmap bitmap, int blockSize)
        {
            if (blockSize <= 1) return;
            if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
            {
                using (var normalized = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(normalized)) graphics.DrawImageUnscaled(bitmap, Point.Empty);
                    ApplyMosaicEffect(normalized, blockSize);
                    using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(normalized, Point.Empty);
                }
                return;
            }
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = checked(bitmap.Width * 4);
                var pixels = new byte[checked(rowBytes * bitmap.Height)];
                for (int y = 0; y < bitmap.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
                for (int y = 0; y < bitmap.Height; y += blockSize)
                    for (int x = 0; x < bitmap.Width; x += blockSize)
                    {
                        int w = Math.Min(blockSize, bitmap.Width - x), h = Math.Min(blockSize, bitmap.Height - y);
                        long blue = 0, green = 0, red = 0, alpha = 0;
                        for (int py = y; py < y + h; py++) for (int px = x; px < x + w; px++)
                        {
                            int offset = py * rowBytes + px * 4;
                            blue += pixels[offset]; green += pixels[offset + 1]; red += pixels[offset + 2]; alpha += pixels[offset + 3];
                        }
                        int count = w * h;
                        for (int py = y; py < y + h; py++) for (int px = x; px < x + w; px++)
                        {
                            int offset = py * rowBytes + px * 4;
                            pixels[offset] = (byte)(blue / count); pixels[offset + 1] = (byte)(green / count);
                            pixels[offset + 2] = (byte)(red / count); pixels[offset + 3] = (byte)(alpha / count);
                        }
                    }
                for (int y = 0; y < bitmap.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
            finally { bitmap.UnlockBits(data); }
        }
    }
}
