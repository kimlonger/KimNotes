using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class Regression
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct CharRange { public int Min, Max; }
    [StructLayout(LayoutKind.Sequential)] struct FormatRange
    {
        public IntPtr Hdc, Target;
        public Rect Area, Page;
        public CharRange Range;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr FormatMessage(IntPtr window, int message, IntPtr render, ref FormatRange range);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr ReleaseFormat(IntPtr window, int message, IntPtr render, IntPtr range);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
    static readonly Assembly App = typeof(note).Assembly;
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static string root;
    static int checks;
    static object Call(object instance, string method, params object[] args) =>
        instance.GetType().GetMethod(method, Flags).Invoke(instance, args);
    static object Static(string type, string method, params object[] args) =>
        App.GetType(type).GetMethod(method, Flags).Invoke(null, args);
    static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Flags).GetValue(instance);
    static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, Flags).SetValue(instance, value);
    static RichTextBox Body(note n) => Field<RichTextBox>(n, "richTextBox1");
    static void WriteTestNote(string path, string text)
    {
        using (var editor = new RichTextBox())
        {
            var handle = editor.Handle;
            editor.Text = text;
            editor.SaveFile(path, RichTextBoxStreamType.RichText);
        }
    }
    static string PathOf(note n) => (string)typeof(note).GetProperty("FilePath", Flags).GetValue(n, null);
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
        checks++;
    }
    static bool Save(note n) => (bool)Call(n, "SaveCurrentNoteSafe");
    static void ChooseTheme(note n, int index)
    {
        n.Activate();
        Call(n, "Touch");
        Call(n, "AdvanceChromeAnimation", DateTime.UtcNow.AddSeconds(2));
        Call(n, "ToggleMore");
        Field<System.Collections.Generic.List<Button>>(n, "swatchButtons")[index].PerformClick();
        Call(n, "HideMore");
    }

    [STAThread]
    static int Main(string[] args)
    {
        root = args[0];
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR", root);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Static("KimNotes.utils.InitConfig", "InitSettings");
            TestAtomicWrites();
            TestConfigFailure();
            TestNotes();
            TestRichTextFormatting();
            TestEditingAndDisposal();
            TestMetadataRecovery();
            TestScreenshotSessions();
            ScreenshotRegression.Run(root, Assert);
            TestSettingsLayout();
            TestSettingsResetAndRestart();
            TestConfigRollbackOnFailedRestart();
            TestHotkeyLifecycleAndStartup();
            TestTodos();
            TestTodoPersistenceFailures();
            TestReminderWindows();
            UiRegression.Run(root, Assert);
            ExperienceRegression.Run(root, Assert);
            TestIncognito();
            Console.WriteLine("All " + checks + " checks passed; data: " + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (Form f in Application.OpenForms.Cast<Form>().ToArray()) f.Close();
        }
    }

    static void TestAtomicWrites()
    {
        var path = Path.Combine(root, "atomic.txt");
        Static("KimNotes.utils.Storage", "WriteText", path, "first");
        Static("KimNotes.utils.Storage", "WriteText", path, "second");
        Assert(File.ReadAllText(path) == "second" && File.ReadAllText(path + ".bak") == "first", "atomic replace retains previous version");
        try
        {
            Static("KimNotes.utils.Storage", "Write", path, new Action<string>(temp => { File.WriteAllText(temp, "partial"); throw new IOException("simulated interruption"); }));
        }
        catch (TargetInvocationException) { }
        Assert(File.ReadAllText(path) == "second" && Directory.GetFiles(root, "*.tmp").Length == 0, "interrupted write preserves original and removes temp");
    }

    static void TestConfigFailure()
    {
        var type = App.GetType("KimNotes.utils.InitConfig");
        var field = type.GetField("noteConfig", Flags);
        var original = (string)field.GetValue(null);
        var blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "block");
        field.SetValue(null, Path.Combine(blocker, "config.txt"));
        var before = Static("KimNotes.utils.InitConfig", "GetConfigValue", "theme");
        bool failed = false;
        try { Static("KimNotes.utils.InitConfig", "SetConfigValue", "theme", "7"); }
        catch (TargetInvocationException) { failed = true; }
        finally { field.SetValue(null, original); }
        Assert(failed && Equals(before, Static("KimNotes.utils.InitConfig", "GetConfigValue", "theme")), "failed config write does not change in-memory settings");
    }

    static void TestNotes()
    {
        var contextType = App.GetType("KimNotes.MyApplicationContext");
        var context = FormatterServices.GetUninitializedObject(contextType);
        App.GetType("Program").GetField("AppContext", Flags).SetValue(null, context);
        var first = (note)Call(context, "AddNewForm", null, false);
        Body(first).Text = "Original\nline2\nline3\nline4\nline5\nDeepSearchToken";
        Assert(Save(first), "initial note saves");
        string firstPath = PathOf(first);
        var reopened = (note)Call(context, "AddNewForm", Path.GetFileName(firstPath), false);
        Assert(ReferenceEquals(first, reopened), "same note activates existing window");
        ChooseTheme(first, 3);
        var second = (note)Call(context, "AddNewForm", null, false);
        Body(second).Text = "Second";
        ChooseTheme(second, 2);
        Assert(Body(first).BackColor == NoteTheme.All[3].Body && Body(second).BackColor == NoteTheme.All[2].Body, "note themes are independent");
        Assert(Field<NoteTheme>(Field<ToolIconButton>(first, "button1"), "<Theme>k__BackingField").Id == 3, "button palette belongs to note instance");
        Assert(Save(second) && firstPath != PathOf(second), "new notes get distinct file identities");
        TestIdleThemeLifecycle(first, second);
        string firstName = Path.GetFileName(firstPath);
        first.Close();
        first = (note)Call(context, "AddNewForm", firstName, false);
        Assert(Body(first).BackColor == NoteTheme.All[3].Body, "theme survives reopen");
        TestCompactPresentation(first);
        Body(first).BackColor = Color.White;
        Call(first, "RestoreThemeSurface");
        Assert(Body(first).BackColor == NoteTheme.All[3].Body, "theme restored after surface reset");

        var history = new history(Path.GetDirectoryName(firstPath));
        history.Show();
        Application.DoEvents();
        var allCards = (System.Collections.IEnumerable)history.GetType().GetField("allCards", Flags).GetValue(history);
        bool found = false;
        foreach (var card in allCards)
        {
            string file = (string)card.GetType().GetProperty("FileName").GetValue(card, null);
            if (file == firstName)
            {
                found = ((string)card.GetType().GetProperty("SearchText").GetValue(card, null)).Contains("deepsearchtoken");
                Assert((Color)card.GetType().GetProperty("BodyColor").GetValue(card, null) == NoteTheme.All[3].Body, "history card uses per-note color");
            }
        }
        Assert(found, "search indexes text after fifth line");
        history.Close();

        Body(first).Clear();
        Assert(Save(first), "cleared existing note saves");
        first.Close();
        first = (note)Call(context, "AddNewForm", firstName, false);
        Assert(string.IsNullOrWhiteSpace(Body(first).Text), "cleared content stays empty after reopen");
        using (var image = new Bitmap(80, 50))
        {
            using (var g = Graphics.FromImage(image)) g.Clear(Color.Red);
            first.InsertImage(image);
        }
        Assert(Body(first).Rtf.Contains("\\pict"), "embedded image appears in RTF");
        Assert(Save(first), "image-only note saves");
        first.Close();
        first = (note)Call(context, "AddNewForm", firstName, false);
        Assert(Body(first).Rtf.Contains("\\pict"), "embedded image survives reopen");
        using (var rendered = new Bitmap(first.Width, first.Height))
        {
            // DrawToBitmap omits RichEdit OLE pictures; ask RichEdit itself to render its document.
            using (var graphics = Graphics.FromImage(rendered))
            {
                graphics.Clear(Color.White);
                var area = new Rect { Right = (int)(rendered.Width * 1440 / graphics.DpiX), Bottom = (int)(rendered.Height * 1440 / graphics.DpiY) };
                var hdc = graphics.GetHdc();
                try
                {
                    var range = new FormatRange { Hdc = hdc, Target = hdc, Area = area, Page = area, Range = new CharRange { Min = 0, Max = -1 } };
                    FormatMessage(Body(first).Handle, 0x0439, (IntPtr)1, ref range);
                    ReleaseFormat(Body(first).Handle, 0x0439, IntPtr.Zero, IntPtr.Zero);
                }
                finally { graphics.ReleaseHdc(hdc); }
            }
            rendered.Save(Path.Combine(root, "note-image.png"));
            int red = 0;
            for (int x = 0; x < rendered.Width; x++)
                for (int y = 0; y < rendered.Height; y++)
                {
                    var pixel = rendered.GetPixel(x, y);
                    if (pixel.R > 220 && pixel.G < 60 && pixel.B < 60) red++;
                }
            Assert(red > 100, "reopened embedded image renders visible pixels");
        }
        string imagePath = PathOf(first);
        first.Close(); second.Close();
        var blank = (note)Call(context, "AddNewForm", null, false);
        Assert(Body(blank).TextLength == 0 && PathOf(blank) == null, "new note after all windows closed never restores old file");
        blank.SetText("OCR output"); Save(blank); blank.Close();
        Assert(File.ReadAllText(imagePath).Contains("\\pict"), "OCR-style new note does not overwrite previous image note");
        var deletion = (note)Call(context, "AddNewForm", firstName, false);
        Body(deletion).AppendText("unsaved change");
        history = new history(Path.GetDirectoryName(imagePath)); history.Show();
        Call(history, "DeleteNote", firstName);
        Assert(deletion.IsDisposed && !File.Exists(imagePath), "deleting an open note closes it without resurrecting deleted data");
        history.Close();
        // Migrate the remaining note instead of the deleted one.
        firstName = Path.GetFileName(Directory.GetFiles(Path.GetDirectoryName(imagePath), "*.rtf")[0]);
        var dest = Path.Combine(root, "migrated");
        Static("KimNotes.utils.Storage", "CopyNotes", Path.GetDirectoryName(imagePath), dest);
        Assert(File.Exists(Path.Combine(dest, firstName)) && File.Exists(Path.Combine(dest, firstName + ".meta.json")), "directory migration includes note and theme");
        File.WriteAllText(Path.Combine(dest, firstName), "collision");
        bool blocked = false;
        try { Static("KimNotes.utils.Storage", "CopyNotes", Path.GetDirectoryName(imagePath), dest); }
        catch (TargetInvocationException) { blocked = true; }
        Assert(blocked && File.ReadAllText(Path.Combine(dest, firstName)) == "collision", "migration refuses conflicting destination files");
    }

    static void TestCompactPresentation(note n)
    {
        Call(n, "Touch");
        Call(n, "AdvanceChromeAnimation", DateTime.UtcNow.AddSeconds(2));
        Rectangle bodyBounds = Field<Panel>(n, "bodyPanel").Bounds;
        Rectangle textBounds = n.RectangleToClient(Body(n).RectangleToScreen(Body(n).ClientRectangle));
        var now = DateTime.UtcNow;
        Call(n, "UpdateCompactState", now, true, false);
        Call(n, "UpdateCompactState", now.AddSeconds(60), true, false);
        Assert(!Field<bool>(n, "compact"), "focused note remains expanded even when pointer is outside");
        Call(n, "UpdateCompactState", now, false, true);
        Call(n, "UpdateCompactState", now.AddSeconds(60), false, true);
        Assert(!Field<bool>(n, "compact"), "unfocused note remains expanded while pointer is inside");
        Call(n, "UpdateCompactState", now, false, false);
        Call(n, "UpdateCompactState", now.AddMilliseconds(4900), false, false);
        Assert(!Field<bool>(n, "compact"), "both conditions must hold for full five seconds");
        Call(n, "UpdateCompactState", now.AddSeconds(5), false, false);
        var started = Field<DateTime>(n, "chromeAnimationStarted");
        Call(n, "AdvanceChromeAnimation", started.AddMilliseconds(350));
        float mid = Field<float>(n, "chromeFade");
        Assert(mid > 0.4f && mid < 0.6f, "hide uses gradual 700ms fade instead of an abrupt toggle");
        Call(n, "AdvanceChromeAnimation", started.AddSeconds(2));
        Assert(Field<float>(n, "chromeFade") == 0f && !Field<ToolIconButton>(n, "moreBtn").Visible &&
            !Field<ToolIconButton>(n, "button1").Visible, "both bars finish visually hidden");
        Assert(Field<Panel>(n, "chromePanel").Visible && Field<Panel>(n, "toolbarPanel").Visible &&
            Field<Panel>(n, "bodyPanel").Bounds == bodyBounds &&
            n.RectangleToClient(Body(n).RectangleToScreen(Body(n).ClientRectangle)) == textBounds,
            "hidden bars reserve their space and never move document text");
        Assert(Body(n).BackColor == NoteTheme.All[3].Body, "compact mode preserves independent theme");
        Call(Body(n), "OnMouseEnter", EventArgs.Empty);
        Call(Body(n), "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 10, 10, 0));
        Call(n, "OnActivated", EventArgs.Empty);
        Call(Body(n), "OnKeyDown", new KeyEventArgs(Keys.A));
        Assert(Field<bool>(n, "compact") && Field<float>(n, "chromeFade") == 0f,
            "hover, movement, keyboard focus and typing do not reveal hidden bars");
        using (var image = new Bitmap(n.Width, n.Height))
        {
            n.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(root, "compact-hidden.png"));
        }
        Call(Field<Control>(n, "titleOverlay"), "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0));
        started = Field<DateTime>(n, "chromeAnimationStarted");
        Assert(!Field<bool>(n, "compact") && Field<float>(n, "chromeFade") == 0f, "click begins reveal without instantly showing bars");
        Call(n, "AdvanceChromeAnimation", started.AddMilliseconds(350));
        Assert(Field<float>(n, "chromeFade") > 0.4f && Field<float>(n, "chromeFade") < 0.6f,
            "reveal fades progressively through intermediate opacity");
        using (var image = new Bitmap(n.Width, n.Height))
        {
            n.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(root, "compact-half.png"));
        }
        Call(n, "AdvanceChromeAnimation", started.AddSeconds(2));
        Assert(Field<float>(n, "chromeFade") == 1f && Field<ToolIconButton>(n, "moreBtn").Visible &&
            Field<ToolIconButton>(n, "button1").Visible, "click reveal restores live title and toolbar controls");
        Assert(Field<Panel>(n, "bodyPanel").Bounds == bodyBounds, "document bounds are stable throughout hide and reveal");
        using (var image = new Bitmap(n.Width, n.Height))
        {
            n.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(root, "compact-visible.png"));
        }
        // Returning to the note restarts the full outside countdown, even after a near-expired interval.
        Call(n, "UpdateCompactState", now, false, false);
        Call(n, "UpdateCompactState", now.AddSeconds(4), false, true);
        Call(n, "UpdateCompactState", now.AddSeconds(5), false, false);
        Call(n, "UpdateCompactState", now.AddSeconds(9), false, false);
        Assert(!Field<bool>(n, "compact"), "pointer re-entry resets the five-second countdown");
        Call(n, "Touch");
    }

    static void TestRichTextFormatting()
    {
        using (var n = new note())
        {
            n.Show();
            var body = Body(n);
            body.Text = "AABBBB";
            using (var first = new Font("Calibri", 10f, FontStyle.Italic)) { body.Select(0, 2); body.SelectionFont = first; }
            using (var second = new Font("Calibri", 14f)) { body.Select(2, 4); body.SelectionFont = second; }
            body.SelectAll();
            Call(n, "button1_Click", Field<ToolIconButton>(n, "button1"), EventArgs.Empty);
            body.Select(0, 1);
            using (var font = body.SelectionFont) Assert(font.Bold && font.Italic && font.Size == 10f, "bold preserves the first run's original font size and italic style");
            body.Select(3, 1);
            using (var font = body.SelectionFont) Assert(font.Bold && font.Size == 14f, "bold handles subsequent mixed-font runs without invalid native messages");
            body.SelectAll();
            Call(n, "button1_Click", Field<ToolIconButton>(n, "button1"), EventArgs.Empty);
            body.Select(3, 1);
            using (var font = body.SelectionFont) Assert(!font.Bold && font.Size == 14f, "bold toggle can restore mixed-format text safely");
            n.Close();
        }
    }

    static void TestEditingAndDisposal()
    {
        var n = new note();
        n.Show(); Application.DoEvents();
        var body = Body(n);
        body.Text = "before AFTER";
        body.Select(7, 5);
        using (var font = new Font("Calibri", 13f, FontStyle.Italic)) body.SelectionFont = font;
        Call(n, "InsertPlainText", "粘贴第一行\r\n第二行");
        int expected = body.TextLength;
        Assert(body.Text == "before 粘贴第一行\n第二行" && body.SelectionStart == expected && body.SelectionLength == 0,
            "plain text paste replaces the selection and leaves caret after normalized multiline content");
        using (var font = body.SelectionFont)
            Assert(font.Italic && Math.Abs(font.Size - 13f) < 0.1f, "pasted text and following typing preserve caret font");
        body.SelectedText = "!";
        Assert(body.Text.EndsWith("第二行!"), "typing after paste appends at its end rather than before it");
        body.Text = string.Join("\n", Enumerable.Range(0, 100).Select(i => "line " + i));
        body.Select(0, 0); body.ScrollToCaret();
        int first = body.GetCharIndexFromPosition(new Point(1, 1));
        var zero = new HandledMouseEventArgs(MouseButtons.None, 0, 1, 1, 0);
        Call(n, "RichTextBox1_MouseWheel", body, zero);
        Assert(body.GetCharIndexFromPosition(new Point(1, 1)) == first, "zero-delta wheel cannot move the note text");
        var handled = new HandledMouseEventArgs(MouseButtons.None, 0, 1, 1, -120) { Handled = true };
        Call(n, "RichTextBox1_MouseWheel", body, handled);
        Assert(body.GetCharIndexFromPosition(new Point(1, 1)) == first, "handled wheel events cannot scroll note text twice");
        var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 1, 1, -120);
        Call(n, "RichTextBox1_MouseWheel", body, wheel);
        Assert(wheel.Handled && body.GetCharIndexFromPosition(new Point(1, 1)) > first, "normal note wheel scrolls and suppresses duplicate native scrolling");
        Call(n, "SetCompact", true);
        n.Dispose(); Application.DoEvents();
        Assert(Field<bool>(n, "shutdownHandlersRemoved") && !Field<Timer>(n, "idleTimer").Enabled
            && !Field<Timer>(n, "chromeAnimationTimer").Enabled && !Field<Timer>(n, "autoSaveTimer").Enabled,
            "direct note disposal stops timers and unregisters shutdown and system event handlers");
        var changed = (Delegate)App.GetType("KimNotes.utils.TodoStore").GetField("Changed", Flags).GetValue(null);
        Assert(changed == null || !changed.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, n)),
            "disposed note is no longer retained by todo change notifications");
        n.Dispose();
        Assert(n.IsDisposed, "repeated note disposal is safe");
    }

    static void TestIdleThemeLifecycle(note a, note b)
    {
        var originalDefault = Static("KimNotes.utils.InitConfig", "GetConfigValue", "theme");
        Static("KimNotes.utils.InitConfig", "SetConfigValue", "theme", "7");
        var now = DateTime.UtcNow;
        var tick = typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic);
        // 240 thirty-second cycles model two hours of idle/save activity; no real-time sleep is used.
        for (int cycle = 0; cycle < 240; cycle++)
        {
            foreach (var n in new[] { a, b })
            {
                Call(n, "UpdateCompactState", now.AddSeconds(cycle * 30), false, false);
                Call(n, "AdvanceChromeAnimation", DateTime.UtcNow.AddSeconds(2));
                tick.Invoke(Field<System.Windows.Forms.Timer>(n, "autoSaveTimer"), new object[] { EventArgs.Empty });
                if (cycle % 30 == 0)
                {
                    Call(n, "OnSystemColorsChanged", EventArgs.Empty);
                    Call(n, "OnPowerModeChanged", null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume));
                    Call(n, "OnSessionSwitch", null, new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionUnlock));
                    Application.DoEvents();
                }
                int theme = n == a ? 3 : 2;
                if (Field<NoteTheme>(n, "currentTheme").Id != theme || Body(n).BackColor != NoteTheme.All[theme].Body ||
                    Field<ToolIconButton>(n, "button1").Theme.Id != theme)
                    throw new Exception("Theme drift in simulated idle cycle " + cycle);
            }
        }
        Assert(true, "two-hour simulated idle, autosave, unlock and resume cycles never mix independent themes");
        Assert(Field<NoteTheme>(a, "currentTheme").Id == 3 && Field<NoteTheme>(b, "currentTheme").Id == 2,
            "global default changes cannot recolor existing notes");
        Call(a, "RecreateHandle");
        Call(b, "RecreateHandle");
        Call(a, "OnDisplaySettingsChanged", null, EventArgs.Empty);
        Call(b, "OnDisplaySettingsChanged", null, EventArgs.Empty);
        Application.DoEvents();
        Assert(Body(a).BackColor == NoteTheme.All[3].Body && Body(b).BackColor == NoteTheme.All[2].Body,
            "window handle recreation and display change preserve independent themes");
        Body(a).BackColor = Color.White;
        Field<Panel>(a, "chromePanel").BackColor = Color.White;
        Call(a, "OnPowerModeChanged", null, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume));
        Application.DoEvents();
        Assert(Body(a).BackColor == NoteTheme.All[3].Body && Field<Panel>(a, "chromePanel").BackColor == NoteTheme.All[3].Chrome,
            "resume restores body and title palette without revealing hidden bars");
        Assert(Field<bool>(a, "compact"), "resume and unlock do not violate click-only reveal rule");
        Static("KimNotes.utils.InitConfig", "SetConfigValue", "theme", originalDefault);
        foreach (var n in new[] { a, b }) { Call(n, "Touch"); Call(n, "AdvanceChromeAnimation", DateTime.UtcNow.AddSeconds(2)); }
    }

    static void TestScreenshotSessions()
    {
        var helper = typeof(ScreenshotHelper);
        var create = helper.GetMethod("CreateScreenshotForm", Flags);
        var a = (Form)create.Invoke(null, new object[] { new Bitmap(40, 30), new Rectangle(0, 0, 40, 30) });
        var b = (Form)create.Invoke(null, new object[] { new Bitmap(40, 30), new Rectangle(50, 50, 40, 30) });
        string aDir = Path.Combine(root, "screens-a"), bDir = Path.Combine(root, "screens-b");
        a.Tag.GetType().GetProperty("ImagePath").SetValue(a.Tag, aDir, null);
        b.Tag.GetType().GetProperty("ImagePath").SetValue(b.Tag, bDir, null);
        a.Tag.GetType().GetProperty("Incognito").SetValue(a.Tag, false, null);
        b.Tag.GetType().GetProperty("Incognito").SetValue(b.Tag, true, null);
        a.Show(); b.Show();
        var doubleClick = typeof(Control).GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic);
        doubleClick.Invoke(a.Controls.OfType<PictureBox>().First(), new object[] { EventArgs.Empty });
        doubleClick.Invoke(b.Controls.OfType<PictureBox>().First(), new object[] { EventArgs.Empty });
        Assert(Directory.GetFiles(aDir, "*.png").Length == 1 && !Directory.Exists(bDir), "screenshot windows retain their own path and incognito flag");
        using (var destination = new note())
        {
            destination.Show();
            destination.SetText("Older note");
            var screenshot = (Form)create.Invoke(null, new object[] { new Bitmap(40, 30), new Rectangle(0, 0, 40, 30) });
            screenshot.Show();
            using (var latest = new note())
            {
                latest.Show();
                latest.SetText("Latest note\nBottom text");
                Body(latest).Select(0, 6);
                destination.Activate();
                int noteCount = Application.OpenForms.OfType<note>().Count();
                screenshot.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "插入便签").PerformClick();
                Assert(screenshot.IsDisposed && Body(latest).Rtf.Contains("\\pict") && !Body(destination).Rtf.Contains("\\pict"),
                    "screenshot resolves the most recently opened note when insertion is clicked");
                Assert(Body(latest).Text.StartsWith("Latest note\nBottom text\n", StringComparison.Ordinal),
                    "screenshot appends beneath all text without replacing the selection");
                Assert(Application.OpenForms.OfType<note>().Count() == noteCount,
                    "inserting into an open note never creates another note");
                latest.Close();
            }
            var secondScreenshot = (Form)create.Invoke(null, new object[] { new Bitmap(40, 30), new Rectangle(0, 0, 40, 30) });
            secondScreenshot.Show();
            secondScreenshot.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "插入便签").PerformClick();
            Assert(Body(destination).Rtf.Contains("\\pict") && Body(destination).Text.StartsWith("Older note\n", StringComparison.Ordinal),
                "closing the newest note routes insertion to the newest remaining open note");
            destination.Close();
        }
        var noNoteScreenshot = (Form)create.Invoke(null, new object[] { new Bitmap(40, 30), new Rectangle(0, 0, 40, 30) });
        noNoteScreenshot.Show();
        Assert(!Application.OpenForms.OfType<note>().Any(), "fallback test starts with no open notes");
        noNoteScreenshot.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "插入便签").PerformClick();
        var created = Application.OpenForms.OfType<note>().Single();
        Assert(noNoteScreenshot.IsDisposed && Body(created).Rtf.Contains("\\pict"),
            "when no notes are open insertion creates one new image note");
        created.Close();
    }

    static System.Collections.Generic.IEnumerable<Control> Descendants(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    static void TestMetadataRecovery()
    {
        string folder = (string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "notesPath");
        string name = "damaged-color-" + Guid.NewGuid().ToString("N") + ".rtf";
        string path = Path.Combine(folder, name);
        WriteTestNote(path, "正文完整，主题元数据损坏也应能打开。");
        File.WriteAllText(path + ".meta.json", "{broken-color-data");
        using (var form = new note(name))
        {
            form.Show(); Application.DoEvents();
            Assert(Body(form).Text.Contains("正文完整") && Body(form).BackColor == NoteTheme.Current().Body,
                "damaged color metadata falls back to a default theme without blocking intact note content");
            using (var list = new history(folder))
            {
                list.Show(); Application.DoEvents();
                var cards = (System.Collections.IEnumerable)Field<object>(list, "allCards");
                Assert(cards.Cast<object>().Any(card => (string)card.GetType().GetProperty("FileName").GetValue(card, null) == name),
                    "a note with damaged color metadata cannot break the history list");
                list.Close();
            }
            Assert(File.ReadAllText(path + ".meta.json") == "{broken-color-data"
                && Directory.GetFiles(folder, name + ".meta.json.damaged-*").Length == 1,
                "damaged metadata is preserved once without repeated archives on list refresh");
            ChooseTheme(form, 2);
            Call(form,"AutoSaveTick",DateTime.UtcNow.AddSeconds(2));
            Assert(File.ReadAllText(path + ".meta.json").Contains("2")
                && File.ReadAllText(Directory.GetFiles(folder, name + ".meta.json.damaged-*").Single()) == "{broken-color-data",
                "explicitly changing theme repairs metadata while retaining its damaged original");
            form.Close();
        }
    }

    static void TestSettingsLayout()
    {
        using (var settings = new KimNotes.settings.ConfigForm())
        {
            settings.Show(); Application.DoEvents();
            Assert(!Descendants(settings).Any(x => x.Text == "OCR / 翻译凭证"), "settings no longer exposes credential entry or editing controls");
            string[] keys = { "TRANSLATE_APP_ID", "TRANSLATE_SECRET", "OCR_API_KEY", "OCR_SECRET" };
            Assert(keys.All(key => !string.IsNullOrWhiteSpace((string)Static("KimNotes.utils.ServiceCredentials", "Get", key))),
                "packaged OCR and translation credentials load from embedded resource without user configuration");
            using (var bitmap = new Bitmap(settings.Width, settings.Height))
            {
                settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(root, "settings.png"));
            }
            settings.Close();
        }
    }

    static void TestSettingsResetAndRestart()
    {
        var config = App.GetType("KimNotes.utils.InitConfig");
        var snapshot = new System.Collections.Generic.Dictionary<string, string>((System.Collections.Generic.Dictionary<string, string>)config.GetField("configMap", Flags).GetValue(null));
        string custom = Path.Combine(root, "reset-custom-notes");
        Directory.CreateDirectory(custom);
        string name = "reset-probe-" + Guid.NewGuid().ToString("N") + ".rtf";
        string source = Path.Combine(custom, name);
        WriteTestNote(source, "恢复默认也要保留便签");
        File.WriteAllText(source + ".meta.json", "{\"ThemeId\":3}");
        try
        {
            Static("KimNotes.utils.InitConfig", "SetConfigValue", "notesPath", custom);
            using (var form = new KimNotes.settings.ConfigForm()) Call(form, "ResetData");
            string targetDir = (string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "notesPath");
            Assert(targetDir == Path.Combine(root, "notes") && File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(Path.Combine(targetDir, name)))
                && File.ReadAllText(source + ".meta.json") == File.ReadAllText(Path.Combine(targetDir, name) + ".meta.json"),
                "restoring defaults migrates custom-directory notes and their independent colors while preserving the source");
            Assert((string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "shortcutKey") == "F1",
                "restoring defaults updates the in-memory configuration as well as disk");
            File.WriteAllText(source, "conflicting contents");
            Static("KimNotes.utils.InitConfig", "SetConfigValue", "notesPath", custom);
            bool refused = false;
            using (var form = new KimNotes.settings.ConfigForm())
                try { Call(form, "ResetData"); } catch (TargetInvocationException ex) { refused = ex.InnerException is IOException; }
            Assert(refused && (string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "notesPath") == custom,
                "default reset refuses conflicting notes without changing the active directory");
        }
        finally { Static("KimNotes.utils.InitConfig", "SetConfigValues", snapshot); }
        bool signaled = false, failed = false;
        try { Static("KimNotes.MyApplicationContext", "RestartCore", (Action)(() => { throw new IOException("launch failed"); }), (Action)(() => signaled = true)); }
        catch (TargetInvocationException ex) { failed = ex.InnerException is IOException; }
        Assert(failed && !signaled, "failed restart launch never signals the running instance to exit");
        string order = "";
        Static("KimNotes.MyApplicationContext", "RestartCore", (Action)(() => order += "launch;"), (Action)(() => order += "signal;"));
        Assert(order == "launch;signal;", "restart launches the replacement before requesting the old instance to exit");
    }

    static void TestConfigRollbackOnFailedRestart()
    {
        const string type = "KimNotes.utils.InitConfig";
        var original = (System.Collections.Generic.Dictionary<string, string>)Static(type, "Snapshot");
        string sourceDir = Path.Combine(root, "restart-rollback-source");
        string targetDir = Path.Combine(root, "restart-rollback-target");
        string name = "rollback-" + Guid.NewGuid().ToString("N") + ".rtf";
        Directory.CreateDirectory(sourceDir);
        WriteTestNote(Path.Combine(sourceDir, name), "before failed restart");
        try
        {
            Static(type, "SetConfigValues", new System.Collections.Generic.Dictionary<string, string>
            {
                { "notesPath", sourceDir }, { "future-config-probe", "preserve complete map" }
            });
            var before = (System.Collections.Generic.Dictionary<string, string>)Static(type, "Snapshot");
            byte[] beforeFile = File.ReadAllBytes(Path.Combine(root, "config.txt"));
            using (var existing = new note(name))
            using (var settings = new KimNotes.settings.ConfigForm())
            {
                existing.Show(); settings.Show(); Application.DoEvents();
                Field<TextBox>(settings, "textBox3").Text = targetDir;
                bool failed = false;
                Action save = () =>
                {
                    Call(settings, "SaveData");
                    Static(type, "SetConfigValue", "rollback-only-probe", "must disappear");
                };
                try
                {
                    Static("KimNotes.settings.ConfigForm", "ApplyWithRestart", save,
                        (Action)(() => { throw new IOException("simulated replacement launch failure"); }));
                }
                catch (TargetInvocationException ex) { failed = ex.InnerException is IOException; }
                var restored = (System.Collections.Generic.Dictionary<string, string>)Static(type, "Snapshot");
                Assert(failed && restored.Count == before.Count && before.All(p => restored.ContainsKey(p.Key) && restored[p.Key] == p.Value)
                    && beforeFile.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "config.txt"))),
                    "failed configuration restart restores the complete previous map and on-disk configuration");
                Assert(File.Exists(Path.Combine(targetDir, name)) && File.Exists(Path.Combine(sourceDir, name)),
                    "restart rollback preserves both the original note and its already migrated copy");
                existing.SetText("edited after failed restart");
                Assert(Save(existing), "existing note can still save after configuration restart fails");
                using (var reopened = new note(name))
                    Assert(PathOf(reopened) == Path.Combine(sourceDir, name) && Body(reopened).Text == "edited after failed restart",
                        "notes opened after failed restart use the same directory and latest contents as existing windows");
                bool resetFailed = false;
                try
                {
                    Static("KimNotes.settings.ConfigForm", "ApplyWithRestart", (Action)(() => Call(settings, "ResetData")),
                        (Action)(() => { throw new IOException("simulated default-reset restart failure"); }));
                }
                catch (TargetInvocationException ex) { resetFailed = ex.InnerException is IOException; }
                Assert(resetFailed && (string)Static(type, "GetConfigValue", "notesPath") == sourceDir
                    && beforeFile.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "config.txt"))),
                    "failed restart after restoring defaults also returns to the original custom directory");
            }
            bool restarted = false;
            Static("KimNotes.settings.ConfigForm", "ApplyWithRestart",
                (Action)(() => Static(type, "SetConfigValue", "notesPath", targetDir)), (Action)(() => restarted = true));
            Assert(restarted && (string)Static(type, "GetConfigValue", "notesPath") == targetDir,
                "successful replacement launch keeps the newly committed configuration");
        }
        finally { Static(type, "Restore", original); }
    }

    static void TestHotkeyLifecycleAndStartup()
    {
        int registrations = 0, removals = 0;
        var active = new System.Collections.Generic.HashSet<IntPtr>();
        Func<IntPtr, bool> register = handle => { registrations++; return active.Add(handle); };
        Action<IntPtr> unregister = handle => { if (active.Remove(handle)) removals++; };
        var hotkey = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.HotKeyHandlerForm"), Flags, null, new object[] { register, unregister }, null);
        Assert(hotkey.IsHandleCreated && registrations == 1 && active.Count == 1, "hotkey registers on creation of its native window handle");
        Call(hotkey, "WndProc", Message.Create(hotkey.Handle, 0x0312, (IntPtr)1, IntPtr.Zero));
        Assert(Field<bool>(hotkey, "captureQueued") && !Field<bool>(hotkey, "isCapturing"),
            "hotkey capture is queued without starting desktop capture inside the native message handler");
        Call(hotkey, "RecreateHandle");
        Assert(registrations == 2 && removals == 1 && active.Count == 1,
            "native handle recreation re-registers screenshot hotkey and releases the old registration");
        Assert(!Field<bool>(hotkey, "captureQueued") && !Field<bool>(hotkey, "isCapturing"),
            "handle recreation cannot leave screenshot capture permanently locked by an obsolete queued callback");
        hotkey.Dispose(); hotkey.Dispose(); Application.DoEvents();
        Assert(!hotkey.IsHandleCreated && active.Count == 0 && removals == 2 && registrations == 2,
            "repeated hotkey disposal cannot recreate a handle or leave a registration alive");
        using (var recordingHotkey = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.HotKeyHandlerForm"), Flags, null, new object[] { register, unregister }, null))
        {
            int beforePause = registrations;
            Call(recordingHotkey, "SetSuspended", true);
            Call(recordingHotkey, "SetSuspended", true);
            Assert(active.Count == 0, "shortcut recording releases native hotkeys so Ctrl combinations can reach the settings window");
            Call(recordingHotkey, "WndProc", Message.Create(recordingHotkey.Handle, 0x0312, (IntPtr)1, IntPtr.Zero));
            Assert(!Field<bool>(recordingHotkey, "captureQueued"), "shortcut recording ignores stale screenshot hotkey messages");
            Call(recordingHotkey, "RecreateHandle");
            Assert(registrations == beforePause && active.Count == 0, "handle recreation during shortcut recording keeps hotkeys suspended");
            Call(recordingHotkey, "SetSuspended", false);
            Call(recordingHotkey, "SetSuspended", false);
            Assert(active.Count == 1 && registrations == beforePause + 1, "ending shortcut recording restores native hotkeys exactly once");
        }
        foreach (string path in new[] { @"C:\Program Files\小羊便签\KimNotes.exe", @"D:\note\KimNotes.exe" })
        {
            string command = (string)Static("KimNotes.MyApplicationContext", "StartupCommand", path);
            int count;
            IntPtr args = CommandLineToArgvW(command, out count);
            try
            {
                Assert(args != IntPtr.Zero && count == 1 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(args)) == path,
                    "Windows parses startup paths containing spaces and Chinese as one executable argument");
            }
            finally { if (args != IntPtr.Zero) LocalFree(args); }
        }
    }

    static void ResetStore()
    {
        var store = App.GetType("KimNotes.utils.TodoStore");
        store.GetField("items", Flags).SetValue(null, null);
        store.GetField("fired", Flags).SetValue(null, null);
        store.GetField("itemsDirty", Flags).SetValue(null, false);
        store.GetField("firedDirty", Flags).SetValue(null, false);
    }
    static object Todo(string method, params object[] args) => Static("KimNotes.utils.TodoStore", method, args);
    static void TestTodos()
    {
        var item = (KimNotes.utils.TodoItem)Todo("Add", "due", DateTime.Now.AddMinutes(5), null, null);
        Todo("SetDue", item, DateTime.Now.AddMinutes(-1));
        Assert(item.IsOverdue, "same-day missed reminder is overdue");
        var due = (System.Collections.IList)Todo("TakeDue");
        Assert(((System.Collections.IList)Todo("TakeDue")).Count == 1, "undisplayed reminder remains eligible for retry");
        Todo("MarkReminded", due);
        Assert(due.Count == 1 && ((System.Collections.IList)Todo("TakeDue")).Count == 0, "reminder fires once per scheduled time");
        Todo("SetDue", item, item.Due);
        Assert(((System.Collections.IList)Todo("TakeDue")).Count == 0, "saving an unchanged reminder time cannot re-fire an already shown reminder");
        ResetStore();
        Assert(((System.Collections.IList)Todo("TakeDue")).Count == 0, "reminder dedupe survives reload");
        var path = Path.Combine(root, "todos.json");
        File.WriteAllText(path, "{broken");
        ResetStore();
        Assert(Todo("Find", item.Id) != null, "corrupt todo file recovers from backup");
        Assert(!File.ReadAllText(path).Contains("{broken"), "recovered data replaces damaged main file before next write");
    }

    static void TestTodoPersistenceFailures()
    {
        var type = App.GetType("KimNotes.utils.TodoStore");
        string tasksPath = Path.Combine(root, "todos.json");
        string firedPath = Path.Combine(root, "reminders-fired.json");
        var item = (KimNotes.utils.TodoItem)Todo("Add", "save failure probe", null, null, null);
        byte[] previousFired = File.ReadAllBytes(firedPath);
        string held = tasksPath + ".held-" + Guid.NewGuid().ToString("N");
        File.Move(tasksPath, held);
        Directory.CreateDirectory(tasksPath);
        try
        {
            item.Done = true;
            Todo("MarkFired", item);
            type.GetField("itemsDirty", Flags).SetValue(null, true);
            bool failed = false;
            try { Todo("Persist"); } catch (TargetInvocationException) { failed = true; }
            Assert(failed && previousFired.SequenceEqual(File.ReadAllBytes(firedPath))
                && (bool)type.GetField("itemsDirty", Flags).GetValue(null),
                "failed task write cannot persist reminder dedupe ahead of unsaved contents");
            Assert(!(bool)Todo("TryFlush") && (bool)type.GetField("itemsDirty", Flags).GetValue(null)
                && previousFired.SequenceEqual(File.ReadAllBytes(firedPath)),
                "flush reports a blocked task write and retains both dirty state and the old dedupe file");
        }
        finally { Directory.Delete(tasksPath); File.Move(held, tasksPath); }
        Todo("TakeDue");
        Assert(!(bool)type.GetField("itemsDirty", Flags).GetValue(null)
            && !(bool)type.GetField("firedDirty", Flags).GetValue(null),
            "normal reminder scan automatically retries pending task and dedupe writes after storage recovers");
        ResetStore();
        item = (KimNotes.utils.TodoItem)Todo("Find", item.Id);
        Assert(item.Done, "automatic retry after a task write failure survives reloading the task store");
        Todo("TakeDue"); // Load the reminder cache before simulating a write-only failure.

        held = firedPath + ".held-" + Guid.NewGuid().ToString("N");
        File.Move(firedPath, held);
        Directory.CreateDirectory(firedPath);
        try
        {
            item.Done = false;
            item.Due = DateTime.Now.AddMinutes(-1);
            item.Text = "dedupe-write-failure-probe";
            Todo("MarkFired", item);
            type.GetField("itemsDirty", Flags).SetValue(null, true);
            bool failed = false;
            try { Todo("Persist"); } catch (TargetInvocationException) { failed = true; }
            Assert(failed && File.ReadAllText(tasksPath).Contains("dedupe-write-failure-probe")
                && !(bool)type.GetField("itemsDirty", Flags).GetValue(null) && (bool)type.GetField("firedDirty", Flags).GetValue(null),
                "dedupe write failure cannot prevent task contents from being saved");
            var due = ((System.Collections.IEnumerable)Todo("TakeDue")).Cast<KimNotes.utils.TodoItem>();
            Assert(!due.Any(t => t.Id == item.Id), "pending dedupe write failure does not repeat an already handled reminder in memory");
        }
        finally { Directory.Delete(firedPath); File.Move(held, firedPath); }
        Todo("TakeDue");
        Assert(!(bool)type.GetField("firedDirty", Flags).GetValue(null), "reminder scan retries pending dedupe persistence after storage recovers");
        ResetStore();
        Assert(!((System.Collections.IEnumerable)Todo("TakeDue")).Cast<KimNotes.utils.TodoItem>().Any(t => t.Id == item.Id),
            "recovered dedupe write remains effective after reloading the store");
        item = (KimNotes.utils.TodoItem)Todo("Find", item.Id);
        item.Text = "pending-change-before-exit";
        type.GetField("itemsDirty", Flags).SetValue(null, true);
        Assert((bool)Todo("TryFlush") && !(bool)type.GetField("itemsDirty", Flags).GetValue(null),
            "successful explicit flush persists pending changes before exit without another user edit");
        ResetStore();
        Assert(((KimNotes.utils.TodoItem)Todo("Find", item.Id)).Text == "pending-change-before-exit",
            "explicit flush retains pending task contents across a store reload");
    }

    static void TestReminderWindows()
    {
        var type = App.GetType("KimNotes.MyApplicationContext");
        var context = FormatterServices.GetUninitializedObject(type);
        var a = (KimNotes.utils.TodoItem)Todo("Add", "A", null, null, null);
        var b = (KimNotes.utils.TodoItem)Todo("Add", "B", null, null, null);
        Call(context, "ShowReminderPopup", "A", new int[] { a.Id });
        var popupA = Application.OpenForms.Cast<Form>().Last();
        Call(context, "ShowReminderPopup", "B", new int[] { b.Id });
        var popupB = Application.OpenForms.Cast<Form>().Last();
        popupA.Controls.OfType<Button>().First(x => x.Text == "完成").PerformClick();
        Assert(a.Done && !b.Done, "older reminder completes only its own tasks");
        popupB.Close();
        string longBody = string.Join("\n", Enumerable.Range(0, 100).Select(i => "· " + i + " 超长待办提醒需要可以滚动查看全部内容，按钮不能超出屏幕。"));
        Call(context, "ShowReminderPopup", longBody, new int[] { b.Id });
        var popup = Application.OpenForms.Cast<Form>().Last();
        Application.DoEvents();
        var scroll = popup.Controls.OfType<Panel>().First(p => p.Name == "reminderBody");
        Assert(Screen.FromControl(popup).WorkingArea.Contains(popup.Bounds)
            && popup.Controls.OfType<Button>().All(button => popup.ClientRectangle.Contains(button.Bounds)),
            "large reminder batches keep popup and action buttons inside the working area");
        var label = scroll.Controls.OfType<Label>().Single();
        Assert(scroll.AutoScroll && scroll.VerticalScroll.Visible && label.Text == longBody && label.Height > scroll.Height,
            "long reminders preserve all text in a scrollable body");
        scroll.AutoScrollPosition = new Point(0, label.Height);
        Assert(scroll.AutoScrollPosition.Y < 0, "last lines of a long reminder can be reached by scrolling");
        using (var bitmap = new Bitmap(popup.Width, popup.Height))
        {
            popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(root, "reminder-long.png"));
        }
        popup.Close();
    }

    static void TestIncognito()
    {
        Static("KimNotes.utils.InitConfig", "SetConfigValue", "checkBox3", "True");
        ResetStore();
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => File.ReadAllBytes(p));
        var n = new note(); n.Show(); n.SetText("private"); ChooseTheme(n, 4); Save(n); n.Close();
        var item = (KimNotes.utils.TodoItem)Todo("Add", "private todo", DateTime.Now.AddMinutes(2), null, null);
        Todo("SetDue", item, DateTime.Now.AddMinutes(-1)); Todo("MarkReminded", Todo("TakeDue"));
        var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert(after.Length == before.Count && after.All(p => before.ContainsKey(p) && before[p].SequenceEqual(File.ReadAllBytes(p))), "incognito notes, themes, todos and reminder dedupe do not write to disk");
    }
}
