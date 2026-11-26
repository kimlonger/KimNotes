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
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr window, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr window, uint command);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Assembly App = typeof(note).Assembly;
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
    static object Store(string method, params object[] args) => App.GetType("KimNotes.utils.TodoStore").GetMethod(method, Flags).Invoke(null, args);

    public static void Run(string root, Action<bool, string> check)
    {
        Taskbar(root, check);
        BrandIcons(root, check);
        Icons(root, check);
        NoteToolbar(root, check);
        CrossMonitorDpi(root, check);
        DragAcrossMonitors(check);
        NoteMenuRegression.Run(root, check);
        EmptyNotes(root, check);
        History(root, check);
        Dialog(root, check);
        List(root, check);
        Tray(root, check);
        UpdateNoticeToast(check);
    }

    static void EmptyNotes(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "empty-note-fixtures");
        Directory.CreateDirectory(folder);
        var config = App.GetType("KimNotes.utils.InitConfig");
        object previous = config.GetMethod("Snapshot", Flags).Invoke(null, null);
        var changed = App.GetType("KimNotes.utils.Storage").GetEvent("NotesChanged", Flags);
        int notified = 0;
        Action countChange = () => notified++;
        var filePath = typeof(note).GetProperty("FilePath", Flags);
        try
        {
            config.GetMethod("SetConfigValue", Flags).Invoke(null, new object[] { "notesPath", folder });
            changed.AddEventHandler(null, countChange);
            try
            {
                var edits = new Action<RichTextBox>[] { editor => { }, editor => editor.Text = " \t\r\n\u00a0\u3000",
                    editor => editor.Rtf = @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0\b\par\i\par}" };
                var descriptions = new[] { "untouched", "whitespace-only", "format-only" };
                for (int i = 0; i < edits.Length; i++)
                using (var draft = new note())
                {
                    draft.Show(); Application.DoEvents();
                    var editor = Field<RichTextBox>(draft, "richTextBox1");
                    edits[i](editor);
                    draft.TopMost = true;
                    var theme = NoteTheme.All[3];
                    draft.ApplyTheme(theme);
                    draft.GetType().GetField("metadataDirty", Flags).SetValue(draft, true);
                    draft.GetType().GetField("cloudThemeDirty", Flags).SetValue(draft, true);
                    bool saved = (bool)Call(draft, "SaveCurrentNoteSafe");
                    check(saved && filePath.GetValue(draft, null) == null && draft.TopMost && editor.BackColor == theme.Body
                        && !Field<bool>(draft, "metadataDirty") && !Field<bool>(draft, "cloudThemeDirty")
                        && !Field<bool>(draft, "hasUnsavedChanges") && !editor.Modified,
                        descriptions[i] + " drafts keep their temporary UI without allocating a note or leaving dirty flags");
                    draft.Close();
                }
                using (var reopened = new note(null, true))
                    check(filePath.GetValue(reopened, null) == null && string.IsNullOrWhiteSpace(Field<RichTextBox>(reopened, "richTextBox1").Text)
                        && !Directory.EnumerateFileSystemEntries(folder).Any() && notified == 0,
                        "closing and reopening blank drafts leaves no note, sidecar, backup or save notification");
                string blockedPath = Path.Combine(root, "empty-note-path-blocker");
                File.WriteAllText(blockedPath, "not a writable notes directory");
                using (var draft = new note())
                {
                    var notePath = typeof(note).GetField("notePath", Flags);
                    notePath.SetValue(draft, blockedPath);
                    draft.SetText("temporary attempted first save");
                    check(!(bool)Call(draft, "SaveCurrentNoteSafe") && filePath.GetValue(draft, null) == null,
                        "a failed first write does not assign a persisted note identity");
                    notePath.SetValue(draft, folder);
                    Field<RichTextBox>(draft, "richTextBox1").Clear();
                    check((bool)Call(draft, "SaveCurrentNoteSafe") && filePath.GetValue(draft, null) == null
                        && !Directory.EnumerateFileSystemEntries(folder).Any() && notified == 0,
                        "clearing a draft after its failed first write still leaves no saved note");
                }
            }
            finally { changed.RemoveEventHandler(null, countChange); }

            string photo;
            using (var imageNote = new note())
            using (var image = new Bitmap(18, 12))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.RoyalBlue);
                imageNote.InsertImage(image);
                photo = (string)filePath.GetValue(imageNote, null);
            }
            check(File.Exists(photo), "a never-saved note with only a real image receives a persisted file");
            if (!File.Exists(photo)) return;
            using (var reopened = new note(Path.GetFileName(photo)))
                check((bool)App.GetType("KimNotes.utils.NoteIndex").GetMethod("HasEmbeddedContent", Flags)
                    .Invoke(null, new object[] { Field<RichTextBox>(reopened, "richTextBox1").Rtf }),
                    "image-only notes reopen with their embedded content");

            string cleared;
            using (var noteToClear = new note())
            {
                noteToClear.SetText("will be cleared intentionally");
                check((bool)Call(noteToClear, "SaveCurrentNoteSafe"), "start with a saved nonempty note");
                cleared = (string)filePath.GetValue(noteToClear, null);
                Field<RichTextBox>(noteToClear, "richTextBox1").Clear();
                check((bool)Call(noteToClear, "SaveCurrentNoteSafe") && File.Exists(cleared),
                    "clearing an existing note still writes the edit without deleting its file");
            }
            using (var reopened = new note(Path.GetFileName(cleared)))
                check(string.IsNullOrWhiteSpace(Field<RichTextBox>(reopened, "richTextBox1").Text),
                    "a saved note that was deliberately cleared stays empty when reopened");

            string legacy = Path.Combine(folder, "legacy-blank.rtf");
            string legacyMetadata = legacy + ".meta.json";
            string text = Path.Combine(folder, "indexed-text.rtf");
            File.WriteAllText(legacy, @"{\rtf1\ansi\b\tab\par}");
            File.WriteAllText(legacyMetadata, "{\"ThemeId\":3}");
            File.WriteAllText(text, @"{\rtf1\ansi EmptyNoteSearchToken}");
            var preserved = new[] { legacy, legacyMetadata, cleared };
            var preservedBytes = preserved.Select(File.ReadAllBytes).ToArray();
            var preservedTimes = preserved.Select(File.GetLastWriteTimeUtc).ToArray();
            SeedLegacyEmptyPreviews(legacy, photo);
            using (var list = new history(folder))
            {
                list.Show(); HistoryLoaded(list);
                var viewport = Field<Panel>(list, "panel1");
                Func<string[]> visibleFiles = () => viewport.Controls.OfType<Panel>().Where(card => card.Visible)
                    .Select(card => (string)card.Tag).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var expected = new[] { Path.GetFileName(photo), Path.GetFileName(text) }.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                check(visibleFiles().SequenceEqual(expected), "history rechecks old empty previews, hides blank files and retains image-only notes");
                var search = Field<SearchBox>(list, "searchBox");
                search.Text = "EmptyNoteSearchToken";
                Call(list, "ApplySearchFilter");
                check(visibleFiles().SequenceEqual(new[] { Path.GetFileName(text) }), "history still searches the text of nonempty notes");
                search.Text = "no-match-for-empty-note-test";
                Call(list, "ApplySearchFilter");
                var chrome = Field<Panel>(list, "chromeBar");
                check(!visibleFiles().Any() && chrome.Visible && search.Visible && Field<int>(list, "contentHeight") == 0,
                    "empty search results leave the history header and search available with no card space");
                search.Text = "";
                Call(list, "ApplySearchFilter");
                check(visibleFiles().SequenceEqual(expected), "clearing search restores only the nonempty note cards");
                check(preserved.Select((path, index) => File.Exists(path) && File.GetLastWriteTimeUtc(path) == preservedTimes[index]
                    && File.ReadAllBytes(path).SequenceEqual(preservedBytes[index])).All(same => same),
                    "hiding old or deliberately cleared notes does not rewrite or remove their data or metadata");
            }
        }
        finally { config.GetMethod("Restore", Flags).Invoke(null, new[] { previous }); }
    }

    static void SeedLegacyEmptyPreviews(params string[] paths)
    {
        var type = App.GetType("KimNotes.utils.NoteIndex");
        var entryType = type.GetNestedType("Entry", Flags);
        object index = type.GetMethod("Load", Flags).Invoke(null, null);
        var set = type.GetMethod("Set", Flags, null, new[] { typeof(string), entryType }, null);
        foreach (string path in paths)
        {
            var info = new FileInfo(path);
            var entry = Activator.CreateInstance(entryType);
            entryType.GetProperty("Mtime").SetValue(entry, info.LastWriteTime.Ticks, null);
            entryType.GetProperty("Length").SetValue(entry, info.Length, null);
            entryType.GetProperty("Title").SetValue(entry, "（空便签）", null);
            entryType.GetProperty("Summary").SetValue(entry, "", null);
            entryType.GetProperty("SearchText").SetValue(entry, "", null);
            set.Invoke(index, new[] { path, entry });
        }
    }

    static void Taskbar(string root, Action<bool, string> check)
    {
        const string key = "showInTaskbar";
        const string backgroundKey = "fileTransferBackgroundReceive";
        var config = App.GetType("KimNotes.utils.InitConfig");
        var snapshot = config.GetMethod("Snapshot", Flags);
        var restore = config.GetMethod("Restore", Flags);
        var load = config.GetMethod("InitSettings", Flags);
        var enabled = config.GetProperty("ShowTaskbarIcons", Flags);
        var backgroundReceive = config.GetProperty("FileTransferBackgroundReceive", Flags);
        var apply = typeof(KimNotes.settings.ConfigForm).GetMethod("SaveAndRestartIfNeeded", Flags);
        int restartRequests = 0;
        Action restart = () => restartRequests++;
        object previous = snapshot.Invoke(null, null);
        var defaults = (System.Collections.Generic.Dictionary<string, string>)config.GetMethod("DefaultValues", Flags).Invoke(null, null);
        string configFile = Path.Combine(root, "config.txt");
        try
        {
            check(defaults.TryGetValue(key, out string defaultValue) && defaultValue == bool.TrueString,
                "new taskbar configurations show normal windows on the taskbar by default");
            check(defaults.TryGetValue(backgroundKey, out string backgroundDefault) && backgroundDefault == bool.FalseString,
                "new configurations keep background file reception off by default");
            defaults[key] = "invalid";
            defaults[backgroundKey] = "invalid";
            restore.Invoke(null, new object[] { defaults });
            check((bool)enabled.GetValue(null, null), "an invalid taskbar setting falls back to showing normal window icons");
            check(!(bool)backgroundReceive.GetValue(null, null), "an invalid file reception setting falls back to off");
            defaults.Remove(key);
            defaults.Remove(backgroundKey);
            defaults["pasteShortcutVersion"] = "2";
            defaults["previousImageKey"] = "F3";
            defaults["nextImageKey"] = "F4";
            restore.Invoke(null, new object[] { defaults });
            check((bool)enabled.GetValue(null, null), "an older configuration without the key also shows normal window icons");
            check(!(bool)backgroundReceive.GetValue(null, null), "an older configuration without an explicit preference leaves background file reception off");
            CheckTaskbarWindowState(root, true, check);

            using (var settings = new KimNotes.settings.ConfigForm())
            {
                settings.Show(); Application.DoEvents();
                var startup = Field<CheckBox>(settings, "checkBox1");
                var incognito = Field<CheckBox>(settings, "checkBox3");
                var taskbar = Field<CheckBox>(settings, "taskbarCheckBox");
                var background = Field<CheckBox>(settings, "fileTransferBackgroundCheckBox");
                var options = new[] { startup, incognito, taskbar, background };
                check(startup.Checked && !incognito.Checked && taskbar.Checked && !background.Checked
                    && options.Select(box => box.Text).SequenceEqual(new[] { "开机启动", "无痕模式", "显示任务栏", "文件传输" })
                    && options.All(box => box.Visible && box.AutoCheck && box.TabStop && box.Appearance == Appearance.Normal
                        && box.Parent == taskbar.Parent && box.Parent.ClientRectangle.Contains(box.Bounds) && box.Left == startup.Left)
                    && startup.Bottom <= incognito.Top && incognito.Bottom <= taskbar.Top && taskbar.Bottom <= background.Top,
                    "the general settings fit as a single column of native checkboxes with their defaults");
                var body = Field<Panel>(settings, "bodyPanel");
                string[] obsoleteHeadings = { "常规", "快捷键", "存储" };
                check(!body.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.OfType<Label>())
                    .Any(heading => heading.Visible && obsoleteHeadings.Contains(heading.Text)),
                    "settings displays its controls directly without the old section headings");
                var footer = Field<Panel>(settings, "footBar");
                check(footer.Visible && settings.ClientRectangle.Contains(footer.Bounds)
                    && settings.Height <= Screen.FromControl(settings).WorkingArea.Height && body.AutoScroll,
                    "settings keeps the save footer within the display and its content scrollable");
                Call(background, "OnClick", EventArgs.Empty);
                check(background.Checked && !(bool)backgroundReceive.GetValue(null, null),
                    "changing background file reception does not apply until it is saved");
                Action saveSettings = () => Call(settings, "SaveData");
                bool restartedForBackground = (bool)apply.Invoke(null, new object[] { saveSettings, restart });
                check(!restartedForBackground && restartRequests == 0 && File.ReadAllLines(configFile).Contains(backgroundKey + "=True"),
                    "saving only background reception also migrates legacy settings without restarting the app");
                Call(taskbar, "OnClick", EventArgs.Empty);
                check(!taskbar.Checked && (bool)enabled.GetValue(null, null),
                    "changing the taskbar checkbox does not apply until it is saved");
                Call(startup, "OnClick", EventArgs.Empty);
                Call(incognito, "OnClick", EventArgs.Empty);
                bool restartedForOtherSettings = (bool)apply.Invoke(null, new object[] { saveSettings, restart });
                check(restartedForOtherSettings && restartRequests == 1, "changing other settings still uses the normal application restart");
                var saved = File.ReadAllLines(configFile);
                check(settings.ShowInTaskbar && saved.Contains(key + "=False"),
                    "save persists the taskbar choice without changing the current settings window");
                check(saved.Contains(backgroundKey + "=True"), "save persists explicitly enabled background file reception");
                check(!startup.Checked && incognito.Checked && saved.Contains("checkBox1=False") && saved.Contains("checkBox3=True"),
                    "the native startup and incognito checkboxes save to their existing configuration keys");
            }

            load.Invoke(null, null);
            check(!(bool)enabled.GetValue(null, null), "reloading the saved settings retains the tray-only preference");
            check((bool)backgroundReceive.GetValue(null, null), "reloading the settings retains explicitly enabled background file reception");
            CheckTaskbarWindowState(root, false, check);
            object customSettings = snapshot.Invoke(null, null);
            using (var settings = new KimNotes.settings.ConfigForm())
            {
                settings.Show(); Application.DoEvents();
                var taskbar = Field<CheckBox>(settings, "taskbarCheckBox");
                var background = Field<CheckBox>(settings, "fileTransferBackgroundCheckBox");
                check(!taskbar.Checked && background.Checked && !Field<CheckBox>(settings, "checkBox1").Checked
                    && Field<CheckBox>(settings, "checkBox3").Checked,
                    "the native checkboxes reflect their reloaded preferences");
                Call(taskbar, "OnClick", EventArgs.Empty);
                Call(background, "OnClick", EventArgs.Empty);
                Call(settings, "SaveData");
            }
            load.Invoke(null, null);
            check((bool)enabled.GetValue(null, null) && File.ReadAllLines(configFile).Contains(key + "=True"),
                "turning the taskbar option on again persists the enabled preference");
            check(!(bool)backgroundReceive.GetValue(null, null) && File.ReadAllLines(configFile).Contains(backgroundKey + "=False"),
                "turning background file reception off persists the disabled preference");
            CheckTransientTaskbarWindows(check);
            restore.Invoke(null, new[] { customSettings });
            using (var settings = new KimNotes.settings.ConfigForm()) Call(settings, "ResetData");
            load.Invoke(null, null);
            check((bool)enabled.GetValue(null, null), "restoring settings defaults enables the taskbar icons again");
            check(!(bool)backgroundReceive.GetValue(null, null), "restoring settings defaults turns off background file reception");
            var setConfig = config.GetMethod("SetConfigValue", Flags);
            setConfig.Invoke(null, new object[] { backgroundKey, bool.TrueString });
            using (var settings = new KimNotes.settings.ConfigForm())
            {
                Action reset = () => Call(settings, "ResetData");
                bool restartedForReset = (bool)apply.Invoke(null, new object[] { reset, restart });
                check(!restartedForReset && restartRequests == 1 && !(bool)backgroundReceive.GetValue(null, null),
                    "restoring only the background reception preference applies without restarting");
            }
        }
        finally { restore.Invoke(null, new[] { previous }); }
    }

    static void CheckTaskbarWindowState(string root, bool enabled, Action<bool, string> check)
    {
        var syncType = App.GetType("KimNotes.utils.CloudSync");
        var existingSync = syncType.GetProperty("Current", Flags).GetValue(null, null);
        using (var temporarySync = existingSync == null
            ? (IDisposable)Activator.CreateInstance(syncType, Flags, null, new object[] { null, false }, null) : null)
        using (var memo = new note())
        using (var historyWindow = new history(root))
        using (var settings = new KimNotes.settings.ConfigForm())
        using (var todos = new TodoListForm())
        using (var account = (Form)Activator.CreateInstance(App.GetType("KimNotes.settings.AccountForm"), Flags, null,
            new object[] { existingSync ?? temporarySync, null }, null))
        {
            var windows = new Form[] { memo, historyWindow, settings, todos, account };
            bool initial = windows.All(window => window.ShowInTaskbar == enabled);
            memo.Show(); Application.DoEvents();
            check(initial && memo.Visible && windows.All(window => HasTaskbarWindowStyle(window) == enabled),
                "a note remains visible and the five normal windows apply the native taskbar preference on creation (enabled " + enabled + ")");
        }
    }

    static bool HasTaskbarWindowStyle(Form window)
    {
        const int extendedStyle = -20, appWindow = 0x00040000;
        return (GetWindowLong(window.Handle, extendedStyle) & appWindow) != 0;
    }

    static void CheckTransientTaskbarWindows(Action<bool, string> check)
    {
        var context = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(App.GetType("KimNotes.MyApplicationContext"));
        Call(context, "ShowReminderPopup", "任务栏测试提醒", new int[0]);
        using (var reminder = Field<Form>(context, "currentReminder"))
        using (var editor = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.TodoEditDialog"), new object[] { "任务栏测试", false }))
        using (var screenshot = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.ScreenshotToolbarForm"), Flags,
            null, new object[] { (Action<string>)(_ => { }) }, null))
        {
            check(new[] { reminder, editor, screenshot }.All(window => !window.ShowInTaskbar && !HasTaskbarWindowStyle(window)),
                "reminder, editor and screenshot toolbar stay off the taskbar even when the option is enabled");
        }
    }

    static void UpdateNoticeToast(Action<bool, string> check)
    {
        var type = App.GetType("KimNotes.UpdateToast");
        var changes = new System.Collections.Generic.List<bool>();
        bool failSave = false;
        int saveErrors = 0;
        int downloads = 0;
        var show = type.GetMethod("ShowForVersion", Flags);
        var shown = (bool)show.Invoke(null, new object[] { "9.8.7", false,
            new Action<bool>(value => { if (failSave) throw new IOException("preview save failure"); changes.Add(value); }), new Action(() => downloads++) });
        Application.DoEvents();
        var toast = (Form)type.GetField("current", Flags).GetValue(null);
        check(shown && toast != null && toast.Visible && Screen.FromControl(toast).WorkingArea.Contains(toast.Bounds),
            "update reminder opens within a monitor working area");
        var close = Field<Button>(toast, "dismiss");
        var brand = Field<Label>(toast, "appName");
        check(toast.ClientRectangle.Contains(close.Bounds) && !close.Bounds.IntersectsWith(brand.Bounds)
            && close.Right <= toast.ClientSize.Width - Math.Max(4, (int)Math.Round(6 * toast.DeviceDpi / 96f))
            && close.Top >= Math.Max(4, (int)Math.Round(6 * toast.DeviceDpi / 96f)),
            "update reminder close control is inset from rounded edges and does not overlap the app name");
        var checkbox = Field<CheckBox>(toast, "skip");
        checkbox.Checked = true;
        checkbox.Checked = false;
        check(changes.SequenceEqual(new[] { true, false }), "update reminder immediately reports skip and undo");
        type.GetField("showSaveError", Flags).SetValue(toast, new Action(() => saveErrors++));
        failSave = true;
        checkbox.Checked = true;
        check(!checkbox.Checked && saveErrors == 1 && changes.SequenceEqual(new[] { true, false }),
            "failed skip persistence restores the prior checkbox state and reports the error");
        bool duplicate = (bool)show.Invoke(null, new object[] { "9.8.7", false,
            new Action<bool>(value => changes.Add(value)), new Action(() => downloads++) });
        check(!duplicate && ReferenceEquals(type.GetField("current", Flags).GetValue(null), toast),
            "rechecking an open reminder reuses it");
        Field<Button>(toast, "download").PerformClick();
        Application.DoEvents();
        check(downloads == 1 && toast.IsDisposed, "download action opens once and closes the reminder");
        show.Invoke(null, new object[] { "9.8.8", false, new Action<bool>(_ => { }), new Action(() => downloads++) });
        Application.DoEvents();
        var secondToast = (Form)type.GetField("current", Flags).GetValue(null);
        Field<Button>(secondToast, "dismiss").PerformClick();
        Application.DoEvents();
        check(secondToast.IsDisposed && downloads == 1, "close control dismisses reminder without opening download");
    }

    static void Tray(string root, Action<bool, string> check)
    {
        var context = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(App.GetType("KimNotes.MyApplicationContext"));
        using (var menu = (ContextMenuStrip)Call(context, "CreateTrayMenu"))
        {
            var labels = menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToArray();
            check(labels.SequenceEqual(new[] { "新建便签", "便签列表", "待办清单", "设置", "检查更新", "退出" })
                && menu.Items.OfType<ToolStripSeparator>().Count() == 1 && menu.Items[3] is ToolStripSeparator,
                "compact native tray menu shows six actions with one separator after the to-do list");
            foreach (var screen in Screen.AllScreens)
            {
                var wa = screen.WorkingArea;
                menu.Show(new Point(wa.Right - 8, wa.Bottom - 8), ToolStripDropDownDirection.AboveLeft);
                Application.DoEvents();
                var factor = (float)App.GetType("KimNotes.utils.UiDpi").GetMethod("FactorForScreen", Flags).Invoke(null, new object[] { screen });
                check(wa.Contains(menu.Bounds) && menu.Width < 220 * factor && menu.Height < 310 * factor
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
        CheckTitleBarHeights(form, check);
        HistoryCardAppearance(form, panel, check);
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
        var historySearch = Field<SearchBox>(form, "searchBox");
        historySearch.Text = "audit-task";
        Call(form, "ApplySearchFilter");
        Field<Timer>(form, "searchDebounceTimer").Stop();
        Call(form, "GlobalMouseWheel", panel, new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120));
        int logicalOffset = (int)Math.Round(Field<int>(form, "scrollOffset") * 96.0 / form.DeviceDpi);
        bool historyDpiFits = true, historyDpiState = true;
        for (int round = 0; round < 2; round++)
        foreach (var screen in Screen.AllScreens)
        {
            form.Location = new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40);
            Application.DoEvents();
            historyDpiFits &= Field<int>(form, "historyLayoutDpi") == form.DeviceDpi
                && form.ClientRectangle.Contains(historySearch.Bounds)
                && panel.Width > 0 && panel.Height > 0 && panel.Controls.Cast<Control>().Any(c => c.Visible);
            historyDpiState &= historySearch.Text == "audit-task"
                && Math.Abs(Field<int>(form, "scrollOffset") * 96.0 / form.DeviceDpi - logicalOffset) < 8;
        }
        check(historyDpiFits, "note list keeps its search and visible cards in bounds across monitor DPIs");
        check(historyDpiState, "note list preserves search and logical scroll position across monitor DPIs");
        WindowDragRegression.Check(form, "History", check);
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
        search.Text = string.Empty;
        search.FocusInput();
        Field<Timer>(form, "searchDebounceTimer").Stop();
        Call(form, "ApplySearchFilter");
        Call(form, "GlobalMouseWheel", panel, new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120));
        var deletionCard = panel.Controls.OfType<Panel>().First(item => item.Visible && item.Top >= 0);
        var delete = deletionCard.Controls.OfType<ToolIconButton>().FirstOrDefault(button => button.IconId == "trash");
        if (delete != null) HistoryDeleteCancellation(form, deletionCard, delete, check);
        form.Dispose(); Application.DoEvents();
        var changed = (Delegate)App.GetType("KimNotes.utils.Storage").GetField("NotesChanged", Flags).GetValue(null);
        check(Field<bool>(form, "resourcesReleased") && !Field<Timer>(form, "refreshTimer").Enabled
            && !Field<Timer>(form, "searchDebounceTimer").Enabled
            && (changed == null || !changed.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, form))),
            "disposing history stops refresh timers and removes its static event subscription");
        form.Dispose();
    }

    static void CheckTitleBarHeights(history historyWindow, Action<bool, string> check)
    {
        using (var reference = new note())
        using (var settings = new KimNotes.settings.ConfigForm())
        using (var todos = new TodoListForm())
        {
            Point position = historyWindow.Location;
            foreach (var window in new Form[] { reference, settings, todos })
            {
                window.StartPosition = FormStartPosition.Manual;
                window.Location = position;
                window.Show();
            }
            Application.DoEvents();

            var headers = new[] { Field<Panel>(settings, "chromeBar"), Field<Panel>(historyWindow, "chromeBar"),
                Field<Panel>(todos, "chromeBar") };
            var mainHeader = Field<Panel>(reference, "chromePanel");
            var mainLogo = Field<PictureBox>(reference, "captionIcon");
            var mainTitle = Field<Label>(reference, "captionTitle");
            var mainClose = Field<ToolIconButton>(reference, "closeBtn");
            check(headers.All(header => header.FindForm().ClientSize == reference.ClientSize),
                "settings, history and todo default width and height match the fresh note (main "
                + reference.ClientSize + "; actual " + string.Join(", ", headers.Select(header => header.FindForm().ClientSize)) + ")");
            double dpi = reference.DeviceDpi / 96.0;
            int buttonSize = (int)Math.Round(26 * dpi);
            check(mainHeader.Height == (int)Math.Round(28 * dpi) && mainHeader.Top == (int)Math.Round(5 * dpi)
                && mainClose.Size == new Size(buttonSize, buttonSize),
                "the main note uses the slim 28-logical-pixel title bar and 26-logical-pixel title actions");
            check(headers.All(header => header.Height == mainHeader.Height && header.Top == mainHeader.Top
                && header.Bottom == mainHeader.Bottom && header.Top == header.FindForm().Padding.Top),
                "settings, history and todo title bands match the main note including padding (main "
                + mainHeader.Bottom + "; actual " + string.Join(", ", headers.Select(header => header.Bottom)) + ")");
            var logos = headers.Select(header => header.Controls.OfType<PictureBox>().Single()).ToArray();
            check(logos.All(logo => logo.Visible && logo.Image != null && logo.SizeMode == PictureBoxSizeMode.Zoom
                && logo.Size == mainLogo.Size && logo.Left + logo.Parent.Left == mainLogo.Left + mainHeader.Left
                && logo.Top + logo.Parent.Top == mainLogo.Top + mainHeader.Top && logo.Parent.ClientRectangle.Contains(logo.Bounds)),
                "the three sheep logos match the main note's displayed size and window-relative position (main "
                + mainLogo.Size + "; actual " + string.Join(", ", logos.Select(logo => logo.Size)) + ")");
            check(logos.All(logo => ((Label)logo.Parent.Tag).Left - logo.Right == mainTitle.Left - mainLogo.Right),
                "title text follows the sheep logo at the same gap as the main note");
            int mainRightGap = reference.ClientSize.Width - mainHeader.Left - mainClose.Right;
            var closeButtons = headers.Select(header => header.Controls.OfType<ToolIconButton>()
                .Single(button => button.IconId == "close")).ToArray();
            check(closeButtons.All(button => button.Visible && button.Size == mainClose.Size
                && button.Top + button.Parent.Top == mainClose.Top + mainHeader.Top
                && button.FindForm().ClientSize.Width - button.Parent.Left - button.Right == mainRightGap
                && button.Parent.ClientRectangle.Contains(button.Bounds)),
                "the three title bands match the main note's close button size, alignment and right spacing");
            var pin = Field<ToolIconButton>(todos, "pinBtn");
            check(pin.Visible && pin.Size == mainClose.Size && pin.Top == closeButtons[2].Top,
                "the todo title pin follows the same sizing and vertical alignment as its close button");
            var body = Field<Panel>(settings, "bodyPanel");
            var footer = Field<Panel>(settings, "footBar");
            var site = Field<LinkLabel>(settings, "aboutSite");
            Rectangle footerBounds = footer.Bounds;
            body.ScrollControlIntoView(site);
            check(body.AutoScroll && body.ClientRectangle.Contains(body.RectangleToClient(site.RectangleToScreen(site.ClientRectangle)))
                && footer.Visible && footer.Bounds == footerBounds && settings.ClientRectangle.Contains(footer.Bounds),
                "the settings website row can be reached while its save footer stays fixed and visible");
        }
    }

    static void HistoryCardAppearance(history form, Panel viewport, Action<bool, string> check)
    {
        var chrome = Field<Panel>(form, "chromeBar");
        check(!chrome.Controls.OfType<ToolIconButton>().Any(button => button.IconId == "gear"),
            "note list omits the settings button");
        var search = Field<SearchBox>(form, "searchBox");
        check(search.IdleColor == search.FocusColor && search.LineColor != search.FocusLineColor,
            "note list search uses a focus outline over the same surface");
        search.FocusInput();
        Field<Timer>(form, "searchDebounceTimer").Stop();
        Call(form, "ApplySearchFilter");
        using (var frame = new Bitmap(search.Width, search.Height))
        {
            search.DrawToBitmap(frame, search.ClientRectangle);
            int depth = Math.Min(search.Height / 2, (int)Math.Round(9 * search.DeviceDpi / 96f) + 3);
            var backdrop = search.BackColor.ToArgb();
            var edge = Enumerable.Range(0, depth);
            bool horizontal = Enumerable.Range(1, frame.Width - 2).All(x =>
                edge.Any(y => frame.GetPixel(x, y).ToArgb() != backdrop) &&
                edge.Any(y => frame.GetPixel(x, frame.Height - 1 - y).ToArgb() != backdrop));
            bool vertical = Enumerable.Range(1, frame.Height - 2).All(y =>
                edge.Any(x => frame.GetPixel(x, y).ToArgb() != backdrop) &&
                edge.Any(x => frame.GetPixel(frame.Width - 1 - x, y).ToArgb() != backdrop));
            check(horizontal && vertical, "history search outline is painted across all four corners and edges");
        }
        var card = viewport.Controls.OfType<Panel>().First(item => item.Visible && item.Top == 0);
        var preview = card.Controls.OfType<RichTextBox>().Single();
        var timeLabel = card.Controls.OfType<Label>().Single();
        var actions = card.Controls.OfType<ToolIconButton>().ToArray();
        check(actions.Length == 3 && actions.All(button => button.Visible && button.TabStop && button.IconSize == 16)
            && actions.Any(button => button.IconId == "open-note" && button.AccessibleName == "打开便签")
            && actions.Any(button => button.IconId == "trash" && button.AccessibleName == "删除便签")
            && actions.Any(button => button.IconId == "clock" && button.AccessibleName == "历史版本"),
            "history cards expose compact, labeled version, open and delete icons before hovering");
        check(card.ContextMenuStrip == null && preview.ContextMenuStrip == null && timeLabel.ContextMenuStrip == null,
            "history icons replace the old right-click menu");
        check(actions.Length == 3 && actions.All(button => card.ClientRectangle.Contains(button.Bounds)
                && !button.Bounds.IntersectsWith(preview.Bounds) && !button.Bounds.IntersectsWith(timeLabel.Bounds))
            && actions.All(button => actions.All(other => button == other || !button.Bounds.IntersectsWith(other.Bounds))),
            "history actions sit inside the card without covering the timestamp or note text");
        check(SendMessage(preview.Handle, 0x0084, IntPtr.Zero, IntPtr.Zero) == new IntPtr(-1),
            "history preview passes pointer input to its card instead of selecting read-only text");
        var surface = card.BackColor.ToArgb();
        using (var frame = new Bitmap(card.Width, card.Height))
        {
            card.DrawToBitmap(frame, card.ClientRectangle);
            var backdrop = viewport.BackColor.ToArgb();
            check(frame.GetPixel(2, 2).ToArgb() == backdrop && frame.GetPixel(card.Width - 3, 2).ToArgb() == backdrop
                && frame.GetPixel(2, card.Height - 3).ToArgb() == backdrop
                && frame.GetPixel(card.Width - 3, card.Height - 3).ToArgb() == backdrop
                && frame.GetPixel(card.Width / 2, 8).ToArgb() == surface,
                "idle history cards have rounded surfaces at all four corners");
        }
        Call(card, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0));
        check(Field<string>(form, "selectedFileName") == (string)card.Tag,
            "clicking a history card selects the corresponding note");
        int oldWidth = card.Width;
        form.Width += 50;
        using (var frame = new Bitmap(card.Width, card.Height))
        {
            card.DrawToBitmap(frame, card.ClientRectangle);
            check(card.Width > oldWidth && frame.GetPixel(oldWidth - 4, card.Height - 4).ToArgb() == surface
                && frame.GetPixel(card.Width - 20, card.Height - 5).ToArgb() == surface,
                "widening a history card leaves a clean corner at both widths");
            check(frame.GetPixel(card.Width / 2, 1).ToArgb() != surface && frame.GetPixel(4, card.Height / 2).ToArgb() == surface,
                "selecting a history card adds an outline without changing its fill");
        }
    }

    static void HistoryDeleteCancellation(history form, Panel card, ToolIconButton delete, Action<bool, string> check)
    {
        string file = Path.Combine(Field<string>(form, "folderPath"), (string)card.Tag);
        int offset = Field<int>(form, "scrollOffset");
        bool confirmationSeen = false;
        using (var dismiss = new Timer { Interval = 30 })
        {
            dismiss.Tick += (sender, args) =>
            {
                const uint enabledPopup = 6;
                const int no = 7, command = 0x0111;
                IntPtr prompt = GetWindow(form.Handle, enabledPopup);
                if (prompt == form.Handle || prompt == IntPtr.Zero) return;
                IntPtr noButton = GetDlgItem(prompt, no);
                if (noButton == IntPtr.Zero) return;
                confirmationSeen = true;
                SendMessage(prompt, command, new IntPtr(no), noButton);
            };
            dismiss.Start();
            Call(delete, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
        }
        var searchDebounce = Field<Timer>(form, "searchDebounceTimer");
        if (searchDebounce.Enabled) Call(searchDebounce, "OnTick", EventArgs.Empty);
        check(confirmationSeen && File.Exists(file) && !card.IsDisposed,
            "canceling the history delete icon confirmation leaves the note intact");
        check(offset < 0 && Field<int>(form, "scrollOffset") == offset,
            "canceling history deletion with empty search preserves the scroll position");
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
            int expectedHeight = (int)Math.Round(32 * form.DeviceDpi / 96f);
            var workArea = Screen.FromControl(form).WorkingArea;
            int noteWidth = needed + form.Padding.Horizontal;
            int noteHeight = (int)Math.Round(noteWidth * 448 / 336.0);
            check(form.ClientSize == new Size(Math.Min(noteWidth, workArea.Width), Math.Min(noteHeight, workArea.Height)),
                "the slimmer toolbar keeps the default note width and height based on the fixed toolbar width");
            int[] positions=null;
            foreach (int width in new[] { 760, 360, 900, 420 })
            {
                form.ClientSize = new Size(width, 440); Application.DoEvents();
                var buttons = bar.Controls.OfType<ToolIconButton>().OrderBy(b => b.Left).ToArray();
                check(bar.Height == expectedHeight && buttons.Length == 8 && buttons[0].Left > 0 && buttons[0].Left < bar.Width / 8
                    && buttons.Last().Right < bar.Width && buttons.All(b => b.Top == (bar.Height - b.Height) / 2 && b.Bottom <= bar.Height)
                    && buttons.Zip(buttons.Skip(1), (a, b) => b.Left >= a.Right + 2 && b.Left-a.Right<=10).All(b => b)
                    && (positions==null || positions.SequenceEqual(buttons.Select(b=>b.Left))),
                    "the 32-logical-pixel toolbar keeps centered buttons and fixed horizontal gaps after resizing to " + width);
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
                "note uses native per-monitor pixels without automatic bitmap scaling or child resizing");
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
            bool geometry = true, colors = true, hidden = true, textStable = true, frames = true, nativeVisibility = true;
            for (int round = 0; round < 12; round++)
            foreach (var screen in screens)
            {
                bool compact = round % 3 == 1;
                Call(form, "SetCompact", compact);
                Call(form, "AdvanceChromeAnimation", Field<DateTime>(form, "chromeAnimationStarted").AddSeconds(2));
                form.Location = new Point(screen.WorkingArea.Left + 32, screen.WorkingArea.Top + 32);
                Application.DoEvents();
                observedDpis.Add((int)GetDpiForWindow(form.Handle));
                int dpi = form.DeviceDpi;
                int expectedHeight = (int)Math.Round(32 * dpi / 96f);
                var buttons = bar.Controls.OfType<ToolIconButton>().ToArray();
                geometry &= Field<int>(form, "layoutDpi") == dpi && bar.Height == expectedHeight
                    && chrome.Height == (int)Math.Round(28 * dpi / 96f)
                    && buttons.All(b => b.Width == (int)Math.Round(30 * dpi / 96f) && b.IconSize == 20
                        && b.Top == (bar.Height - b.Height) / 2 && b.Bottom <= bar.Height && b.Right < bar.Width);
                colors &= bar.BackColor == theme.Body && body.BackColor == theme.Body
                    && buttons.All(b => b.BackColor == theme.Body && b.Theme == theme);
                hidden &= Field<bool>(form, "compact") == compact && overlay.Visible == compact
                    && buttons.All(b => b.Visible == !compact)
                    && (!compact || overlay.Bounds == bar.ClientRectangle);
                nativeVisibility &= IsWindowVisible(bar.Handle) && IsWindowVisible(body.Handle)
                    && IsWindowVisible(overlay.Handle) == compact
                    && buttons.All(b => IsWindowVisible(b.Handle) == !compact);
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
            check(nativeVisibility, "native window visibility matches the compact state after repeated DPI transitions");
            check(textStable, "monitor moves never alter note text");
            if (screens.Length > 1)
            {
                Console.WriteLine("Cross-monitor DPI evidence: " + string.Join(", ", observedDpis.OrderBy(d => d)) + "; notifications=" + dpiEvents);
                check(dpiEvents == 0, "mixed-DPI monitor stress never rescales the note or its children");
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
                dropped &= form.DeviceDpi == initialDpi && Field<int>(form, "layoutDpi") == initialDpi
                    && form.Location == drop && !Field<bool>(form, "interactingWithBounds")
                    && form.Size == initialSize && events == beforeEvents;
                palette &= Field<bool>(form, "compact") == compact
                    && bar.BackColor == theme.Body && body.BackColor == theme.Body
                    && bar.Height == (int)Math.Round(32 * initialDpi / 96f)
                    && bar.Controls.OfType<ToolIconButton>().All(button => button.Visible == !compact && button.BackColor == theme.Body);
                formats &= ReadCharacterFormatting(body).SequenceEqual(formatting);
            }
            check(steady, "live cross-monitor dragging keeps the window under the requested position without mid-drag resizing or full DPI layout");
            check(dropped, "dropping keeps the same pixel size and layout without a delayed monitor rescale");
            check(palette, "deferred drag DPI preserves per-note colors, compact state and toolbar geometry");
            check(formats, "repeated live DPI crossings preserve every character's font, style, size and color");
            WindowDragRegression.Check(form, "Note", check);
            check(body.CanUndo, "cross-monitor dragging retains the native rich-text undo history");
            body.Undo();
            check(body.Text == text.Substring(0, text.Length - 1), "undo after a monitor drag still reverses the user's last text edit");
            form.Close();
        }
    }

    static void BrandIcons(string root, Action<bool, string> check)
    {
        foreach (string resource in new[] { "KimNotes.note", "KimNotes.history", "KimNotes.settings.ConfigForm", "KimNotes.settings.register" })
        {
            var manager = new System.Resources.ResourceManager(resource, App);
            var icon = (Icon)manager.GetObject("$this.Icon");
            using (var expected = DecodeIconPng(icon))
            using (var form = new Form { Icon = icon })
            {
                var chrome = FormChrome.Apply(form, "小羊便签", false, null, Color.White, Color.Black);
                var image = chrome.Controls.OfType<PictureBox>().Single().Image;
                check(SameIconPixels(image, expected), resource + " title icon matches decoded brand PNG on .NET Framework");
            }
            manager.ReleaseAllResources();
        }
        using (var n = new note())
        using (var expected = DecodeIconPng(n.Icon))
        {
            var image = Field<PictureBox>(n, "captionIcon").Image;
            check(SameIconPixels(image, expected), "note title icon matches the brand PNG instead of compressed-data noise");
            using (var sheet = new Bitmap(240, 100))
            using (var graphics = Graphics.FromImage(sheet))
            {
                graphics.Clear(Color.FromArgb(219, 230, 242));
                int left = 16;
                foreach (int size in new[] { 18, 23, 27, 36 })
                {
                    graphics.DrawImage(image, new Rectangle(left, 22, size, size));
                    left += size + 22;
                }
                sheet.Save(Path.Combine(root, "brand-icons.png"));
            }
        }
    }

    // Decode the packaged PNG directly, independently of Icon.ToBitmap and the UI helper.
    static Bitmap DecodeIconPng(Icon icon)
    {
        using (var bytes = new MemoryStream())
        {
            icon.Save(bytes);
            var reader = new BinaryReader(bytes);
            bytes.Position = 4;
            int count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                bytes.Position = 6 + i * 16;
                int width = reader.ReadByte(); if (width == 0) width = 256;
                int height = reader.ReadByte(); if (height == 0) height = 256;
                bytes.Position += 6;
                int length = reader.ReadInt32();
                int offset = reader.ReadInt32();
                if (width != icon.Width || height != icon.Height) continue;
                bytes.Position = offset;
                using (var png = new MemoryStream(reader.ReadBytes(length)))
                using (var image = new Bitmap(png)) return new Bitmap(image);
            }
        }
        throw new Exception("Packaged brand icon has no matching PNG frame");
    }

    static bool SameIconPixels(Image actual, Bitmap expected)
    {
        if (actual == null || actual.Size != expected.Size) return false;
        using (var first = new Bitmap(expected.Width, expected.Height))
        using (var second = new Bitmap(expected.Width, expected.Height))
        {
            using (var g = Graphics.FromImage(first)) { g.Clear(Color.White); g.DrawImageUnscaled(actual, 0, 0); }
            using (var g = Graphics.FromImage(second)) { g.Clear(Color.White); g.DrawImageUnscaled(expected, 0, 0); }
            for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
            {
                var a = first.GetPixel(x, y); var b = second.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) > 2 || Math.Abs(a.G - b.G) > 2 || Math.Abs(a.B - b.B) > 2) return false;
            }
        }
        return true;
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
            content.Text = "跨屏时保留的待办内容";
            dateInput.Text = "2027-05-23";
            timeInput.Text = "14:37";
            int logicalWidth = (int)Math.Round(dialog.ClientSize.Width * 96.0 / dialog.DeviceDpi);
            bool dpiLayout = true, dpiState = true;
            for (int round = 0; round < 3; round++)
            foreach (var screen in Screen.AllScreens)
            {
                dialog.Location = new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40);
                Application.DoEvents();
                float scale = dialog.DeviceDpi / 96f;
                var repeat = Field<Button>(dialog, "repeatBox");
                dpiLayout &= Math.Abs((float)type.GetProperty("S", Flags).GetValue(dialog, null) - scale) < 0.001f
                    && Math.Abs(dialog.ClientSize.Width * 96.0 / dialog.DeviceDpi - logicalWidth) < 4
                    && dialog.ClientRectangle.Contains(frame.Bounds) && dialog.ClientRectangle.Contains(repeat.Bounds)
                    && dialog.ClientRectangle.Contains(save.Bounds) && dialog.ClientRectangle.Contains(cancel.Bounds)
                    && presets.All(button => dialog.ClientRectangle.Contains(button.Bounds))
                    && presets[0].Right < presets[1].Left && repeat.Bottom < save.Top;
                dpiState &= content.Text == "跨屏时保留的待办内容" && dateInput.Text == "2027-05-23" && timeInput.Text == "14:37";
            }
            check(dpiLayout, "todo editor recomputes its field and action bounds on each monitor DPI");
            check(dpiState, "moving a todo editor between monitors preserves unfinished text and reminder input");
            WindowDragRegression.Check(dialog, "Todo editor", check);
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
        var overdue = (KimNotes.utils.TodoItem)Store("Add", "整理本周项目反馈", DateTime.Today.AddDays(-1).AddHours(19), null, null);
        var next = (KimNotes.utils.TodoItem)Store("Add", "检查截图标注与图片保存", DateTime.Today.AddDays(1).AddHours(9), null, null);
        Store("Add", "准备下一次版本发布\n确认说明与下载包保持一致", null, null, null);
        var done = (KimNotes.utils.TodoItem)Store("Add", "统一工具栏矢量图标", null, null, null);
        Store("SetDone", done, true);
        using (var list = new TodoListForm())
        {
            list.Show(); Application.DoEvents();
            TodoListAppearance(list, check);
            Snapshot(list, Path.Combine(root, "todo-list.png"));
            var cards = (System.Collections.IEnumerable)Field<object>(list, "cards");
            Control nextCard = null;
            Control overdueCard = null;
            bool fits = true;
            bool multilineFits = false;
            foreach (Control card in cards)
            {
                var item = (KimNotes.utils.TodoItem)card.GetType().GetProperty("Item").GetValue(card, null);
                if (item.Id == next.Id) nextCard = card;
                if (item.Id == overdue.Id) overdueCard = card;
                var text = Field<TextBox>(card, "txt");
                fits &= text.Top >= 0 && text.Bottom < card.Height && text.Right < card.Width;
                foreach (Control child in card.Controls) fits &= child.Bottom < card.Height && child.Right < card.Width;
                if (text.Lines.Length > 1)
                {
                    int lastLineTop = text.GetPositionFromCharIndex(text.TextLength - 1).Y;
                    multilineFits = lastLineTop > 0 && lastLineTop + text.Font.Height <= text.ClientSize.Height;
                }
            }
            check(fits, "todo cards keep content and reminder labels inside their bounds");
            check(multilineFits, "compact todo cards keep the last line of multiline content visible");
            var reminder = TodoReminder(nextCard);
            check(reminder.Text == TodoDateLabel(next.Due.Value)
                && TodoReminder(overdueCard).Text == "过期 · " + TodoDateLabel(overdue.Due.Value)
                && Field<string>(Field<Control>(list, "hdrActive"), "Title") == "进行"
                && Field<string>(Field<Control>(list, "hdrDone"), "Title") == "完成",
                "todo section labels and full reminder dates use the requested copy");
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
            check(!next.Done, "clicking a reminder label does not accidentally complete the todo");
            for (int i = 0; i < 16; i++) Store("Add", "待办滚轮检查 " + i, null, null, null);
            Application.DoEvents();
            var body = Field<Panel>(list, "body");
            var content = Field<Panel>(list, "content");
            var firstCard = content.Controls.Cast<Control>().First(c => c.GetType().Name == "TodoCard");
            var cardText = Field<TextBox>(firstCard, "txt");
            var wheel = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120);
            Call(cardText, "OnMouseWheel", wheel);
            int offset = content.Top;
            Call(firstCard, "OnMouseWheel", wheel);
            Call(content, "OnMouseWheel", wheel);
            Call(body, "OnMouseWheel", wheel);
            Call(list, "OnMouseWheel", wheel);
            int step = (int)Math.Round(42 * (float)App.GetType("KimNotes.utils.UiDpi").GetProperty("Factor").GetValue(null, null));
            check(wheel.Handled && offset == -step && content.Top == offset,
                "todo text/card/container wheel bubbling scrolls exactly once (first=" + offset + ", final=" + content.Top + ", step=" + step + ")");
            var zeroWheel = new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 0);
            Call(cardText, "OnMouseWheel", zeroWheel);
            Call(body, "OnMouseWheel", zeroWheel);
            check(zeroWheel.Handled && content.Top == offset,
                "zero-delta todo wheel does not move the list");
            var reminderWheel = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120);
            Call(TodoReminder(firstCard), "OnMouseWheel", reminderWheel);
            Call(firstCard, "OnMouseWheel", reminderWheel);
            Call(body, "OnMouseWheel", reminderWheel);
            check(reminderWheel.Handled && content.Top == offset - step,
                "todo reminder chip wheel is handled once before reaching its parent");
            var actionWheel = new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120);
            Call(Field<ToolIconButton>(firstCard, "editButton"), "OnMouseWheel", actionWheel);
            Call(firstCard, "OnMouseWheel", actionWheel);
            Call(body, "OnMouseWheel", actionWheel);
            check(actionWheel.Handled && content.Top == offset - step * 2,
                "todo action buttons still allow wheel scrolling without duplicate movement");
            const int scrollBarStyles = 0x00100000 | 0x00200000; // WS_HSCROLL | WS_VSCROLL
            check(!body.AutoScroll && (GetWindowLong(body.Handle, -16) & scrollBarStyles) == 0
                && content.Left == 0 && content.Width <= body.ClientSize.Width,
                "todo wheel scrolling leaves neither a native scrollbar nor horizontal drift");
            for (int i = 0; i < 200; i++) Call(body, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120));
            check(content.Bottom == body.ClientSize.Height, "todo wheel scrolling stops at the final row");
            for (int i = 0; i < 200; i++) Call(body, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, 120));
            check(content.Top == 0, "todo wheel scrolling stops at the top");
            Func<int, Control> findCard = id => content.Controls.Cast<Control>().First(c => c.GetType().Name == "TodoCard"
                && ((KimNotes.utils.TodoItem)c.GetType().GetProperty("Item").GetValue(c, null)).Id == id);
            TodoListActions(list, findCard, next, done, check);
            var search = Field<KimNotes.SearchBox>(list, "searchBox");
            search.Text = "待办滚轮检查";
            typeof(Timer).GetMethod("OnTick", Flags).Invoke(Field<Timer>(list, "searchDebounce"), new object[] { EventArgs.Empty });
            var dpiCard = content.Controls.Cast<Control>().First(c => c.GetType().Name == "TodoCard");
            var dpiItem = (KimNotes.utils.TodoItem)dpiCard.GetType().GetProperty("Item").GetValue(dpiCard, null);
            Call(list, "SetScrollOffset", (int)Math.Round(100 * list.DeviceDpi / 96f));
            int logicalScroll = (int)Math.Round(-content.Top * 96.0 / list.DeviceDpi);
            int logicalWidth = (int)Math.Round(list.ClientSize.Width * 96.0 / list.DeviceDpi);
            var originalDpiCards = ((System.Collections.IEnumerable)Field<object>(list, "cards")).Cast<Control>().ToArray();
            bool listDpiFits = true, listDpiState = true;
            for (int round = 0; round < 3; round++)
            foreach (var screen in Screen.AllScreens)
            {
                list.Location = new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40);
                Application.DoEvents();
                var visibleCards = ((System.Collections.IEnumerable)Field<object>(list, "cards")).Cast<Control>().ToArray();
                listDpiFits &= Math.Abs(Field<float>(list, "s") - list.DeviceDpi / 96f) < 0.001f
                    && Math.Abs(list.ClientSize.Width * 96.0 / list.DeviceDpi - logicalWidth) < 4
                    && visibleCards.Length == 16
                    && visibleCards.SequenceEqual(originalDpiCards)
                    && visibleCards.All(card => card.Controls.Cast<Control>().All(child => child.Right <= card.ClientSize.Width && child.Bottom <= card.ClientSize.Height));
                listDpiState &= search.Text == "待办滚轮检查" && Field<string>(list, "keyword") == "待办滚轮检查"
                    && Math.Abs(-content.Top * 96.0 / list.DeviceDpi - logicalScroll) < 8
                    && !dpiItem.Done;
            }
            check(listDpiFits, "todo list resizes cards and controls for each monitor DPI without clipping");
            WindowDragRegression.Check(list, "Todo list", check);
            Call(Field<ToolIconButton>(dpiCard, "completionButton"), "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
            check(listDpiState && dpiItem.Done, "todo list preserves search and scroll across monitors and completion still works");
            Application.DoEvents();
            search.Text = "不存在的搜索词";
            typeof(Timer).GetMethod("OnTick", Flags).Invoke(Field<Timer>(list, "searchDebounce"), new object[] { EventArgs.Empty });
            check(((System.Collections.ICollection)Field<object>(list, "cards")).Count == 0, "todo search renders a clean empty state");
            list.Close();
        }
    }

    static Control TodoReminder(Control card)
        => card.Controls.Cast<Control>().Single(child => child.GetType().Name == "Chip"
            && (bool)child.GetType().GetProperty("Clickable", Flags).GetValue(child, null));

    static string TodoDateLabel(DateTime time)
        => time.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    static void TodoListActions(TodoListForm list, Func<int, Control> findCard, KimNotes.utils.TodoItem active,
        KimNotes.utils.TodoItem done, Action<bool, string> check)
    {
        var activeCard = findCard(active.Id);
        var activeToggle = Field<ToolIconButton>(activeCard, "completionButton");
        var activeEdit = Field<ToolIconButton>(activeCard, "editButton");
        var doneCard = findCard(done.Id);
        var doneToggle = Field<ToolIconButton>(doneCard, "completionButton");
        var editButton = Field<ToolIconButton>(doneCard, "editButton");
        var deleteButton = Field<ToolIconButton>(doneCard, "deleteButton");
        check(activeToggle.Visible && activeToggle.IconId == "check-open" && !activeToggle.Selected
            && activeToggle.AccessibleName == "完成待办" && activeEdit.Visible && Field<ToolIconButton>(activeCard, "deleteButton") == null,
            "active todos expose an unchecked completion icon and an edit button");
        check(doneToggle.Visible && doneToggle.IconId == "todo" && !doneToggle.Selected
            && doneToggle.GlyphColor == Field<Color>(list, "accent") && doneToggle.AccessibleName == "恢复待办"
            && editButton.Visible && deleteButton.Visible && doneToggle.TabStop && editButton.TabStop && deleteButton.TabStop,
            "completed todos show the accented checked icon, edit, and delete without a persistent selection border");
        check(activeToggle.Left == doneToggle.Left && activeToggle.Size == doneToggle.Size
            && activeEdit.Left == editButton.Left && activeEdit.Size == editButton.Size,
            "active and completed todo cards align their completion and edit buttons in the same columns");
        var completionDate = doneCard.Controls.Cast<Control>()
            .FirstOrDefault(child => child.Text == "完成 · " + TodoDateLabel(done.DoneAt.Value));
        check(completionDate != null, "completed todo metadata shows the full completion date");

        var text = Field<TextBox>(doneCard, "txt");
        var actions = new[] { doneToggle, editButton, deleteButton };
        var actionBounds = actions.Select(button => button.RectangleToScreen(button.ClientRectangle)).ToArray();
        check(actionBounds.All(bounds => !bounds.IntersectsWith(text.RectangleToScreen(text.ClientRectangle)))
            && !actionBounds[0].IntersectsWith(actionBounds[1]) && !actionBounds[0].IntersectsWith(actionBounds[2])
            && !actionBounds[1].IntersectsWith(actionBounds[2]), "todo action hit zones do not cover the text or one another");
        check(completionDate != null && completionDate.Left == text.Left && completionDate.Top >= text.Bottom
            && actionBounds.All(bounds => !bounds.IntersectsWith(completionDate.RectangleToScreen(completionDate.ClientRectangle))),
            "the full completion date stays at lower left beside the compact actions");
        float scale = Field<float>(list, "s");
        int hitSize = (int)Math.Round(24 * scale);
        int gap = (int)Math.Round(2 * scale);
        check(actions.All(button => button.IconSize == 16 && button.Size == new Size(hitSize, hitSize))
            && actionBounds[1].Left - actionBounds[0].Right == gap && actionBounds[2].Left - actionBounds[1].Right == gap,
            "todo card actions use compact, consistently spaced icons");

        bool unexpectedEditor = false;
        using (var closeDialog = new Timer { Interval = 30 })
        {
            closeDialog.Tick += (s, e) =>
            {
                foreach (Form editor in Application.OpenForms.Cast<Form>().Where(f => f.GetType().Name == "TodoEditDialog").ToArray())
                {
                    unexpectedEditor = true;
                    editor.DialogResult = DialogResult.Cancel;
                }
            };
            closeDialog.Start();
            Call(activeCard, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
            Call(text, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Left, 2, 4, 4, 0));
        }
        check(!active.Done && done.Done && !unexpectedEditor && doneCard.ContextMenuStrip == null && text.ContextMenuStrip == null,
            "todo card clicks do not complete or edit, and the explicit buttons replace the context menu");

        Call(activeToggle, "OnMouseClick", new MouseEventArgs(MouseButtons.Right, 1, 4, 4, 0));
        check(!active.Done, "right-clicking a completion button leaves task state alone");
        Call(activeToggle, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
        check(active.Done, "the completion button completes the selected todo immediately");
        Application.DoEvents();
        Call(Field<ToolIconButton>(findCard(active.Id), "completionButton"), "OnKeyUp", new KeyEventArgs(Keys.Space));
        check(!active.Done, "Space on the checked completion button restores a completed todo immediately");
        Application.DoEvents();

        string updatedText = done.Text + "（已编辑）";
        bool editorOpened = false;
        using (var submitDialog = new Timer { Interval = 30 })
        {
            submitDialog.Tick += (s, e) =>
            {
                foreach (Form editor in Application.OpenForms.Cast<Form>().Where(f => f.GetType().Name == "TodoEditDialog").ToArray())
                {
                    editorOpened = true;
                    Field<TextBox>(editor, "txtContent").Text = updatedText;
                    editor.DialogResult = DialogResult.OK;
                }
            };
            submitDialog.Start();
            Call(Field<ToolIconButton>(findCard(done.Id), "editButton"), "OnKeyUp", new KeyEventArgs(Keys.Enter));
        }
        check(editorOpened && done.Text == updatedText && done.Done, "the edit button updates a completed todo without restoring it");
        Application.DoEvents();
        var deletedCard = findCard(done.Id);
        Call(Field<ToolIconButton>(deletedCard, "deleteButton"), "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, 4, 4, 0));
        Application.DoEvents();
        check(done.DeletedAt.HasValue && done.Text == updatedText && deletedCard.IsDisposed && Store("Find", done.Id) == null,
            "the delete button hides the completed todo and retains its record");
    }

    static void TodoListAppearance(TodoListForm list, Action<bool, string> check)
    {
        var chrome = Field<Panel>(list, "chromeBar");
        var theme = Field<NoteTheme>(list, "th");
        check(chrome.BackColor == theme.Chrome && chrome.Controls.OfType<PictureBox>().Any(icon => icon.Image != null),
            "todo list uses the shared themed title bar with the app icon");

        var top = Field<Panel>(list, "topPanel");
        var search = Field<SearchBox>(list, "searchBox");
        var add = Field<ToolIconButton>(list, "addBtn");
        int topWidth = top.ClientSize.Width;
        int searchWidth = search.Width;
        int addWidth = add.Width;
        float scale = Field<float>(list, "s");
        int addSize = (int)Math.Round(28 * scale);
        int searchHeight = (int)Math.Round(34 * scale);
        int searchGap = (int)Math.Round(6 * scale);
        check(add.Visible && add.IconId == "add" && add.AccessibleName == "新增待办"
            && add.IconSize == 18 && add.Size == new Size(addSize, addSize) && search.Height == searchHeight
            && add.Top == search.Top + (search.Height - add.Height) / 2 && add.Right == topWidth
            && search.Left == 0 && add.Left - search.Right == searchGap && search.Width > topWidth - addWidth * 2,
            "the compact add icon is centered beside the full-height todo search field");

        var card = ((System.Collections.IEnumerable)Field<object>(list, "cards")).Cast<Control>().Last();
        int initialWidth = list.Width;
        int oldCardWidth = card.Width;
        bool repaintsFormerEdge = false;
        InvalidateEventHandler recordRepaint = (sender, args) =>
            repaintsFormerEdge |= args.InvalidRect.Contains(oldCardWidth - 1, card.Height / 2);
        card.Invalidated += recordRepaint;
        list.Width += 80;
        card.Invalidated -= recordRepaint;
        check(card.Width > oldCardWidth && repaintsFormerEdge,
            "widening a todo card invalidates its former right edge");
        check(search.Width - searchWidth == top.ClientSize.Width - topWidth && add.Width == addWidth
            && add.Right == top.ClientSize.Width && add.Left - search.Right == searchGap,
            "widening the todo window expands the search while retaining the add icon gap and size");
        for (int i = 0; i < 12; i++)
        {
            list.Width = initialWidth + (i % 2 == 0 ? 0 : 65);
            Application.DoEvents();
        }
        list.Width = initialWidth;
        var viewport = Field<Panel>(list, "body");
        check(card.Right < viewport.ClientSize.Width && card.Width == oldCardWidth
            && card.Controls.Cast<Control>().All(child => child.Right <= card.ClientSize.Width),
            "repeated horizontal resizing keeps todo cards and their contents within the viewport");
    }
}
