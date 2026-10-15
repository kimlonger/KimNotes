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
        TestSelection();
        TestInitialMenu(root);
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
            Check(menu.Visible && menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "插入便签")
                && menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "标注")
                && Screen.FromControl(form).WorkingArea.Contains(menu.Bounds),
                "completed screenshot automatically opens its action menu within the working area");
            using (var preview = new Bitmap(menu.Width, menu.Height))
            {
                menu.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                preview.Save(Path.Combine(root, "screenshot-auto-menu.png"));
            }
            menu.Close();
            Call(form, "OnActivated", EventArgs.Empty); Application.DoEvents();
            Check(!menu.Visible, "dismissing screenshot menu does not reopen it on activation");
            menu.Show(Picture(form), new Point(10, 10)); Application.DoEvents();
            Check(menu.Visible, "screenshot action menu can still be reopened manually");
            menu.Items.Cast<ToolStripItem>().First(i => i.Text == "标注").PerformClick();
            menu.Close(); Application.DoEvents();
            Check((bool)State(form, "IsAnnotating") && Picture(form).ContextMenuStrip == null,
                "automatic action menu can enter annotation without leaving a menu over the image");
            Click(Tool(form, "text"));
            Drag(form, MouseButtons.Left, new Point(30, 30), new Point(30, 30));
            Check(Children(form).Any(c => c is KimNotes.utils.TransparentInputBox),
                "text annotation can receive focus after entering from the automatic menu");
            Click(Tool(form, "check"));
            form.Close();
        }
        var closed = (Form)Static("CreateScreenshotForm", Pattern(), new Rectangle(40, 40, 160, 120));
        closed.Show(); closed.Close(); Application.DoEvents();
        Check(closed.IsDisposed, "closing screenshot before queued menu display is safe");
    }

    static void TestSelection()
    {
        using (var original = Pattern())
        using (var crop = (Bitmap)Static("CropFromSnapshot", original, new Rectangle(-5, -7, 20, 20)))
            Check(crop.Size == new Size(15, 13) && crop.GetPixel(14, 12) == original.GetPixel(14, 12), "crop clips to snapshot and preserves exact source pixels");
        bool rejected = false;
        using (var original = Pattern())
            try { using (var crop = (Bitmap)Static("CropFromSnapshot", original, new Rectangle(300, 400, 10, 10))) { } }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is ArgumentOutOfRangeException; }
        Check(rejected, "out-of-bounds crop is rejected instead of fabricating a one-pixel image");

        var type = typeof(ScreenshotHelper).Assembly.GetType("KimNotes.utils.ScreenOverlay");
        using (var overlay = (Form)Activator.CreateInstance(type, Flags, null,
            new object[] { new Rectangle(-200, -100, 400, 200), new Bitmap(400, 200) }, null))
        {
            Call(overlay, "OverlayMouseDown", null, new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0));
            Call(overlay, "OverlayMouseUp", null, new MouseEventArgs(MouseButtons.Left, 1, 20, 30, 0));
            var selected = (Rectangle)type.GetProperty("SelectedArea").GetValue(overlay, null);
            Check(selected == new Rectangle(-180, -70, 80, 70), "reverse drag uses final mouse-up position and handles negative monitor coordinates");
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
