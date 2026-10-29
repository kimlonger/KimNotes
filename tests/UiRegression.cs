using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using KimNotes;

internal static class UiRegression
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Assembly App = typeof(note).Assembly;
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
    static object Store(string method, params object[] args) => App.GetType("KimNotes.utils.TodoStore").GetMethod(method, Flags).Invoke(null, args);

    public static void Run(string root, Action<bool, string> check)
    {
        Icons(root, check);
        NoteToolbar(root, check);
        CrossMonitorDpi(root, check);
        DragAcrossMonitors(check);
        History(root, check);
        Dialog(root, check);
        List(root, check);
        Tray(root, check);
    }

    static void Tray(string root, Action<bool, string> check)
    {
        var context = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(App.GetType("KimNotes.MyApplicationContext"));
        using (var menu = (ContextMenuStrip)Call(context, "CreateTrayMenu"))
        {
            var labels = menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToArray();
            check(labels.SequenceEqual(new[] { "新建便签", "便签列表", "待办清单", "唤回最近贴图", "上一张贴图", "下一张贴图", "设置", "账号", "退出" })
                && menu.Items.OfType<ToolStripSeparator>().Count() == 2,
                "compact native tray menu retains every existing action with two simple separators");
            foreach (var screen in Screen.AllScreens)
            {
                var wa = screen.WorkingArea;
                menu.Show(new Point(wa.Right - 8, wa.Bottom - 8), ToolStripDropDownDirection.AboveLeft);
                Application.DoEvents();
                var factor = (float)App.GetType("KimNotes.utils.UiDpi").GetMethod("FactorForScreen", Flags).Invoke(null, new object[] { screen });
                check(wa.Contains(menu.Bounds) && menu.Width < 220 * factor && menu.Height < 280 * factor
                    && menu.Items.OfType<ToolStripMenuItem>().All(i => i.Height >= i.Font.Height + 2),
                    "simple tray menu stays compact, readable and on screen at " + screen.DeviceName);
                using (var preview = new Bitmap(menu.Width, menu.Height))
                { menu.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size)); preview.Save(Path.Combine(root, "tray-menu-compact-" + (int)(factor * 100) + ".png")); }
                menu.Close(); Application.DoEvents();
            }
        }
    }

    static void History(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "history-boundaries");
        Directory.CreateDirectory(folder);
        using (var editor = new RichTextBox())
        {
            var handle = editor.Handle;
            for (int i = 0; i < 35; i++)
            {
                editor.Text = "audit-task " + i + "\n便签列表刷新和搜索位置检查";
                editor.SaveFile(Path.Combine(folder, i.ToString("00") + ".rtf"));
            }
        }
        var form = new history(folder);
        form.Show(); HistoryLoaded(form);
        var panel = Field<Panel>(form, "panel1");
        var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120);
        Call(form, "GlobalMouseWheel", panel, wheel);
        int offset = Field<int>(form, "scrollOffset");
        Call(form, "GlobalMouseWheel", form, wheel);
        check(offset < 0 && Field<int>(form, "scrollOffset") == offset && wheel.Handled,
            "history wheel bubbling cannot scroll twice");
        Call(form, "GlobalMouseWheel", panel, new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 0));
        check(Field<int>(form, "scrollOffset") == offset, "zero-delta history wheel does not move the list");
        Call(form, "RefreshCardList");
        HistoryLoaded(form);
        check(Field<int>(form, "scrollOffset") == offset, "history refresh preserves the current reading position");
        form.GetType().GetField("scrollOffset", Flags).SetValue(form, -100000);
        form.ClientSize = new Size(form.ClientSize.Width + 50, form.ClientSize.Height + 100);
        Application.DoEvents();
        int minimum = Math.Min(0, panel.ClientSize.Height - Field<int>(form, "contentHeight"));
        check(Field<int>(form, "scrollOffset") >= minimum && Field<int>(form, "scrollOffset") <= 0
            && panel.Controls.Cast<Control>().Any(c => c.Visible),
            "resizing history clamps its scroll offset and cannot leave an empty viewport past the last card");
        var search = Field<SearchBox>(form, "searchBox");
        search.Text = "no-match-boundary";
        Call(form, "ApplySearchFilter");
        check(Field<int>(form, "scrollOffset") == 0 && !panel.Controls.Cast<Control>().Any(c => c.Visible),
            "history search resets to the top and renders an empty result safely");
        search.Text = "audit-task";
        Call(form, "ApplySearchFilter");
        check(panel.Controls.Cast<Control>().Any(c => c.Visible), "clearing a history filter restores visible cards");
        form.Dispose(); Application.DoEvents();
        var changed = (Delegate)App.GetType("KimNotes.utils.Storage").GetField("NotesChanged", Flags).GetValue(null);
        check(Field<bool>(form, "resourcesReleased") && !Field<Timer>(form, "refreshTimer").Enabled
            && !Field<Timer>(form, "searchDebounceTimer").Enabled
            && (changed == null || !changed.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, form))),
            "disposing history stops refresh timers and removes its static event subscription");
        form.Dispose();
    }

    static void HistoryLoaded(history form)
    {
        var until=DateTime.UtcNow.AddSeconds(30);
        do {Application.DoEvents();System.Threading.Thread.Sleep(2);if(DateTime.UtcNow>until)throw new Exception("history background loading timed out");}
        while(Field<bool>(form,"previewLoading") || Field<Timer>(form,"refreshTimer").Enabled);
    }

    static void NoteToolbar(string root, Action<bool, string> check)
    {
        using (var form = new note())
        {
            form.SetText("横向拉伸后，工具栏仍紧凑排列在便签左下角。");
            form.Show(); Application.DoEvents();
            var bar = Field<Panel>(form, "toolbarPanel");
            var overlay = Field<Control>(form, "toolsOverlay");
            int needed = (int)Call(form, "ToolbarNeededWidth");
            int[] positions=null;
            foreach (int width in new[] { 760, 360, 900, 420 })
            {
                form.ClientSize = new Size(width, 440); Application.DoEvents();
                var buttons = bar.Controls.OfType<ToolIconButton>().OrderBy(b => b.Left).ToArray();
                check(buttons.Length == 8 && buttons[0].Left > 0 && buttons[0].Left < bar.Width / 8
                    && buttons.Last().Right < bar.Width && buttons.All(b => b.Top >= 2 && b.Bottom < bar.Height)
                    && buttons.Zip(buttons.Skip(1), (a, b) => b.Left >= a.Right + 2 && b.Left-a.Right<=10).All(b => b)
                    && (positions==null || positions.SequenceEqual(buttons.Select(b=>b.Left))),
                    "toolbar keeps fixed left-aligned positions and compact gaps after resize to " + width);
                positions=buttons.Select(b=>b.Left).ToArray();
                check((int)Call(form, "ToolbarNeededWidth") == needed && overlay.Dock == DockStyle.Fill,
                    "toolbar sizing excludes fade overlay and leaves its docked bounds intact");
            }
            Call(form, "SetCompact", true);
            Call(form, "AdvanceChromeAnimation", Field<DateTime>(form, "chromeAnimationStarted").AddMilliseconds(350));
            form.ClientSize = new Size(760, 440); Application.DoEvents();
            Call(form, "AdvanceChromeAnimation", Field<DateTime>(form, "chromeAnimationStarted").AddSeconds(2));
            check(!bar.Controls.OfType<ToolIconButton>().Any(b => b.Visible) && overlay.Bounds == bar.ClientRectangle,
                "resizing during fade preserves hidden toolbar and full-width overlay");
            Call(form, "Touch");
            Call(form, "AdvanceChromeAnimation", Field<DateTime>(form, "chromeAnimationStarted").AddSeconds(2));
            check(bar.Controls.OfType<ToolIconButton>().All(b => b.Visible && b.Right < bar.Width),
                "click reveal after resize restores all toolbar buttons inside the bar");
            using (var bitmap = new Bitmap(bar.Width, bar.Height))
            {
                bar.DrawToBitmap(bitmap, bar.ClientRectangle);
                var color = Field<Color>(form, "toolbarLineColor");
                check(Enumerable.Range(0, bar.Width).All(x => bitmap.GetPixel(x, 0).ToArgb() == color.ToArgb()),
                    "toolbar separator renders continuously across the full resized width");
            }
            Snapshot(form, Path.Combine(root, "note-wide-toolbar.png"));
            form.Close();
        }
    }

    static void CrossMonitorDpi(string root, Action<bool, string> check)
    {
        using (var form = new note())
        {
            form.ShowInTaskbar = false;
            form.TopMost = false;
            form.SetText("跨屏便签测试\n主屏与副屏反复移动\n正文、配色和精简模式都应保持一致。");
            form.ApplyTheme(NoteTheme.All[0]);
            form.Show(); Application.DoEvents();
            check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(form.Handle), (IntPtr)(-4)),
                "application startup really creates a PerMonitorV2 window with packaged DPI configuration");
            var screens = Screen.AllScreens;
            var body = Field<RichTextBox>(form, "richTextBox1");
            var bar = Field<Panel>(form, "toolbarPanel");
            var chrome = Field<Panel>(form, "chromePanel");
            var overlay = Field<Control>(form, "toolsOverlay");
            var theme = NoteTheme.All[0];
            int dpiEvents = 0;
            form.DpiChanged += (s, e) => dpiEvents++;
            var observedDpis = new System.Collections.Generic.HashSet<int>();
            string text = body.Text;
            body.Select(0, 4);
            using (var emphasis = new Font(body.Font.FontFamily, 15f, FontStyle.Bold | FontStyle.Italic))
                body.SelectionFont = emphasis;
            body.SelectionColor = Color.DarkRed;
            var formatting = ReadCharacterFormatting(body);
            bool geometry = true, colors = true, hidden = true, textStable = true, frames = true;
            for (int round = 0; round < 12; round++)
            foreach (var screen in screens)
            {
                bool compact = round % 3 == 1;
                Call(form, "SetCompact", compact);
                Call(form, "AdvanceChromeAnimation", Field<DateTime>(form, "chromeAnimationStarted").AddSeconds(2));
                form.Location = new Point(screen.WorkingArea.Left + 32, screen.WorkingArea.Top + 32);
                Application.DoEvents();
                int dpi = (int)GetDpiForWindow(form.Handle);
                observedDpis.Add(dpi);
                int expectedHeight = (int)Math.Round(38 * dpi / 96f);
                var buttons = bar.Controls.OfType<ToolIconButton>().ToArray();
                geometry &= Field<int>(form, "layoutDpi") == dpi && bar.Height == expectedHeight
                    && chrome.Height == (int)Math.Round(36 * dpi / 96f)
                    && buttons.All(b => b.Width == (int)Math.Round(30 * dpi / 96f) && b.IconSize == 20 && b.Top >= 2 && b.Bottom < bar.Height && b.Right < bar.Width);
                colors &= bar.BackColor == theme.Body && body.BackColor == theme.Body
                    && buttons.All(b => b.BackColor == theme.Body && b.Theme == theme);
                hidden &= Field<bool>(form, "compact") == compact && overlay.Visible == compact
                    && buttons.All(b => b.Visible == !compact)
                    && (!compact || overlay.Bounds == bar.ClientRectangle);
                textStable &= body.Text == text;
                using (var bitmap = new Bitmap(bar.Width, bar.Height))
                {
                    bar.DrawToBitmap(bitmap, bar.ClientRectangle);
                    frames &= bitmap.GetPixel(1, bar.Height - 2).ToArgb() == theme.Body.ToArgb();
                    foreach (var button in buttons)
                        frames &= bitmap.GetPixel(button.Left, Math.Max(3, button.Top)).ToArgb() == theme.Body.ToArgb();
                }
                if (round == 0 || round == 1)
                    Snapshot(form, Path.Combine(root, "note-dpi-" + dpi + (compact ? "-hidden.png" : "-visible.png")));
            }
            check(geometry, "repeated real monitor moves keep title, toolbar and button geometry aligned to the current DPI");
            check(colors && frames, "repeated real monitor moves render the toolbar and icon backgrounds without white blocks");
            check(hidden, "monitor moves preserve click-only compact state and full-width opaque overlays");
            check(textStable, "monitor moves never alter note text");
            if (screens.Length > 1)
            {
                Console.WriteLine("Cross-monitor DPI evidence: " + string.Join(", ", observedDpis.OrderBy(d => d)) + "; notifications=" + dpiEvents);
                check(observedDpis.Count == 1 || dpiEvents >= 20, "mixed-DPI monitor stress receives native DPI notifications on repeated transitions");
            }
            // Return to the initial monitor before comparing logical rich-text formatting.
            form.Location = new Point(screens[0].WorkingArea.Left + 32, screens[0].WorkingArea.Top + 32);
            Application.DoEvents();
            // RichEdit can normalize RTF font-family classification (fswiss to fnil)
            // without changing the document. Compare every character's actual formatting.
            var afterFormatting = ReadCharacterFormatting(body);
            for (int i = 0; i < formatting.Length; i++)
                if (formatting[i] != afterFormatting[i]) Console.WriteLine("DPI format difference " + i + ": " + formatting[i] + " -> " + afterFormatting[i]);
            check(afterFormatting.SequenceEqual(formatting), "DPI round trips preserve mixed rich-text fonts, sizes, styles and colors");
            form.Close();
        }
    }

    static string[] ReadCharacterFormatting(RichTextBox body)
    {
        int start = body.SelectionStart, length = body.SelectionLength;
        var result = new string[body.TextLength];
        for (int i = 0; i < result.Length; i++)
        {
            body.Select(i, 1);
            using (var font = body.SelectionFont)
                result[i] = font.Name + "|" + font.SizeInPoints + "|" + font.Style + "|" + body.SelectionColor.ToArgb();
        }
        body.Select(start, length);
        return result;
    }

    static void WindowMessage(Form form, int id)
    {
        Call(form, "WndProc", Message.Create(form.Handle, id, IntPtr.Zero, IntPtr.Zero));
    }

    static void DragAcrossMonitors(Action<bool, string> check)
    {
        using (var form = new note())
        {
            form.TopMost = false;
            form.SetText("跨屏拖动仍保留格式\n正文和图片位置不跳动。");
            form.Show(); Application.DoEvents();
            var screens = Screen.AllScreens;
            if (screens.Length < 2) { form.Close(); return; }
            var body = Field<RichTextBox>(form, "richTextBox1");
            body.Select(0, 4);
            using (var font = new Font(body.Font.FontFamily, 16f, FontStyle.Bold | FontStyle.Italic)) body.SelectionFont = font;
            body.SelectionColor = Color.DarkGreen;
            body.Select(body.TextLength, 0); body.SelectedText = "尾";
            string text = body.Text;
            var formatting = ReadCharacterFormatting(body);
            int events = 0;
            form.DpiChanged += (s, e) => events++;
            bool steady = true, dropped = true, palette = true, formats = true;
            for (int round = 0; round < 4; round++)
            {
                var start = screens[round % screens.Length];
                var target = screens[(round + 1) % screens.Length];
                form.Location = new Point(start.WorkingArea.Left + 60, start.WorkingArea.Top + 60);
                Application.DoEvents();
                bool compact = round % 2 == 1;
                Call(form, "SetCompact", compact);
                Call(form, "AdvanceChromeAnimation", Field<DateTime>(form, "chromeAnimationStarted").AddSeconds(2));
                int beforeEvents = events;
                int initialDpi = form.DeviceDpi;
                var initialSize = form.Size;
                WindowMessage(form, 0x0231); // WM_ENTERSIZEMOVE
                WindowMessage(form, 0x0216); // WM_MOVING
                foreach (var screen in new[] { target, start, target })
                {
                    var destination = new Point(screen.WorkingArea.Left + 60, screen.WorkingArea.Top + 60);
                    form.Location = destination;
                    Application.DoEvents();
                    steady &= form.Location == destination && form.Size == initialSize && events == beforeEvents
                        && Field<bool>(form, "interactingWithBounds") && body.Text == text;
                }
                var drop = form.Location;
                int nativeDpi = (int)GetDpiForWindow(form.Handle);
                WindowMessage(form, 0x0232); // WM_EXITSIZEMOVE
                Application.DoEvents();
                var bar = Field<Panel>(form, "toolbarPanel");
                var theme = Field<NoteTheme>(form, "currentTheme");
                dropped &= form.DeviceDpi == nativeDpi && Field<int>(form, "layoutDpi") == nativeDpi
                    && form.Location == drop && !Field<bool>(form, "interactingWithBounds")
                    && Math.Abs(form.Width - initialSize.Width * nativeDpi / (double)initialDpi) < 3
                    && Math.Abs(form.Height - initialSize.Height * nativeDpi / (double)initialDpi) < 3
                    && events - beforeEvents == (nativeDpi == initialDpi ? 0 : 1);
                palette &= Field<bool>(form, "compact") == compact
                    && bar.BackColor == theme.Body && body.BackColor == theme.Body
                    && bar.Height == (int)Math.Round(38 * nativeDpi / 96f)
                    && bar.Controls.OfType<ToolIconButton>().All(button => button.Visible == !compact && button.BackColor == theme.Body);
                formats &= ReadCharacterFormatting(body).SequenceEqual(formatting);
            }
            check(steady, "live cross-monitor dragging keeps the window under the requested position without mid-drag resizing or full DPI layout");
            check(dropped, "dropping applies the final monitor DPI once at the drop point with the original logical size");
            check(palette, "deferred drag DPI preserves per-note colors, compact state and toolbar geometry");
            check(formats, "repeated live DPI crossings preserve every character's font, style, size and color");
            check(body.CanUndo, "cross-monitor dragging retains the native rich-text undo history");
            body.Undo();
            check(body.Text == text.Substring(0, text.Length - 1), "undo after a monitor drag still reverses the user's last text edit");
            form.Close();
        }
    }

    static void Icons(string root, Action<bool, string> check)
    {
        string[] ids = { "bullet", "bold", "strike", "translate", "add", "pin", "scissors", "notes", "todo", "rect", "arrow", "mosaic", "text", "undo", "check", "search", "more", "close", "gear", "calendar", "clock", "copy", "save", "ocr", "eyedropper" };
        using (var sheet = new Bitmap(ids.Length * 64, 340))
        using (var g = Graphics.FromImage(sheet))
        using (var font = new Font("Segoe UI", 8f))
        {
            g.Clear(Color.White);
            for (int row = 0; row < 4; row++)
            {
                int size = new[] { 32, 40, 48, 64 }[row];
                bool valid = true;
                for (int col = 0; col < ids.Length; col++)
                using (var button = new ToolIconButton { IconId = ids[col], Size = new Size(size, size), BackColor = Color.White })
                using (var bitmap = new Bitmap(size, size))
                {
                    button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    int count = 0;
                    for (int x = 0; x < size; x++) for (int y = 0; y < size; y++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.R < 220 && pixel.G < 220 && pixel.B < 220)
                        {
                            count++;
                            if (x <= 1 || y <= 1 || x >= size - 2 || y >= size - 2) valid = false;
                        }
                    }
                    valid &= count > 4;
                    g.DrawImageUnscaled(bitmap, col * 64 + (64 - size) / 2, row * 82 + 17);
                    g.DrawString(ids[col], font, Brushes.Gray, col * 64 + 2, row * 82);
                }
                check(valid, "all vector toolbar icons render without clipping at " + new[] { "100%", "125%", "150%", "200%" }[row] + " size");
            }
            sheet.Save(Path.Combine(root, "toolbar-icons.png"));
        }
    }

    static void Snapshot(Form form, string path)
    {
        using (var image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(path);
        }
    }

    static void Dialog(string root, Action<bool, string> check)
    {
        var type = App.GetType("KimNotes.utils.TodoEditDialog");
        using (var dialog = (Form)Activator.CreateInstance(type, new object[] { "整理本周项目反馈\r\n确认截图与贴图的边界行为", false }))
        {
            dialog.Show(); Application.DoEvents();
            var content = Field<TextBox>(dialog, "txtContent");
            var frame = Field<Panel>(dialog, "contentFrame");
            check(Field<Label>(dialog, "lblTime").Visible, "reminder section label is visible in the todo dialog");
            check(content.ScrollBars == ScrollBars.None && !dialog.AutoScroll, "new todo uses a fixed text area without scrollbars");
            var original = dialog.ClientSize;
            check(original.Width == (int)(320 * (float)App.GetType("KimNotes.utils.UiDpi").GetProperty("Factor").GetValue(null, null)) && original.Height > original.Width,
                "todo editor opens as a narrow portrait card");
            var inputSize = frame.Size;
            content.Text = string.Join("\r\n", Enumerable.Repeat("长内容也保持区域固定，不丢失已输入的文字。", 15));
            check(dialog.ClientSize == original && frame.Size == inputSize && content.Text.Contains("不丢失"), "long todo input preserves text without resizing the dialog");
            var date = Field<DateTimePicker>(dialog, "dtpDate");
            var time = Field<DateTimePicker>(dialog, "dtpTime");
            date.Value = DateTime.Today.AddDays(5);
            time.Value = DateTime.Today.AddHours(13).AddMinutes(25);
            var due = (DateTime?)type.GetProperty("Due").GetValue(dialog, null);
            check(due == DateTime.Today.AddDays(5).AddHours(13).AddMinutes(25), "editing reminder date and time never resets the user's selection to today");
            foreach (var button in Field<Button[]>(dialog, "presets")) button.PerformClick();
            var presets = Field<Button[]>(dialog, "presets");
            check(presets[0].Top == presets[1].Top && presets[2].Top == presets[3].Top && presets[2].Top > presets[0].Bottom
                && presets[0].Left == presets[2].Left, "reminder presets use two columns and two rows");
            check(dialog.ClientSize == original && Field<Button[]>(dialog, "presets").Select(b => b.Width).Distinct().Count() == 1,
                "reminder preset buttons stay equal-width and never shift the dialog size");
            content.Text = "整理本周项目反馈\r\n确认截图与贴图的边界行为";
            Snapshot(dialog, Path.Combine(root, "todo-new.png"));
            var save = Field<Button>(dialog, "btnOk");
            var cancel = Field<Button>(dialog, "btnCancel");
            check(Math.Abs((cancel.Left + save.Right) / 2 - dialog.ClientSize.Width / 2) <= 1,
                "todo dialog actions are centered as one group");
            var dateField = Field<Control>(dialog, "dateField");
            var timeField = Field<Control>(dialog, "timeField");
            var dateInput = Field<MaskedTextBox>(dateField, "input");
            var timeInput = Field<MaskedTextBox>(timeField, "input");
            presets[0].PerformClick();
            dateInput.Text = "2027-02-30";
            timeInput.Text = "14:37";
            check(dateInput.Text == "2027-02-30" && !(bool)Call(dateField, "Commit"),
                "switching to custom time does not overwrite the unfinished or invalid date");
            presets[0].PerformClick();
            timeInput.Text = "29:99";
            dateInput.Text = "2027-05-23";
            check(timeInput.Text == "29:99" && !(bool)Call(timeField, "Commit"),
                "switching to custom date does not silently restore a previous time");
            presets[0].PerformClick();
            type.GetField("syncing", Flags).SetValue(dialog, true);
            date.Value = DateTime.Today.AddDays(-1);
            time.Value = DateTime.Today.AddDays(-1).AddHours(18);
            type.GetField("syncing", Flags).SetValue(dialog, false);
            check((DateTime?)type.GetProperty("Due").GetValue(dialog, null) == DateTime.Today.AddDays(-1).AddHours(18),
                "preset save uses the displayed date instead of recomputing today across midnight");
            dateInput.Text = "2027-05-23";
            timeInput.Text = "14:37";
            due = (DateTime?)type.GetProperty("Due").GetValue(dialog, null);
            check(due == new DateTime(2027, 5, 23, 14, 37, 0), "custom date and minute input preserves the entered values");
            dateInput.Text = "2027-02-30";
            check(!(bool)Call(dateField, "Commit"), "invalid calendar dates are rejected");
            save.PerformClick();
            check(dialog.DialogResult != DialogResult.OK, "invalid date input cannot silently save an old reminder value");
            dateInput.Text = "2027-05-23";
            foreach (var field in new[] { dateField, timeField })
            {
                Call(field, "OpenPicker"); Application.DoEvents();
                var popup = Field<ToolStripDropDown>(field, "popup");
                check(popup != null && popup.Visible, "custom " + (field == dateField ? "calendar" : "time") + " picker opens");
                var host = (ToolStripControlHost)popup.Items[0];
                check(!host.AutoSize && !popup.AutoSize && host.Width == host.Control.Width && host.Height == host.Control.Height
                    && popup.ClientSize.Width >= host.Width && popup.ClientSize.Height >= host.Height,
                    "picker preserves its explicit viewport instead of using preferred content size");
                using (var preview = new Bitmap(popup.Width, popup.Height))
                {
                    popup.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                    preview.Save(Path.Combine(root, field == dateField ? "calendar-picker.png" : "time-picker.png"));
                }
                if (host.Control is MonthCalendar)
                {
                    Call(host.Control, "OnDateSelected", new DateRangeEventArgs(new DateTime(2027, 6, 12), new DateTime(2027, 6, 12)));
                    check(date.Value.Date == new DateTime(2027, 6, 12), "calendar selection updates the actual reminder date");
                }
                else
                {
                    var times = (ListBox)host.Control;
                    check(times.Width >= TextRenderer.MeasureText("23:45", times.Font).Width + SystemInformation.VerticalScrollBarWidth + 20
                        && times.Height / times.ItemHeight >= 5 && times.Height / times.ItemHeight <= 8,
                        "time dropdown shows readable complete times in a bounded scrollable viewport");
                    ((ListBox)host.Control).SelectedIndex = 43;
                    Call(host.Control, "OnClick", EventArgs.Empty);
                    check(time.Value.Hour == 10 && time.Value.Minute == 45, "quick time selection updates the actual reminder minute");
                }
                Application.DoEvents();
                check(popup.IsDisposed && Field<ToolStripDropDown>(field, "popup") == null, "picker closes and releases resources safely");
            }
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                timeField.Size = new Size((int)(170 * scale), (int)(40 * scale));
                Call(timeField, "OpenPicker"); Application.DoEvents();
                var popup = Field<ToolStripDropDown>(timeField, "popup");
                var host = (ToolStripControlHost)popup.Items[0];
                var times = (ListBox)host.Control;
                check(times.Width >= 140 && times.Height <= 8 * times.ItemHeight && host.Size == times.Size
                    && Screen.FromControl(dialog).WorkingArea.Contains(popup.Bounds),
                    "time picker stays bounded and inside the working area at field scale " + scale);
                popup.Close(); Application.DoEvents();
            }
            var inputHeight = frame.Height;
            dialog.ClientSize = new Size(original.Width + 60, original.Height + 70);
            presets[1].PerformClick(); Application.DoEvents();
            check(dialog.ClientSize.Width == original.Width + 60 && dialog.ClientSize.Height == original.Height + 70
                && frame.Height == inputHeight && save.Bottom < dialog.ClientSize.Height
                && Math.Abs((cancel.Left + save.Right) / 2 - dialog.ClientSize.Width / 2) <= 1,
                "manual resize persists across preset changes while input height and centered footer stay stable");
            dialog.ClientSize = original;
            Snapshot(dialog, Path.Combine(root, "todo-new.png"));
            check(save.Bottom < dialog.ClientSize.Height && frame.Right < dialog.ClientSize.Width,
                "todo form content and actions fit inside the fixed layout");
            dialog.Close();
        }
        var oldCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fi-FI");
            using (var dialog = (Form)Activator.CreateInstance(type, new object[] { "区域格式测试", false }))
            {
                dialog.Show(); Application.DoEvents();
                var field = Field<Control>(dialog, "timeField");
                var input = Field<MaskedTextBox>(field, "input");
                input.Text = "14:37";
                check(input.Text == "14:37" && (bool)Call(field, "Commit"),
                    "time input keeps its advertised HH:mm format under non-colon regional settings");
                Call(field, "OpenPicker"); Application.DoEvents();
                var menu = Field<ToolStripDropDown>(field, "popup");
                var times = (ListBox)((ToolStripControlHost)menu.Items[0]).Control;
                check(times.Items[43].ToString() == "10:45", "time picker uses the same invariant format as the input");
                dialog.Close(); Application.DoEvents();
                check(menu.IsDisposed, "closing a dialog with an open time picker releases the popup");
            }
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = oldCulture; }
    }

    static void List(string root, Action<bool, string> check)
    {
        var type = App.GetType("KimNotes.utils.TodoStore");
        type.GetField("items", Flags).SetValue(null, new System.Collections.Generic.List<KimNotes.utils.TodoItem>());
        type.GetField("nextId", Flags).SetValue(null, 1);
        var overdue = (KimNotes.utils.TodoItem)Store("Add", "整理本周项目反馈", DateTime.Now.AddHours(-2), null, null);
        var next = (KimNotes.utils.TodoItem)Store("Add", "检查截图标注与图片保存", DateTime.Today.AddDays(1).AddHours(9), null, null);
        Store("Add", "准备下一次版本发布\n确认说明与下载包保持一致", null, null, null);
        var done = (KimNotes.utils.TodoItem)Store("Add", "统一工具栏矢量图标", null, null, null);
        Store("SetDone", done, true);
        using (var list = new TodoListForm())
        {
            list.Show(); Application.DoEvents();
            check(list.ClientSize.Width == (int)(336 * (float)App.GetType("KimNotes.utils.UiDpi").GetProperty("Factor").GetValue(null, null)) && list.ClientSize.Height > list.ClientSize.Width,
                "todo list uses the same narrow default width and portrait proportions");
            Snapshot(list, Path.Combine(root, "todo-list.png"));
            var cards = (System.Collections.IEnumerable)Field<object>(list, "cards");
            object nextCard = null;
            bool fits = true;
            foreach (Control card in cards)
            {
                var item = (KimNotes.utils.TodoItem)card.GetType().GetProperty("Item").GetValue(card, null);
                if (item.Id == next.Id) nextCard = card;
                var text = Field<TextBox>(card, "txt");
                fits &= text.Top >= 0 && text.Bottom < card.Height && text.Right < card.Width;
                foreach (Control child in card.Controls) fits &= child.Bottom < card.Height && child.Right < card.Width;
            }
            check(fits, "todo cards keep content and reminder labels inside their bounds");
            var reminder = Field<Control>(nextCard, "dueChip");
            using (var closeDialog = new Timer { Interval = 30 })
            {
                closeDialog.Tick += (s, e) =>
                {
                    foreach (Form f in Application.OpenForms.Cast<Form>().Where(f => f.GetType().Name == "TodoEditDialog").ToArray())
                        f.DialogResult = DialogResult.Cancel;
                };
                closeDialog.Start();
                Call(reminder, "OnClick", EventArgs.Empty);
                closeDialog.Stop();
            }
            typeof(Timer).GetMethod("OnTick", Flags).Invoke(Field<Timer>(list, "clickTimer"), new object[] { EventArgs.Empty });
            check(!next.Done, "clicking a reminder label does not accidentally complete the todo");
            for (int i = 0; i < 16; i++) Store("Add", "待办滚轮检查 " + i, null, null, null);
            Application.DoEvents();
            var body = Field<Panel>(list, "body");
            var content = Field<Panel>(list, "content");
            var firstCard = content.Controls.Cast<Control>().First(c => c.GetType().Name == "TodoCard");
            var cardText = Field<TextBox>(firstCard, "txt");
            var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120);
            Call(cardText, "OnMouseWheel", wheel);
            int offset = body.AutoScrollPosition.Y;
            Call(firstCard, "OnMouseWheel", wheel);
            Call(content, "OnMouseWheel", wheel);
            Call(body, "OnMouseWheel", wheel);
            Call(list, "OnMouseWheel", wheel);
            int step = (int)Math.Round(42 * (float)App.GetType("KimNotes.utils.UiDpi").GetProperty("Factor").GetValue(null, null));
            check(wheel.Handled && offset == -step && body.AutoScrollPosition.Y == offset,
                "todo text/card/container wheel bubbling scrolls exactly once (first=" + offset + ", final=" + body.AutoScrollPosition.Y + ", step=" + step + ")");
            var zeroWheel = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 0);
            Call(cardText, "OnMouseWheel", zeroWheel);
            Call(body, "OnMouseWheel", zeroWheel);
            check(zeroWheel.Handled && body.AutoScrollPosition.Y == offset,
                "zero-delta todo wheel does not move the list");
            var reminderWheel = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120);
            Call(Field<Control>(firstCard, "dueChip"), "OnMouseWheel", reminderWheel);
            Call(firstCard, "OnMouseWheel", reminderWheel);
            Call(body, "OnMouseWheel", reminderWheel);
            check(reminderWheel.Handled && body.AutoScrollPosition.Y == offset - step,
                "todo reminder chip wheel is handled once before reaching its parent");
            Func<int, Control> findCard = id => content.Controls.Cast<Control>().First(c => c.GetType().Name == "TodoCard"
                && ((KimNotes.utils.TodoItem)c.GetType().GetProperty("Item").GetValue(c, null)).Id == id);
            Action tickClicks = () => typeof(Timer).GetMethod("OnTick", Flags).Invoke(Field<Timer>(list, "clickTimer"), new object[] { EventArgs.Empty });
            Call(list, "CardClicked", findCard(overdue.Id));
            Call(list, "CardClicked", findCard(next.Id));
            tickClicks();
            check(overdue.Done && next.Done, "rapid single clicks on different todo cards complete both tasks");
            Application.DoEvents();

            var refreshItem = (KimNotes.utils.TodoItem)Store("Add", "刷新期间保留单击操作", null, null, null);
            Application.DoEvents();
            var pendingCard = findCard(refreshItem.Id);
            Call(list, "CardClicked", pendingCard);
            Call(list, "Render");
            tickClicks();
            check(pendingCard.IsDisposed && refreshItem.Done, "todo refresh cannot discard a pending click when its old card is disposed");
            Application.DoEvents();

            var doubleClickCard = findCard(overdue.Id);
            Call(list, "CardClicked", doubleClickCard);
            Call(list, "CardClicked", findCard(next.Id));
            using (var closeDialog = new Timer { Interval = 30 })
            {
                closeDialog.Tick += (s, e) =>
                {
                    foreach (Form f in Application.OpenForms.Cast<Form>().Where(f => f.GetType().Name == "TodoEditDialog").ToArray())
                        f.DialogResult = DialogResult.Cancel;
                };
                closeDialog.Start();
                Call(list, "CardDoubleClicked", doubleClickCard);
                closeDialog.Stop();
            }
            tickClicks();
            check(overdue.Done && !next.Done, "double clicking a todo cancels only that task's pending completion");
            Application.DoEvents();

            var nextSingleCard = findCard(next.Id);
            Call(list, "CardClicked", nextSingleCard);
            Call(list, "CardClicked", findCard(overdue.Id));
            using (var closeDialog = new Timer { Interval = 30 })
            {
                closeDialog.Tick += (s, e) =>
                {
                    foreach (Form f in Application.OpenForms.Cast<Form>().Where(f => f.GetType().Name == "TodoEditDialog").ToArray())
                        f.DialogResult = DialogResult.Cancel;
                };
                closeDialog.Start();
                Call(Field<Control>(nextSingleCard, "dueChip"), "OnClick", EventArgs.Empty);
                closeDialog.Stop();
            }
            tickClicks();
            check(!next.Done && !overdue.Done, "opening a todo reminder cancels only its own pending click and preserves other task clicks");
            Application.DoEvents();
            var search = Field<KimNotes.SearchBox>(list, "searchBox");
            search.Text = "不存在的搜索词";
            typeof(Timer).GetMethod("OnTick", Flags).Invoke(Field<Timer>(list, "searchDebounce"), new object[] { EventArgs.Empty });
            check(((System.Collections.ICollection)Field<object>(list, "cards")).Count == 0, "todo search renders a clean empty state");
            list.Close();
        }
    }
}
