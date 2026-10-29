using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using KimNotes;

internal static class ScreenshotRegression
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowEnabled(IntPtr window);
    static Action<bool, string> Check;
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    static object Static(string method, params object[] args) => typeof(ScreenshotHelper).GetMethod(method, Flags).Invoke(null, args);
    static object State(Form f, string key) => f.Tag.GetType().GetProperty(key).GetValue(f.Tag, null);
    static PictureBox Picture(Form f) => f.Controls.OfType<PictureBox>().First(p => p.GetType() != typeof(PictureBox));
    static Bitmap Image(Form f) => (Bitmap)Picture(f).Image;
    static System.Collections.Generic.IEnumerable<Control> Children(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
        if (parent is Form form)
            foreach (var owned in form.OwnedForms)
            {
                yield return owned;
                foreach (var child in Children(owned)) yield return child;
            }
    }
    static ToolIconButton Tool(Form f, string id) => Children(f).OfType<ToolIconButton>().First(c => c.IconId == id);
    static void Click(Control c) => Call(c, "OnClick", EventArgs.Empty);
    static PictureBox Layer(Form f) => f.Controls.OfType<PictureBox>().First(p => p.GetType() == typeof(PictureBox));
    static void Drag(Form f, MouseButtons button, Point start, Point end, bool move = true)
    {
        var layer = Layer(f);
        Call(layer, "OnMouseDown", new MouseEventArgs(button, 1, start.X, start.Y, 0));
        if (move) Call(layer, "OnMouseMove", new MouseEventArgs(button, 0, end.X, end.Y, 0));
        Call(layer, "OnMouseUp", new MouseEventArgs(button, 1, end.X, end.Y, 0));
    }
    static Form Paste(Bitmap image)
    {
        var f = (Form)Static("CreateScreenshotForm", image, new Rectangle(40, 40, image.Width, image.Height));
        f.Show();
        Application.DoEvents();
        f.ContextMenuStrip.Close();
        f.Activate();
        Application.DoEvents();
        return f;
    }
    static void Annotate(Form f) => Static("AnnotateImages", Image(f), f);
    static Bitmap Pattern(int w = 160, int h = 120)
    {
        var result = new Bitmap(w, h);
        for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) result.SetPixel(x, y, Color.FromArgb(x % 256, y % 256, (x + y) % 256));
        return result;
    }
    static int RedPixels(Bitmap image)
    {
        int count = 0;
        for (int x = 0; x < image.Width; x++) for (int y = 0; y < image.Height; y++)
        {
            var c = image.GetPixel(x, y);
            if (c.R > 180 && c.G < 80 && c.B < 80) count++;
        }
        return count;
    }

    public static void Run(string root, Action<bool, string> check)
    {
        Check = check;
        TestSelection(root);
        TestInitialMenu(root);
        TestInitialAction();
        TestMaskPainting(root);
        TestCaptureSession(root);
        TestCaptureCancellation();
        TestFastCaptureHandoff();
        TestDestroyHistory(root);
        TestAnnotations(root);
        TestUndoResources();
        TestTextInput();
        TestLargeAnnotation();
        TestExports(root);
        TestZoomAndEscape();
        TestLifecycle();
        TestOcrResults();
    }

    static void TestInitialMenu(string root)
    {
        using (var form = (Form)Static("CreateScreenshotForm", Pattern(360, 240), new Rectangle(60, 60, 360, 240)))
        {
            form.Show(); Application.DoEvents();
            var menu = form.ContextMenuStrip;
            Check(menu.Items.OfType<ToolStripMenuItem>().Count(i => i.Text == "销毁贴图") == 1
                && menu.Items.OfType<ToolStripMenuItem>().Count(i => i.Text == "关闭贴图") == 1,
                "screenshot right-click menu exposes distinct close and destroy actions");
            var toolbar = form.OwnedForms.Single(f => f.GetType().Name == "ScreenshotToolbarForm");
            Check(toolbar.Visible && !menu.Visible && Children(toolbar).OfType<ToolIconButton>().Count() == 12
                && Screen.FromControl(form).WorkingArea.Contains(toolbar.Bounds),
                "completed screenshot automatically opens a separate action toolbar inside the working area");
            var actionButtons = Children(toolbar).OfType<ToolIconButton>().ToArray();
            Check(actionButtons.All(b => b.Parent.ClientRectangle.Contains(b.Bounds))
                && actionButtons.Select(b => b.Top).Distinct().Count() == 1
                && actionButtons.SelectMany((b, i) => actionButtons.Skip(i + 1).Select(other => !b.Bounds.IntersectsWith(other.Bounds))).All(ok => ok),
                "automatically opened toolbar lays out every icon separately without overlap or clipped hit targets");
            bool monitorBounds = true;
            foreach (var screen in Screen.AllScreens)
            {
                var wa = screen.WorkingArea;
                form.Location = new Point(wa.Right - form.Width - 2, wa.Bottom - form.Height - 2);
                Application.DoEvents();
                monitorBounds &= wa.Contains(toolbar.Bounds) && toolbar.Bottom <= form.Top;
            }
            Check(monitorBounds, "floating toolbar follows mixed-DPI monitors and moves above a selection near the lower screen edge");
            using (var preview = new Bitmap(toolbar.Width, toolbar.Height))
            {
                toolbar.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                preview.Save(Path.Combine(root, "screenshot-auto-toolbar.png"));
            }
            Check(new[] { "ocr", "eyedropper", "notes" }.All(id => actionButtons.Any(b => b.IconId == id && b.Visible))
                && !actionButtons.Any(b => b.IconId == "more" || b.IconId == "pin"),
                "compact single-row toolbar exposes OCR, color picking and insertion directly without a More button");
            Click(Tool(form, "eyedropper"));
            Check((bool)State(form, "IsColorPicking"), "direct color-picking button invokes the existing screenshot action");
            Call(form, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            menu.Close();
            Call(form, "OnActivated", EventArgs.Empty); Application.DoEvents();
            Check(!menu.Visible, "dismissing screenshot menu does not reopen it on activation");
            menu.Show(Picture(form), new Point(10, 10)); Application.DoEvents();
            Check(menu.Visible, "screenshot action menu can still be reopened manually");
            menu.Close();
            Click(Tool(form, "check"));
            using (var copied = Clipboard.GetImage())
                Check(!toolbar.Visible && !form.IsDisposed && form.TopMost && copied.Size == Image(form).Size,
                    "check copies the image and keeps a pinned desktop screenshot while hiding its toolbar");
            menu.Items.Cast<ToolStripItem>().First(i => i.Text == "显示工具栏").PerformClick();
            Check(toolbar.Visible, "right-click command restores the same toolbar after it was hidden");
            menu.Items.Cast<ToolStripItem>().First(i => i.Text == "标注").PerformClick();
            menu.Close(); Application.DoEvents();
            Check((bool)State(form, "IsAnnotating") && Picture(form).ContextMenuStrip == null,
                "automatic action menu can enter annotation without leaving a menu over the image");
            Check(ReferenceEquals(toolbar, State(form, "Toolbar")) && !toolbar.IsDisposed,
                "entering annotation reuses the existing toolbar window instead of destroying and recreating it");
            Click(Tool(form, "text"));
            Drag(form, MouseButtons.Left, new Point(30, 30), new Point(30, 30));
            Console.WriteLine("Annotation focus: " + (Form.ActiveForm?.GetType().Name ?? "none") + "; textSelected=" + Tool(form, "text").Selected + "; inputs=" + Children(form).Count(c => c is KimNotes.utils.TransparentInputBox));
            Check(Children(form).Any(c => c is KimNotes.utils.TransparentInputBox),
                "text annotation can receive focus after entering from the automatic menu");
            Click(Tool(form, "check"));
            Check(ReferenceEquals(toolbar, State(form, "Toolbar")) && !toolbar.IsDisposed,
                "finishing annotation retains the same toolbar for subsequent editing");
            form.Close();
        }
        var closed = (Form)Static("CreateScreenshotForm", Pattern(), new Rectangle(40, 40, 160, 120));
        closed.Show(); closed.Close(); Application.DoEvents();
        Check(closed.IsDisposed, "closing screenshot before queued menu display is safe");
    }

    static void TestInitialAction()
    {
        using (var form = (Form)Static("CreateScreenshotForm", Pattern(), new Rectangle(70, 70, 160, 120)))
        {
            form.Tag.GetType().GetProperty("InitialAction").SetValue(form.Tag, "rect", null);
            form.Show(); Application.DoEvents();
            Check((bool)State(form, "IsAnnotating") && Tool(form, "rect").Selected,
                "drawing action chosen on the selection starts the same annotation tool after capture");
            Drag(form, MouseButtons.Left, new Point(12, 12), new Point(90, 70));
            Click(Tool(form, "copy"));
            using (var copied = Clipboard.GetImage())
            using (var bitmap = copied==null ? null : new Bitmap(copied))
            {
                if(copied==null)Console.WriteLine("Missing annotation clipboard bitmap; formats="+string.Join(",",Clipboard.GetDataObject()?.GetFormats()??new string[0]));
                Check(copied!=null,"annotation copy exports a retrievable system clipboard image");
                Check(!(bool)State(form, "IsAnnotating") && bitmap.Size == new Size(160, 120) && RedPixels(bitmap) > 50,
                    "toolbar copy first commits pending annotations and copies original-resolution pixels");
            }
            Click(Tool(form, "rect"));
            Drag(form, MouseButtons.Left, new Point(20, 20), new Point(110, 90));
            Click(Tool(form, "check"));
            using (var copied = Clipboard.GetImage())
            using (var bitmap = new Bitmap(copied))
                Check(!form.IsDisposed && !(bool)State(form, "IsAnnotating") && form.TopMost && form.OwnedForms.All(f => !f.Visible)
                    && bitmap.Size == Image(form).Size && RedPixels(bitmap) == RedPixels(Image(form)),
                    "one check commits annotations, copies the edited pixels and retains the pinned image");
            form.Close();
        }
        using (var form = (Form)Static("CreateScreenshotForm", Pattern(), new Rectangle(70, 70, 160, 120)))
        {
            form.Tag.GetType().GetProperty("InitialAction").SetValue(form.Tag, "check", null);
            form.Show(); Application.DoEvents();
            using (var copied = Clipboard.GetImage())
                Check(form.Visible && form.TopMost && form.OwnedForms.All(f => !f.Visible) && copied.Size == Image(form).Size,
                    "check chosen on the selection copies and retains the desktop image without another confirmation");
            form.Close();
        }
    }

    static void TestCaptureSession(string root)
    {
        var type = typeof(ScreenshotHelper).Assembly.GetType("KimNotes.utils.ScreenOverlay");
        var wa = Screen.PrimaryScreen.WorkingArea;
        using (var overlay = (Form)Activator.CreateInstance(type, Flags, null,
            new object[] { new Rectangle(wa.Left + 20, wa.Top + 20, 800, 600), Pattern(800, 600) }, null))
        using (var timer = new Timer { Interval = 50 })
        {
            int step = 0, hidden = 0;
            Exception failure = null;
            Form viewer = null;
            Panel tools = null;
            IntPtr toolbarHandle = IntPtr.Zero;
            overlay.VisibleChanged += (s, e) => { if (!overlay.Visible) hidden++; };
            timer.Tick += (s, e) =>
            {
                try
                {
                    if (!overlay.Visible) return;
                    if (step++ == 0)
                    {
                        Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0));
                        Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 460, 340, 0));
                        tools = overlay.Controls.OfType<Panel>().Single(); toolbarHandle = tools.Handle;
                        Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "rect"));
                        viewer = overlay.OwnedForms.Single(f => f.GetType().Name == "ScreenshotForm");
                        Check(overlay.Visible && hidden == 0 && tools.Handle == toolbarHandle && ReferenceEquals(State(viewer, "InlineToolbar"), tools)
                            && IsWindowEnabled(overlay.Handle) && IsWindowEnabled(viewer.Handle) && IsWindowEnabled(tools.Handle)
                            && (bool)State(viewer, "IsAnnotating") && !viewer.OwnedForms.Any(f => f.GetType().Name == "ScreenshotToolbarForm"),
                            "first selection tool click keeps the mask and the same toolbar handle alive in one capture session");
                        using (var background = new Bitmap(viewer.ClientSize.Width, viewer.ClientSize.Height))
                        using (var g = Graphics.FromImage(background))
                        {
                            Call(viewer, "OnPaintBackground", new PaintEventArgs(g, viewer.ClientRectangle));
                            Check(background.GetPixel(200, 100) == Image(viewer).GetPixel(200, 100),
                                "first viewer background already contains screenshot pixels instead of flashing a white frame");
                        }
                        Drag(viewer, MouseButtons.Left, new Point(15, 15), new Point(120, 90));
                    }
                    else
                    {
                        var before = viewer.Bounds;
                        Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "arrow"));
                        Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "copy"));
                        Check(overlay.Visible && hidden == 0 && viewer.Bounds == before && tools.Handle == toolbarHandle && !tools.IsDisposed,
                            "switching tools and copying keep the selection mask, image geometry and toolbar stable");
                        timer.Stop();
                        Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "check"));
                    }
                }
                catch (Exception ex) { failure = ex; timer.Stop(); Call(overlay, "FinishCapture", false); }
            };
            timer.Start();
            var result = (Form)Static("RunCaptureSession", overlay, null, false);
            timer.Stop();
            if (failure != null) throw failure;
            Check(ReferenceEquals(result, viewer) && viewer.Visible && viewer.Owner == null && hidden == 1 && State(viewer, "InlineToolbar") == null && IsWindowEnabled(viewer.Handle),
                "copy-and-pin exits the mask once and leaves the same screenshot window visible");
            overlay.Dispose();
            var savedSize = viewer.ClientSize;
            viewer.ClientSize = new Size(savedSize.Width + 10, savedSize.Height + 10);
            viewer.ClientSize = savedSize;
            Call(viewer, "RecreateHandle");
            Application.DoEvents();
            Check(viewer.Visible && viewer.Owner == null && overlay.IsDisposed && IsWindowEnabled(viewer.Handle),
                "pinned image can resize and recreate its native handle after the original overlay is disposed");
            Application.DoEvents();
            Check(!Children(Picture(viewer)).OfType<ToolIconButton>().Any()
                && !viewer.Controls.OfType<ToolIconButton>().Any(),
                "pinned screenshot has no corner button covering the image after the toolbar is hidden");
            using (var preview = new Bitmap(viewer.Width, viewer.Height))
            { viewer.DrawToBitmap(preview, viewer.ClientRectangle); preview.Save(Path.Combine(root, "screenshot-pinned-clean.png")); }
            string closedId = (string)State(viewer, "HistoryId");
            viewer.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "关闭贴图").PerformClick();
            Check(viewer.IsDisposed, "right-click close remains available when the screenshot toolbar is hidden");
            using (var retained = Archive("Load", closedId, false) as Bitmap)
                Check(retained != null, "closing an unobstructed pinned screenshot preserves its history");
        }
    }

    static void TestCaptureCancellation()
    {
        var type = typeof(ScreenshotHelper).Assembly.GetType("KimNotes.utils.ScreenOverlay");
        var wa = Screen.PrimaryScreen.WorkingArea;
        using (var overlay = (Form)Activator.CreateInstance(type, Flags, null,
            new object[] { new Rectangle(wa.Left + 20, wa.Top + 20, 640, 480), Pattern(640, 480) }, null))
        using (var timer = new Timer { Interval = 50 })
        {
            Exception failure = null;
            Form viewer = null;
            object state = null;
            int step = 0;
            timer.Tick += (s, e) =>
            {
                try
                {
                    if (!overlay.Visible) return;
                    var tools = overlay.Controls.OfType<Panel>().Single();
                    if (step++ == 0)
                    {
                        Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 60, 60, 0));
                        Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 400, 280, 0));
                        Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "rect"));
                        viewer = overlay.OwnedForms.Single(f => f.GetType().Name == "ScreenshotForm"); state = viewer.Tag;
                        Drag(viewer, MouseButtons.Left, new Point(20, 20), new Point(100, 80));
                    }
                    else
                    {
                        timer.Stop();
                        Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "close"));
                    }
                }
                catch (Exception ex) { failure = ex; timer.Stop(); Call(overlay, "FinishCapture", false); }
            };
            timer.Start();
            var result = (Form)Static("RunCaptureSession", overlay, null, false);
            timer.Stop();
            if (failure != null) throw failure;
            Check(result == null && overlay.DialogResult == DialogResult.Cancel && viewer.IsDisposed,
                "canceling after the first drawing tool closes both capture surfaces without leaving a modal window");
            Check(state.GetType().GetProperty("CleanupAnnotation").GetValue(state, null) == null && overlay.OwnedForms.Length == 0,
                "canceling an in-progress capture releases annotation resources and owned windows");
        }
    }

    static void TestFastCaptureHandoff()
    {
        var type = typeof(ScreenshotHelper).Assembly.GetType("KimNotes.utils.ScreenOverlay");
        var wa = Screen.PrimaryScreen.WorkingArea;
        foreach (string action in new[] { "check", "toolbar", "close", "check" })
        using (var overlay = (Form)Activator.CreateInstance(type, Flags, null,
            new object[] { new Rectangle(wa.Left + 30, wa.Top + 30, 500, 350), Pattern(500, 350) }, null))
        using (var timer = new Timer { Interval = 20 })
        {
            Form viewer = null;
            Exception failure = null;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 40, 40, 0));
                    Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 320, 240, 0));
                    var tools = overlay.Controls.OfType<Panel>().Single();
                    Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "copy"));
                    viewer = overlay.OwnedForms.Single(f => f.GetType().Name == "ScreenshotForm");
                    // Complete in the same input event, before Form.CallShownEvent is pumped.
                    if (action == "toolbar") Call(overlay, "ConfirmSelection");
                    else Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == action));
                }
                catch (Exception ex) { failure = ex; Call(overlay, "FinishCapture", false); }
            };
            System.Threading.ThreadExceptionEventHandler deferredError = (s, e) => failure = e.Exception;
            Application.ThreadException += deferredError;
            try
            {
                timer.Start();
                var result = (Form)Static("RunCaptureSession", overlay, null, false);
                overlay.Dispose();
                if (failure != null) throw failure;
                if (action != "close")
                {
                    Check(ReferenceEquals(result, viewer) && viewer.Owner == null, "fast " + action + " detaches the screenshot before releasing the overlay");
                    viewer.ClientSize = new Size(viewer.ClientSize.Width + 2, viewer.ClientSize.Height + 2);
                    Call(viewer, "RecreateHandle");
                }
                Application.DoEvents();
                if (failure != null) throw failure;
                Check(action == "close" ? viewer.IsDisposed : viewer.Visible,
                    "deferred shown/layout callbacks remain safe after immediate overlay disposal for " + action);
            }
            finally
            {
                Application.ThreadException -= deferredError;
                if (viewer != null && !viewer.IsDisposed) viewer.Close();
            }
        }
        using (var form = Paste(Pattern()))
        {
            form.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "关闭贴图").PerformClick();
            Check(form.IsDisposed, "right-click close closes the current screenshot");
            var recalled = ScreenshotHelper.RecallLatest();
            Check(recalled != null && Image(recalled).GetPixel(10, 10) == Color.FromArgb(10, 10, 20),
                "right-click close retains the closed screenshot for later recall");
            recalled?.Close();
        }
    }

    static object Archive(string method, params object[] args) => typeof(ScreenshotHelper).Assembly
        .GetType("KimNotes.utils.ScreenshotArchive").GetMethod(method, Flags).Invoke(null, args);

    static void TestDestroyHistory(string root)
    {
        foreach (bool incognito in new[] { false, true })
        using (var source = Pattern())
        {
            string sentinel = (string)Archive("Save", source, incognito, null);
            using (var form = (Form)Static("CreateScreenshotForm", new Bitmap(source), new Rectangle(40, 40, 160, 120)))
            {
                form.Tag.GetType().GetProperty("Incognito").SetValue(form.Tag, incognito, null);
                form.Tag.GetType().GetProperty("AutoMenu").SetValue(form.Tag, false, null);
                form.Show(); Application.DoEvents();
                string id = (string)State(form, "HistoryId");
                Archive("Save", source, incognito, id); // Include the backup on disk.
                string exported = Path.Combine(root, "export-kept-" + incognito + ".png");
                source.Save(exported, ImageFormat.Png);
                using (var duplicate = (Form)Static("CreateScreenshotForm", new Bitmap(source), new Rectangle(220, 40, 160, 120)))
                {
                    foreach (var entry in new[] { new { Key = "HistoryId", Value = (object)id },
                        new { Key = "Incognito", Value = (object)incognito }, new { Key = "HistoryDirty", Value = (object)false },
                        new { Key = "AutoMenu", Value = (object)false } })
                        duplicate.Tag.GetType().GetProperty(entry.Key).SetValue(duplicate.Tag, entry.Value, null);
                    duplicate.Show(); Application.DoEvents();
                    form.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "销毁贴图").PerformClick();
                    Application.DoEvents();
                    Check(form.IsDisposed && duplicate.IsDisposed, "destroy closes every view of the deleted screenshot: incognito=" + incognito);
                }
                var entries = (string[])Archive("List", incognito);
                using (var deleted = Archive("Load", id, incognito) as Bitmap)
                    Check(!entries.Contains(id) && entries.Contains(sentinel) && deleted == null,
                        "destroy removes only the selected history record and prevents resurrection: incognito=" + incognito);
                Check(File.Exists(exported) && (incognito || !File.Exists(Path.Combine(root, "screenshot-history", id + ".bak"))),
                    "destroy removes history backup while preserving exported images: incognito=" + incognito);
                if (!incognito)
                {
                    var recalled = ScreenshotHelper.RecallLatest(); Application.DoEvents();
                    Check(recalled != null && (string)State(recalled, "HistoryId") == sentinel,
                        "recall skips the destroyed screenshot and opens the remaining latest record");
                    recalled?.Close();
                }
            }
        }
    }

    static void TestMaskPainting(string root)
    {
        var type = typeof(ScreenshotHelper).Assembly.GetType("KimNotes.utils.ScreenOverlay");
        var source = new Bitmap(600, 400);
        using (var g = Graphics.FromImage(source)) g.Clear(Color.White);
        using (var overlay = (Form)Activator.CreateInstance(type, Flags, null,
            new object[] { new Rectangle(-200, -100, 600, 400), source }, null))
        using (var frame = new Bitmap(600, 400))
        {
            Action<Rectangle> paint = clip =>
            {
                using (var g = Graphics.FromImage(frame))
                {
                    g.SetClip(clip);
                    Call(overlay, "OnPaint", new PaintEventArgs(g, clip));
                }
            };
            paint(new Rectangle(0, 0, 600, 400));
            int initial = frame.GetPixel(5, 5).R;
            Check(initial > 120 && initial < 135 && frame.GetPixel(550, 380).R == initial,
                "capture starts with a uniform dimmed desktop instead of an unmasked first frame");
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 150, 150, 0));
            Call(overlay, "OverlayMouseMove", null, new MouseEventArgs(MouseButtons.Left, 0, 350, 300, 0));
            var partial = new Rectangle(0, 0, 400, 350);
            paint(partial);
            Check(frame.GetPixel(5, 5).R == initial && frame.GetPixel(550, 380).R == initial && frame.GetPixel(250, 220).R == 255,
                "first partial selection repaint leaves both upper-left and remote desktop areas equally dim");
            paint(partial);
            Check(frame.GetPixel(5, 5).R == initial && source.GetPixel(5, 5).R == 255,
                "repeated partial repaints neither accumulate a darker patch nor alter the captured source pixels");
            frame.Save(Path.Combine(root, "screenshot-mask-partial.png"));
        }
    }

    static void TestSelection(string root)
    {
        using (var original = Pattern())
        using (var crop = (Bitmap)Static("CropFromSnapshot", original, new Rectangle(-5, -7, 20, 20)))
            Check(crop.Size == new Size(15, 13) && crop.GetPixel(14, 12) == original.GetPixel(14, 12), "crop clips to snapshot and preserves exact source pixels");
        bool rejected = false;
        using (var original = Pattern())
            try { using (var crop = (Bitmap)Static("CropFromSnapshot", original, new Rectangle(300, 400, 10, 10))) { } }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is ArgumentOutOfRangeException; }
        Check(rejected, "out-of-bounds crop is rejected instead of fabricating a one-pixel image");

        var overlayType = typeof(ScreenshotHelper).Assembly.GetType("KimNotes.utils.ScreenOverlay");
        var snapshot = new Bitmap(1200, 750);
        using (var g = Graphics.FromImage(snapshot))
        using (var font = new Font("Microsoft YaHei UI", 14f))
        {
            g.Clear(Color.FromArgb(239, 243, 248));
            g.FillRectangle(Brushes.White, 80, 70, 1000, 530);
            g.DrawString("小羊便签 · 截图工具栏预览", font, Brushes.DimGray, 130, 110);
            g.DrawString("选区可移动、调整大小，框选完成后直接操作。", font, Brushes.SlateGray, 130, 175);
        }
        using (var overlay = (Form)Activator.CreateInstance(overlayType, Flags, null,
            new object[] { new Rectangle(0, 0, 1200, 750), snapshot }, null))
        {
            overlay.Show(); Application.DoEvents();
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 120, 145, 0));
            Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 900, 470, 0));
            var tools = overlay.Controls.OfType<Panel>().Single();
            Check(tools.Visible && tools.Controls.OfType<ToolIconButton>().Count() == 12 && overlay.ClientRectangle.Contains(tools.Bounds),
                "finishing a selection exposes annotation and output tools without a second menu step");
            using (var preview = new Bitmap(overlay.Width, overlay.Height))
            { overlay.DrawToBitmap(preview, overlay.ClientRectangle); preview.Save(Path.Combine(root, "screenshot-selection-toolbar.png")); }
            using (var crop = (Bitmap)Static("CropFromSnapshot", snapshot, new Rectangle(120, 145, 780, 325)))
                Check(crop.GetPixel(770, 320).ToArgb() == Color.White.ToArgb(), "selection border and toolbar never become part of captured source pixels");
            Click(tools.Controls.OfType<ToolIconButton>().First(b => b.IconId == "rect"));
            Check(overlay.DialogResult == DialogResult.OK && (string)overlayType.GetProperty("SelectedAction", Flags).GetValue(overlay, null) == "rect",
                "selection toolbar carries the chosen drawing tool into screenshot editing");
        }

        var type = typeof(ScreenshotHelper).Assembly.GetType("KimNotes.utils.ScreenOverlay");
        using (var overlay = (Form)Activator.CreateInstance(type, Flags, null,
            new object[] { new Rectangle(-200, -100, 400, 200), new Bitmap(400, 200) }, null))
        {
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0));
            Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 20, 30, 0));
            var selected = (Rectangle)type.GetProperty("SelectedArea").GetValue(overlay, null);
            Check(selected == new Rectangle(-180, -70, 80, 70), "reverse drag uses final mouse-up position and handles negative monitor coordinates");
            Check(overlay.DialogResult == DialogResult.None, "releasing a screenshot selection keeps it open for adjustment");
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 100, 65, 0));
            Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 150, 65, 0));
            selected = (Rectangle)type.GetProperty("SelectedAreaLocal").GetValue(overlay, null);
            Check(selected == new Rectangle(20, 30, 130, 70), "selection edge drag changes crop size while retaining the opposite edge");
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 60, 65, 0));
            Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, -40, -35, 0));
            selected = (Rectangle)type.GetProperty("SelectedAreaLocal").GetValue(overlay, null);
            Check(selected == new Rectangle(0, 0, 130, 70), "selection movement clamps to snapshot bounds and preserves crop dimensions");
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 130, 70, 0));
            Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 500, 300, 0));
            selected = (Rectangle)type.GetProperty("SelectedAreaLocal").GetValue(overlay, null);
            Check(selected == new Rectangle(0, 0, 400, 200), "corner resize clamps to the full snapshot without overflowing");
            Call(overlay, "ConfirmSelection");
            Check(overlay.DialogResult == DialogResult.OK && (Rectangle)type.GetProperty("SelectedArea").GetValue(overlay, null) == new Rectangle(-200, -100, 400, 200),
                "explicit screenshot confirmation returns adjusted global bounds");
        }
        using (var overlay = (Form)Activator.CreateInstance(type, Flags, null,
            new object[] { new Rectangle(0, 0, 200, 100), new Bitmap(200, 100) }, null))
        {
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Right, 1, 40, 40, 0));
            Check(overlay.DialogResult == DialogResult.Cancel, "right-click cancels screen selection");
        }
        var field = typeof(ScreenshotHelper).GetField("captureInProgress", Flags);
        field.SetValue(null, 1);
        try { Check(Static("CaptureInteractive", null, false) == null, "all screenshot entry points share one reentrancy guard"); }
        finally { field.SetValue(null, 0); }
    }

    static void TestAnnotations(string root)
    {
        using (var f = Paste(Pattern()))
        {
            Annotate(f); Click(Tool(f, "rect"));
            Drag(f, MouseButtons.Right, new Point(10, 10), new Point(70, 70));
            Click(Tool(f, "check"));
            Check(Image(f).GetPixel(10, 10) == Color.FromArgb(10, 10, 20), "right mouse button cannot accidentally draw annotations");
            Annotate(f); Click(Tool(f, "rect"));
            Drag(f, MouseButtons.Left, new Point(12, 12), new Point(80, 60), false);
            Click(Tool(f, "undo")); Click(Tool(f, "check"));
            Check(Image(f).GetPixel(12, 12) == Color.FromArgb(12, 12, 24), "undo removes a rectangle even when no mouse-move event preceded release");
            Annotate(f); Click(Tool(f, "rect"));
            Drag(f, MouseButtons.Left, new Point(12, 12), new Point(80, 60));
            Click(Tool(f, "check"));
            Check(RedPixels(Image(f)) > 50, "rectangle annotations are committed into the actual image");
            Annotate(f); Click(Tool(f, "mosaic"));
            Drag(f, MouseButtons.Left, new Point(5, 5), new Point(95, 85));
            Click(Tool(f, "check"));
            Check(Image(f).GetPixel(20, 20) != Color.FromArgb(20, 20, 40), "mosaic commits safely onto previously annotated images");
            Annotate(f); Click(Tool(f, "arrow"));
            Drag(f, MouseButtons.Left, new Point(130, 90), new Point(100, 30));
            Click(Tool(f, "check"));
            Check(RedPixels(Image(f)) > 80, "reverse-direction arrow works across repeated annotation sessions");
            Annotate(f); Click(Tool(f, "text"));
            Drag(f, MouseButtons.Left, new Point(35, 35), new Point(35, 35));
            var input = Children(f).First(c => c is KimNotes.utils.TransparentInputBox);
            foreach (char character in "Test 文本") Call(input, "OnKeyPress", new KeyPressEventArgs(character));
            Call(input, "OnLostFocus", EventArgs.Empty);
            Check(input.IsDisposed, "committing text on focus loss disposes input exactly once without recursive callbacks");
            Click(Tool(f, "check"));
            Image(f).Save(Path.Combine(root, "annotation-result.png"));
            Check(!((bool)State(f, "IsAnnotating")) && Picture(f).ContextMenuStrip != null, "finishing text restores screenshot menu and normal mode");
            var bitmap = Image(f);
            f.Dispose();
            bool disposed = false;
            try { bitmap.GetPixel(0, 0); } catch (ArgumentException) { disposed = true; }
            Check(disposed, "disposing screenshot directly releases its owned bitmap");
        }
    }

    static void TestLargeAnnotation()
    {
        using (var image = new Bitmap(1800, 1400))
        {
            using (var g = Graphics.FromImage(image)) g.Clear(Color.White);
            using (var f = Paste(new Bitmap(image)))
            {
                Annotate(f);
                var area = Screen.FromControl(f).WorkingArea;
                Check(area.Contains(f.Bounds), "large screenshot annotation and toolbar fit entirely in monitor working area");
                var scale = (float)State(f, "Scale");
                Click(Tool(f, "rect"));
                Drag(f, MouseButtons.Left, new Point((int)(50 * scale), (int)(50 * scale)), new Point((int)(200 * scale), (int)(200 * scale)));
                Click(Tool(f, "check"));
                Check(Image(f).Size == image.Size && RedPixels(Image(f)) > 200, "annotation fit preserves full-resolution image and maps drawing coordinates correctly");
            }
        }
    }

    static void TestUndoResources()
    {
        using (var f = Paste(Pattern()))
        {
            Annotate(f); Click(Tool(f, "mosaic"));
            var cleanup = (Action)State(f, "CleanupAnnotation");
            var annotations = (System.Collections.IList)cleanup.Target.GetType().GetField("annotations", Flags).GetValue(cleanup.Target);
            Drag(f, MouseButtons.Left, new Point(5, 5), new Point(75, 75));
            var retained = (Bitmap)annotations[0].GetType().GetProperty("Resource").GetValue(annotations[0], null);
            bool allUndoneDisposed = true;
            for (int i = 0; i < 8; i++)
            {
                Drag(f, MouseButtons.Left, new Point(20, 20), new Point(95, 95));
                var latest = annotations[annotations.Count - 1];
                var patch = (Bitmap)latest.GetType().GetProperty("Resource").GetValue(latest, null);
                Click(Tool(f, "undo"));
                bool disposed = false;
                try { patch.GetPixel(0, 0); } catch (ArgumentException) { disposed = true; }
                allUndoneDisposed &= disposed && annotations.Count == 1;
            }
            Check(allUndoneDisposed, "repeated mosaic undo immediately disposes every removed bitmap while annotation remains open");
            bool retainedAlive = true;
            try { retained.GetPixel(0, 0); } catch (ArgumentException) { retainedAlive = false; }
            Check(retainedAlive, "undo preserves bitmap resources belonging to earlier annotations");
            Click(Tool(f, "check"));
            bool retainedDisposed = false;
            try { retained.GetPixel(0, 0); } catch (ArgumentException) { retainedDisposed = true; }
            Check(retainedDisposed && annotations.Count == 0, "finishing annotation disposes remaining resources and clears the history");
        }
    }

    static void TestTextInput()
    {
        foreach (string element in new[] { "\U0001F600", "e\u0301" })
        {
            using (var input = new KimNotes.utils.TransparentInputBox())
            {
                foreach (char character in "L" + element + "R") Call(input, "OnKeyPress", new KeyPressEventArgs(character));
                var caret = input.GetType().GetField("_caretIndex", Flags);
                Call(input, "OnKeyDown", new KeyEventArgs(Keys.Left));
                bool boundaries = (int)caret.GetValue(input) == 1 + element.Length;
                Call(input, "OnKeyDown", new KeyEventArgs(Keys.Left));
                boundaries &= (int)caret.GetValue(input) == 1;
                Call(input, "OnKeyDown", new KeyEventArgs(Keys.Right));
                boundaries &= (int)caret.GetValue(input) == 1 + element.Length;
                Check(boundaries, "text annotation arrows keep Unicode text element intact: " + element);
                Call(input, "OnKeyDown", new KeyEventArgs(Keys.Back));
                Check(input.GetText() == "LR" && (int)caret.GetValue(input) == 1,
                    "text annotation Backspace removes the entire Unicode text element: " + element);
            }
            using (var input = new KimNotes.utils.TransparentInputBox())
            {
                foreach (char character in "L" + element + "R") Call(input, "OnKeyPress", new KeyPressEventArgs(character));
                Call(input, "OnKeyDown", new KeyEventArgs(Keys.Home));
                Call(input, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Call(input, "OnKeyDown", new KeyEventArgs(Keys.Delete));
                Check(input.GetText() == "LR" && (int)input.GetType().GetField("_caretIndex", Flags).GetValue(input) == 1,
                    "text annotation Delete removes the entire Unicode text element: " + element);
            }
        }
        using (var input = new KimNotes.utils.TransparentInputBox())
        {
            var zero = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 0);
            Call(input, "HandleZoom", zero, Keys.Control);
            Check(zero.Handled && Math.Abs(input._scale - 1f) < 0.001f,
                "zero-delta Ctrl wheel is consumed without changing annotation text scale");
            var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 120);
            Call(input, "HandleZoom", wheel, Keys.Control);
            Call(input, "HandleZoom", wheel, Keys.Control);
            Check(wheel.Handled && Math.Abs(input._scale - 1.1f) < 0.001f,
                "Ctrl wheel scales annotation text once and marks the event handled");
            var ordinary = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120);
            Call(input, "HandleZoom", ordinary, Keys.None);
            var alreadyHandled = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120) { Handled = true };
            Call(input, "HandleZoom", alreadyHandled, Keys.Control);
            Check(!ordinary.Handled && Math.Abs(input._scale - 1.1f) < 0.001f,
                "ordinary or already handled wheel events do not alter annotation text scale");
        }
    }

    static void TestExports(string root)
    {
        using (var image = Pattern())
        {
            string jpeg = Path.Combine(root, "export.JPG");
            Static("ExportImage", image, jpeg);
            using (var exported = System.Drawing.Image.FromFile(jpeg))
                Check(exported.RawFormat.Guid == ImageFormat.Jpeg.Guid, "uppercase JPG extension exports real JPEG bytes");
            for (int i = 0; i < 8; i++) Static("SaveToDefaultPath", image, Path.Combine(root, "rapid-saves"));
            Check(Directory.GetFiles(Path.Combine(root, "rapid-saves"), "*.png").Length == 8, "rapid screenshot saves never overwrite one another");
        }
    }

    static void TestLifecycle()
    {
        for (int i = 0; i < 12; i++)
        {
            using (var f = Paste(Pattern()))
            {
                var state = f.Tag;
                Annotate(f); Click(Tool(f, "mosaic"));
                Drag(f, MouseButtons.Left, new Point(10, 10), new Point(90, 90));
                var layer = Layer(f);
                f.Dispose();
                if (!layer.IsDisposed || state.GetType().GetProperty("CleanupAnnotation").GetValue(state, null) != null)
                    throw new Exception("Annotation resources survived disposal.");
            }
        }
        Check(true, "closing during annotation repeatedly releases drawing session controls and cleanup handlers");
    }

    static void TestZoomAndEscape()
    {
        using (var f = Paste(Pattern()))
        {
            var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 20, 20, 120);
            Call(Picture(f), "OnMouseWheel", wheel);
            Call(f, "OnMouseWheel", wheel);
            Check(Math.Abs((float)State(f, "Scale") - 1.1f) < 0.001f, "wheel event bubbling changes screenshot zoom only once");
            Check(Image(f).Size == new Size(160, 120), "screenshot zoom never resamples or replaces source pixels");
            f.Tag.GetType().GetProperty("IsColorPicking").SetValue(f.Tag, true, null);
            Call(Picture(f), "OnDoubleClick", EventArgs.Empty);
            Check(!f.IsDisposed, "double-click during color picking cannot save and close the screenshot");
            var escape = new KeyEventArgs(Keys.Escape);
            Call(f, "OnKeyDown", escape);
            Check(!f.IsDisposed && !(bool)State(f, "IsColorPicking") && escape.Handled && Picture(f).Cursor == Cursors.Default,
                "Escape cancels color picking while preserving the screenshot");
            Static("StartColorPick", f);
            Call(Picture(f), "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 20, 20, 0));
            Check(!f.IsDisposed && !(bool)State(f, "IsColorPicking") && Picture(f).Cursor == Cursors.Default,
                "right-click also cancels color picking without closing the screenshot");
            Annotate(f); Click(Tool(f, "rect"));
            Drag(f, MouseButtons.Left, new Point(10, 10), new Point(80, 60));
            Call(f, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            Check(!f.IsDisposed && !(bool)State(f, "IsAnnotating") && RedPixels(Image(f)) > 50,
                "first Escape finishes annotation and preserves the screenshot");
            Call(f, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            Check(f.IsDisposed, "second Escape closes the screenshot after annotation has finished");
        }
        using (var pixels = new Bitmap(2, 2, PixelFormat.Format32bppArgb))
        {
            pixels.SetPixel(0, 0, Color.FromArgb(100, 20, 10, 30));
            pixels.SetPixel(1, 0, Color.FromArgb(100, 40, 10, 30));
            pixels.SetPixel(0, 1, Color.FromArgb(100, 60, 10, 30));
            pixels.SetPixel(1, 1, Color.FromArgb(100, 80, 10, 30));
            Static("ApplyMosaicEffect", pixels, 2);
            Check(pixels.GetPixel(0, 0) == Color.FromArgb(100, 50, 10, 30) && pixels.GetPixel(0, 0) == pixels.GetPixel(1, 1),
                "optimized mosaic computes correct block average and preserves transparency");
        }
    }

    static void TestOcrResults()
    {
        Check((string)Static("ParseOcrResponse", "{\"words_result\":[]}") == "", "empty OCR results do not fabricate note content");
        Check((string)Static("ParseOcrResponse", "{\"words_result\":[{\"words\":\"one\"},{\"words\":\"two\"}]}") == "one" + Environment.NewLine + "two", "OCR text parser preserves line order");
        bool failed = false;
        try { Static("ParseOcrResponse", "{\"error_code\":110,\"error_msg\":\"invalid token\"}"); }
        catch (TargetInvocationException ex) { failed = ex.InnerException is InvalidOperationException; }
        Check(failed, "OCR service errors are reported rather than creating blank notes");
    }
}
