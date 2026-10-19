using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;

internal static class ExperienceRegression
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly Assembly App = typeof(note).Assembly;
    static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    static object Static(string type, string name, params object[] args) => App.GetType(type).GetMethod(name, Flags).Invoke(null, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
    static string PathOf(note n) => (string)typeof(note).GetProperty("FilePath", Flags).GetValue(n, null);
    static RichTextBox Body(note n) => Field<RichTextBox>(n, "richTextBox1");

    internal static void Run(string root, Action<bool, string> check)
    {
        LayoutAndSave(root, check);
        Images(check);
        History(root, check);
        Repeats(check);
        Dialogs(root, check);
        HistoryTypography(root, check);
        ShortcutMigration(check);
    }

    static void LayoutAndSave(string root, Action<bool, string> check)
    {
        string path; Rectangle bounds;
        using (var n = new note())
        {
            n.Show(); n.SetText("布局与自动保存测试"); Application.DoEvents();
            var wa = Screen.FromControl(n).WorkingArea;
            n.Bounds = new Rectangle(wa.Left + 70, wa.Top + 50, 500, 560);
            n.TopMost = true;
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            path = PathOf(n); bounds = n.Bounds;
            check(File.Exists(path) && !Field<bool>(n, "hasUnsavedChanges") && Field<Label>(n, "saveStatus").Text == "已保存", "input pause saves the document and reports saved status");
            byte[] bytes = File.ReadAllBytes(path);
            int changed = 0;
            Action onChanged = () => changed++;
            var notesChanged = App.GetType("KimNotes.utils.Storage").GetEvent("NotesChanged", Flags);
            notesChanged.AddEventHandler(null, onChanged);
            n.Left += 12; Call(n, "SaveCurrentNoteSafe"); bounds = n.Bounds;
            notesChanged.RemoveEventHandler(null, onChanged);
            check(bytes.SequenceEqual(File.ReadAllBytes(path)), "layout-only persistence does not rewrite or replace the rich-text backup");
            check(changed == 0, "moving a saved note does not rebuild every open note-list preview");
            Call(n, "OnResizeBegin", EventArgs.Empty);
            n.SetText("拖动过程中延后保存");
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(10));
            check(Field<bool>(n, "hasUnsavedChanges"), "interactive window movement defers disk saves until the drag finishes");
            Call(n, "OnResizeEnd", EventArgs.Empty);
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            check(!Field<bool>(n, "hasUnsavedChanges"), "autosave resumes after interactive movement ends");
            n.Close();
        }
        using (var n = new note(Path.GetFileName(path)))
        {
            n.Show(); Application.DoEvents();
            check(n.Bounds == bounds && n.TopMost, "reopened note restores saved monitor position, size and pin state");
            n.SetText("停顿后更新正文");
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddMilliseconds(200));
            check(Field<bool>(n, "hasUnsavedChanges"), "autosave debounce does not write immediately on every keystroke");
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            check(!Field<bool>(n, "hasUnsavedChanges"), "autosave persists updated content after the idle interval");
            string held = path + ".autosave-held";
            File.Move(path, held); Directory.CreateDirectory(path);
            try
            {
                n.SetText("保存失败后仍保留的正文");
                Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
                check(Field<bool>(n, "hasUnsavedChanges") && Field<Label>(n, "saveStatus").Text == "保存失败",
                    "autosave failure retains unsaved contents and exposes a failure status");
            }
            finally { Directory.Delete(path); File.Move(held, path); }
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            check(!Field<bool>(n, "hasUnsavedChanges") && Field<Label>(n, "saveStatus").Text == "已保存", "autosave retries after storage becomes writable without requiring another edit");
            var session = (System.Collections.Generic.List<string>)Static("KimNotes.utils.DesktopSession", "Read");
            check(session.Contains(path), "desktop session records the saved open note for the next application startup");
            var type = App.GetType("KimNotes.utils.DesktopSession");
            type.GetField("Exiting", Flags).SetValue(null, true);
            n.Close();
            check(((System.Collections.Generic.List<string>)Static("KimNotes.utils.DesktopSession", "Read")).Contains(path), "application shutdown preserves open-note session instead of clearing it while closing windows");
            type.GetField("Exiting", Flags).SetValue(null, false);
        }
    }

    static void Images(Action<bool, string> check)
    {
        using (var n = new note())
        using (var source = new Bitmap(80, 60))
        {
            using (var g = Graphics.FromImage(source)) { g.Clear(Color.Coral); g.FillRectangle(Brushes.Navy, 0, 0, 20, 20); }
            n.Show(); n.SetText("图片之前\n");
            Call(n, "AppendImage", source);
            var editor = Body(n);
            editor.AppendText("\n图片之后");
            int index = Enumerable.Range(0, editor.TextLength).First(i => Static("KimNotes.utils.NoteImages", "SelectedPicture", editor, i) != null);
            var rtf = (string)Static("KimNotes.utils.NoteImages", "SelectedPicture", editor, index);
            using (var extracted = (Bitmap)Static("KimNotes.utils.NoteImages", "Extract", rtf))
            {
                Console.WriteLine("Embedded image extraction: " + extracted.Size + "; source=" + source.Size);
                check(extracted != null && extracted.Size == source.Size && extracted.GetPixel(40, 30).ToArgb() == Color.Coral.ToArgb()
                    && extracted.GetPixel(10, 10).ToArgb() == Color.Navy.ToArgb(), "embedded WMF picture extraction preserves original dimensions and distinct source pixels without a desktop-sized blank canvas");
                extracted.Save(Path.Combine(Path.GetDirectoryName(PathOf(n)), "extracted-picture.png"));
            }
            string text = editor.Text;
            check((bool)Static("KimNotes.utils.NoteImages", "Resize", editor, index, 0.75d) && editor.Text == text,
                "resizing one embedded picture preserves surrounding note text");
            editor.Select(index, 1); editor.SelectedText = "";
            check(editor.Text.Contains("图片之前") && editor.Text.Contains("图片之后") && !editor.Rtf.Contains("\\pict"), "deleting a selected picture preserves the text before and after it");
            n.Close();
        }
    }

    static void History(string root, Action<bool, string> check)
    {
        using (var a = new Bitmap(60, 50))
        using (var b = new Bitmap(70, 55))
        {
            using (var g = Graphics.FromImage(a)) g.Clear(Color.Red);
            using (var g = Graphics.FromImage(b)) g.Clear(Color.Blue);
            string first = (string)Static("KimNotes.utils.ScreenshotArchive", "Save", a, false, null);
            string second = (string)Static("KimNotes.utils.ScreenshotArchive", "Save", b, false, null);
            var viewer = ScreenshotHelper.RecallLatest(); Application.DoEvents();
            var picture = viewer.Controls.OfType<PictureBox>().Single();
            check(((Bitmap)picture.Image).GetPixel(20, 20).B == 255, "recall opens the newest retained screenshot without taking a new screenshot");
            var oldViewer = viewer;
            ScreenshotHelper.NavigateHistory(1);
            check(ReferenceEquals(oldViewer, viewer) && ((Bitmap)picture.Image).GetPixel(20, 20).R == 255, "previous screenshot replaces the image in the same floating window");
            ScreenshotHelper.NavigateHistory(-1);
            check(((Bitmap)picture.Image).GetPixel(20, 20).B == 255, "next screenshot returns to the newer retained image");
            viewer.Close();
            using (var hotkeys = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.HotKeyHandlerForm"), Flags, null,
                new object[] { new Func<IntPtr, bool>(h => true), new Action<IntPtr>(h => { }) }, null))
            {
                Call(hotkeys, "WndProc", Message.Create(hotkeys.Handle, 0x0312, (IntPtr)2, IntPtr.Zero));
                Application.DoEvents();
                viewer = Application.OpenForms.Cast<Form>().First(f => f.GetType().Name == "ScreenshotForm");
                check(((Bitmap)viewer.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).B == 255, "recall hotkey dispatch reopens retained screenshot without recapturing the desktop");
                Call(hotkeys, "WndProc", Message.Create(hotkeys.Handle, 0x0312, (IntPtr)3, IntPtr.Zero));
                Application.DoEvents();
                check(((Bitmap)viewer.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).R == 255, "previous hotkey dispatch navigates the existing screenshot window");
                viewer.Close();
            }
            viewer = ScreenshotHelper.RecallLatest(); Application.DoEvents();
            check(viewer != null && !viewer.IsDisposed && ((Bitmap)viewer.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).B == 255,
                "closing a floating screenshot keeps its pixels available for recall");
            check(viewer.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == "上一张") &&
                viewer.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == "下一张"), "floating screenshot right-click menu exposes previous and next navigation");
            check(!viewer.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text.Contains("唤回")), "screenshot right-click menu omits redundant recall action");
            viewer.Close();
            int before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
            string secret = (string)Static("KimNotes.utils.ScreenshotArchive", "Save", a, true, null);
            using (var restored = (Bitmap)Static("KimNotes.utils.ScreenshotArchive", "Load", secret, true))
                check(restored != null && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == before,
                    "incognito screenshot recall stores history only in memory");
            using (var green = new Bitmap(40, 40))
            using (var orange = new Bitmap(40, 40))
            {
                using (var g = Graphics.FromImage(green)) g.Clear(Color.Green);
                using (var g = Graphics.FromImage(orange)) g.Clear(Color.Orange);
                var earlier = (Form)Static("KimNotes.ScreenshotHelper", "CreateScreenshotForm", new Bitmap(green), new Rectangle(50, 50, 40, 40));
                var later = (Form)Static("KimNotes.ScreenshotHelper", "CreateScreenshotForm", new Bitmap(orange), new Rectangle(150, 50, 40, 40));
                foreach (var window in new[] { earlier, later })
                { window.Tag.GetType().GetProperty("AutoMenu").SetValue(window.Tag, false, null); window.Show(); Application.DoEvents(); }
                earlier.Activate(); Application.DoEvents();
                var changed = ScreenshotHelper.NavigateHistory(1);
                check(ReferenceEquals(changed, earlier) && ((Bitmap)earlier.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).B == 255,
                    "global navigation follows the last activated floating screenshot when multiple windows exist");
                earlier.Close(); later.Close();
            }
        }
    }

    static void Repeats(Action<bool, string> check)
    {
        var start = new DateTime(2030, 7, 1, 9, 0, 0);
        var item = (TodoItem)Static("KimNotes.utils.TodoStore", "Add", "每日重复测试", start, null, null);
        Static("KimNotes.utils.TodoStore", "SetSchedule", item, start, RepeatRule.Daily);
        var now = start.AddDays(3).AddHours(2);
        var due = (System.Collections.Generic.List<TodoItem>)Static("KimNotes.utils.TodoStore", "TakeDueAt", now);
        check(due.Count(t => t.Id == item.Id) == 1 && item.Due == start.AddDays(3), "missed daily occurrences coalesce into one latest reminder at the original clock time");
        Static("KimNotes.utils.TodoStore", "MarkReminded", due);
        var storeType = App.GetType("KimNotes.utils.TodoStore");
        foreach (string field in new[] { "items", "fired" }) storeType.GetField(field, Flags).SetValue(null, null);
        item = (TodoItem)Static("KimNotes.utils.TodoStore", "Find", item.Id);
        check(item.Repeat == RepeatRule.Daily && item.Due == start.AddDays(3), "repeat rule and normalized occurrence survive a store reload");
        check(!((System.Collections.Generic.List<TodoItem>)Static("KimNotes.utils.TodoStore", "TakeDueAt", now)).Any(t => t.Id == item.Id), "repeat scanning cannot show the same occurrence twice");
        check(((System.Collections.Generic.List<TodoItem>)Static("KimNotes.utils.TodoStore", "TakeDueAt", now.AddDays(1))).Any(t => t.Id == item.Id), "the next daily occurrence is eligible even when the previous task was not completed");
        var friday = new DateTime(2026, 10, 16, 9, 0, 0);
        check((DateTime)Static("KimNotes.utils.TodoStore", "NextOccurrence", friday, RepeatRule.Weekdays, friday.AddHours(2)) == friday.AddDays(3),
            "weekday recurrence skips Saturday and Sunday while preserving the reminder clock time");
        check((DateTime)Static("KimNotes.utils.TodoStore", "NextOccurrence", friday, RepeatRule.Weekly, friday.AddDays(20)) == friday.AddDays(21),
            "weekly recurrence remains anchored to the chosen weekday after a long idle gap");
        var previous = item.Due;
        Static("KimNotes.utils.TodoStore", "SetDone", item, true);
        check(!item.Done && item.Due > previous, "completing a recurring task schedules its next occurrence and keeps the series active");
        Static("KimNotes.utils.TodoStore", "SetSchedule", item, null, RepeatRule.Daily);
        check(item.Repeat == RepeatRule.None && !item.Due.HasValue, "disabling reminders also disables recurrence");
    }

    static void Dialogs(string root, Action<bool, string> check)
    {
        using (var dialog = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.TodoEditDialog"), new object[] { "工作日检查项目进展", false }))
        {
            dialog.Show(); Application.DoEvents();
            Call(dialog, "SetRepeat", RepeatRule.Weekdays);
            var selector = Field<Button>(dialog, "repeatBox");
            check(selector.Text.Contains("工作日") && dialog.ClientRectangle.Contains(selector.Bounds) && selector.Bottom < Field<Button>(dialog, "btnOk").Top,
                "recurrence selector is visible inside the todo dialog without overlapping its actions");
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "todo-repeat.png")); }
            dialog.Close();
        }
        using (var settings = (Form)Activator.CreateInstance(App.GetType("KimNotes.settings.ConfigForm")))
        {
            settings.Show(); Application.DoEvents();
            var values = Field<System.Collections.Generic.Dictionary<string, string>>(settings, "historyKeyValues");
            var buttons = Field<System.Collections.Generic.Dictionary<string, Button>>(settings, "historyKeyButtons");
            var body = Field<Panel>(settings, "bodyPanel");
            var path = Field<TextBox>(settings, "textBox3");
            var capture = Field<Button>(settings, "keycapBtn");
            check(!body.VerticalScroll.Visible && !body.HorizontalScroll.Visible && path.Parent.Width == capture.Width
                && buttons.Values.All(b => b.Width == capture.Width) && path.Font.Size <= 8.5f,
                "compact settings fit without default scrollbars and align path and shortcut field widths");
            check(values.Count == 3 && buttons.Count == 3 && buttons.Values.All(b => b.Width > 100 && b.Parent.ClientRectangle.Contains(b.Bounds)),
                "settings expose all three configurable screenshot history shortcuts without clipping");
            check(buttons.Values.All(b => b.Text.StartsWith("Ctrl + ") && !b.Text.Contains("Alt")) &&
                Field<Label>(settings, "hintLabel").Bottom < Field<Button>(settings, "keycapBtn").Top,
                "Ctrl-only keycaps use a compact shared hint beside the shortcut section heading");
            var modifiers = (uint)App.GetType("KimNotes.utils.HotKeyHandlerForm").GetField("HistoryModifiers", Flags).GetRawConstantValue();
            check((modifiers & 3) == 2 && (modifiers & 0x4000) != 0, "native screenshot-history registration uses Ctrl only and suppresses key-repeat events");
            string captureKey = Field<TextBox>(settings, "textBox1").Text;
            buttons["recallKey"].PerformClick();
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs((Keys)Enum.Parse(typeof(Keys), captureKey)));
            check(Field<string>(settings, "historyCapturing") == "recallKey" && Field<Label>(settings, "hintLabel").Text.Contains("重复"),
                "history shortcuts reject a collision with the screenshot shortcut before saving");
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.F8));
            Call(settings, "BeginCapture");
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.F8));
            check(Field<TextBox>(settings, "textBox1").Text == captureKey && Field<bool>(settings, "capturing"),
                "screenshot shortcut capture also rejects a collision with a history shortcut");
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.Escape));
            buttons["recallKey"].PerformClick();
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.F2));
            buttons["previousImageKey"].PerformClick();
            buttons["previousImageKey"].Focus();
            bool consumed = (bool)Call(buttons["previousImageKey"], "PreProcessMessage",
                Message.Create(buttons["previousImageKey"].Handle, 0x100, (IntPtr)Keys.Left, IntPtr.Zero));
            check(consumed && values["previousImageKey"] == "Left" && Field<string>(settings, "historyCapturing") == null
                && buttons["previousImageKey"].Text.Contains("←"),
                "focused shortcut button captures Left through real WinForms preprocessing instead of moving focus");
            buttons["nextImageKey"].PerformClick();
            consumed = (bool)Call(settings, "ProcessCmdKey", Message.Create(settings.Handle, 0x100, (IntPtr)Keys.Right, IntPtr.Zero), Keys.Control | Keys.Right);
            check(consumed && values["nextImageKey"] == "Right" && Field<string>(settings, "historyCapturing") == null,
                "shortcut recording also accepts Ctrl+Right through command-key preprocessing");
            buttons["previousImageKey"].PerformClick();
            Call(settings, "ProcessCmdKey", Message.Create(settings.Handle, 0x100, (IntPtr)Keys.Right, IntPtr.Zero), Keys.Right);
            check(values["previousImageKey"] == "Left" && Field<string>(settings, "historyCapturing") == "previousImageKey"
                && Field<Label>(settings, "hintLabel").Text.Contains("重复"),
                "navigation-key recording rejects conflicts without ending capture or changing the old value");
            Call(settings, "ProcessCmdKey", Message.Create(settings.Handle, 0x100, (IntPtr)Keys.Escape, IntPtr.Zero), Keys.Escape);
            check(Field<string>(settings, "historyCapturing") == null && values["previousImageKey"] == "Left",
                "Escape cancels navigation-key capture and preserves its previous shortcut");
            using (var bitmap = new Bitmap(settings.Width, settings.Height))
            { settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "settings-history-shortcuts.png")); }
            settings.Close();
        }
    }

    static void ShortcutMigration(Action<bool, string> check)
    {
        var configType = App.GetType("KimNotes.utils.InitConfig");
        var field = configType.GetField("configMap", Flags);
        var original = field.GetValue(null);
        var legacy = new System.Collections.Generic.Dictionary<string, string>((System.Collections.Generic.Dictionary<string, string>)original);
        try
        {
            legacy["pasteShortcutVersion"] = "2"; legacy["previousImageKey"] = "F3"; legacy["nextImageKey"] = "F4";
            field.SetValue(null, legacy);
            check((string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "previousImageKey") == "Left" &&
                (string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "nextImageKey") == "Right", "previous release navigation defaults migrate to the requested Ctrl+Left/Right");
            legacy["previousImageKey"] = "G";
            check((string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "previousImageKey") == "G", "shortcut migration preserves custom key choices");
            legacy["pasteShortcutVersion"] = "3"; legacy["previousImageKey"] = "F3";
            check((string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "previousImageKey") == "F3", "explicit function-key choices remain available after migration");
        }
        finally { field.SetValue(null, original); }
    }

    static void HistoryTypography(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "history-fonts"); Directory.CreateDirectory(folder);
        using (var editor = new RichTextBox())
        {
            var handle = editor.Handle;
            editor.Text = "便签列表内容\n小字号预览，保持紧凑";
            editor.SaveFile(Path.Combine(folder, "font.rtf"));
        }
        using (var history = new history(folder))
        {
            history.Show(); Application.DoEvents();
            bool fonts = true, fit = true;
            for (int round = 0; round < 4; round++)
            foreach (var screen in Screen.AllScreens)
            {
                history.Location = new Point(screen.WorkingArea.Left + 24, screen.WorkingArea.Top + 24);
                Application.DoEvents();
                var cards = Field<Panel>(history, "panel1").Controls.OfType<Panel>().ToArray();
                foreach (var card in cards)
                {
                    var preview = card.Controls.OfType<RichTextBox>().Single();
                    var date = card.Controls.OfType<Label>().Single();
                    fonts &= preview.Text.Contains("便签列表内容") && Math.Abs(preview.Font.SizeInPoints - 9f) < 0.05 && Math.Abs(date.Font.SizeInPoints - 8f) < 0.05;
                    int last = preview.GetFirstCharIndexFromLine(preview.Lines.Length - 1);
                    fit &= preview.GetPositionFromCharIndex(last).Y + preview.Font.Height <= preview.ClientSize.Height + 2;
                }
                if (round == 0) using (var bitmap = new Bitmap(history.Width, history.Height))
                { history.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "history-font-" + history.DeviceDpi + ".png")); }
            }
            check(fonts, "history preview stays at 9pt with 8pt dates across repeated real monitor moves instead of applying DPI twice");
            check(fit, "compact history preview height still fits all displayed lines after DPI transitions");
            history.Close();
        }
    }
}
