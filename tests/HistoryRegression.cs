using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

// Build as CloudRegression.exe to reuse the existing friend assembly, without
// adding production visibility or involving a real account/server.
internal static class HistoryRegression
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
    static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    static void Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (!condition()) { if (DateTime.UtcNow > timeout) throw new Exception("Timed out waiting for an event/result."); Application.DoEvents(); Thread.Sleep(2); }
        Application.DoEvents();
    }
    static void Loaded(history form)
    {
        Until(() => !Field<bool>(form, "previewLoading") && !Field<System.Windows.Forms.Timer>(form, "refreshTimer").Enabled);
    }
    static object[] Cards(history form) => Field<IList>(form, "allCards").Cast<object>().ToArray();
    static object Card(history form, string name) => Cards(form).Single(card => (string)card.GetType().GetProperty("FileName").GetValue(card) == name);
    static T Prop<T>(object target, string name) => (T)(target.GetType().GetProperty(name)?.GetValue(target) ?? target.GetType().GetField(name).GetValue(target));
    static void Write(string path, string text)
    {
        using (var editor = new RichTextBox()) { var handle = editor.Handle; editor.Text = text; editor.SaveFile(path, RichTextBoxStreamType.RichText); }
    }
    static void Session(CloudSync cloud, CloudSession session) => typeof(CloudSync).GetProperty("Session", Flags).SetValue(cloud, session);

    [STAThread] static int Main(string[] args)
    {
        string root = args[0];
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR", root);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        try
        {
            InitConfig.InitSettings();
            string binaryRtf = "{\\rtf1 before{\\pict\\bin4 " + "}{\\{" + "}after}";
            string onlyText = NoteIndex.TextOnlyRtf(binaryRtf);
            Check(onlyText == "{\\rtf1 before{\\uc1 [\\u22270?\\u29255?]}after}", "picture skipping respects binary data containing escaped/unbalanced braces");
            var longLine = NoteIndex.FromText(new string('a', 20000) + "FullTail", DateTime.Now, 1);
            Check(longLine.Title.Length <= 257 && longLine.SearchText.EndsWith("fulltail"), "very long lines keep previews small while preserving full-content search");
            var folder = InitConfig.GetConfigValue("notesPath"); Directory.CreateDirectory(folder);
            string ansiPath = Path.Combine(root, "legacy-ansi.rtf");
            File.WriteAllBytes(ansiPath, System.Text.Encoding.GetEncoding(28591).GetBytes("{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0\\fnil\\fcharset0 Arial;}}\\f0 caf\u00e9}"));
            using (var legacy = new RichTextBox())
            {
                var ansi = NoteIndex.ReadPreview(legacy, ansiPath, File.GetLastWriteTime(ansiPath), new FileInfo(ansiPath).Length);
                Check(ansi.Title == "café", "text-only preview retains the RTF file's declared legacy code page");
            }
            string user = Guid.NewGuid().ToString(), other = Guid.NewGuid().ToString();
            string photo = Path.Combine(folder, "photo.rtf");
            string image;
            using (var bitmap = new Bitmap(512, 384))
            using (var data = new MemoryStream())
            {
                var random = new Random(123);
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                    bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                bitmap.Save(data, ImageFormat.Png); image = BitConverter.ToString(data.ToArray()).Replace("-", "");
            }
            File.WriteAllText(photo, "{\\rtf1\\ansi photo title\\par line two\\par line three\\par line four\\par line five\\par"
                + "{\\pict\\pngblip\\picw512\\pich384 " + image + "}\\par FullTextNeedle beyond preview}");
            var hash = CloudStore.FileHash(photo);
            for (int i = 0; i < 45; i++)
            {
                string path = Path.Combine(folder, i.ToString("00") + ".rtf");
                Write(path, "note " + i + "\nlist content"); File.SetLastWriteTime(path, DateTime.Now.AddHours(-i - 1));
            }
            var hidden = Path.Combine(folder, "hidden.rtf"); Write(hidden, "other account secret"); CloudStore.Claim("hidden.rtf", other);
            CloudStore.Claim("photo.rtf", user);
            CloudStore.UserId = user;
            using (var sync = new CloudSync(start: false))
            {
                Session(sync, new CloudSession { UserId = user, Email = "synthetic@example.invalid" });
                CloudStore.UserId = user;
                var cloudState = new CloudState();
                var record = new CloudRecord { Revision = 2, LocalStamp = CloudStore.QuickStamp(photo) };
                cloudState.Notes["photo.rtf"] = record;
                Set(sync, "state", cloudState);
                var index = NoteIndex.Load();
                using (var form = new history(folder))
                {
                    // Deterministically suspend indexing, rather than measuring machine speed.
                    lock (Field<object>(index, "gate"))
                    {
                        form.Show();
                        bool heartbeat = false; form.BeginInvoke((Action)(() => heartbeat = true));
                        Until(() => heartbeat);
                        Check(form.Visible && Field<bool>(form, "previewLoading"), "first window and UI messages work while the index worker is blocked");
                    }
                    Loaded(form);
                    Check(Cards(form).Length == 46 && Cards(form).All(card => Prop<string>(card, "FileName") != "hidden.rtf"), "visible index respects account ownership");
                    Check(Cards(form).All(card => !Prop<string>(card, "SearchText").Contains("（空便签）"))
                        && Cards(form).Any(card => Prop<string>(card, "SearchText").Contains("note 1")), "ordinary RTF notes retain real text rather than empty previews");
                    var panel = Field<Panel>(form, "panel1");
                    Check(panel.Controls.OfType<Panel>().Count() < Cards(form).Length / 2, "offscreen notes do not allocate RichEdit cards during opening");
                    var photoCard = Card(form, "photo.rtf");
                    var native = Prop<Panel>(photoCard, "Card");
                    var preview = Prop<RichTextBox>(photoCard, "PreviewBox");
                    Check(preview.Text.Contains("photo title") && !preview.Text.Contains("FullTextNeedle"), "picture-heavy RTF gives a compact five-line preview");
                    Check(Prop<string>(photoCard, "SearchText").Contains("fulltextneedle") && CloudStore.FileHash(photo) == hash, "full-content search survives picture removal from the preview without modifying the original");
                    NoteIndex.Entry cached;
                    var info = new FileInfo(photo);
                    Check(index.TryGet(photo, info.LastWriteTime, info.Length, out cached), "newly parsed previews are cached with modification time and byte length");
                    var search = Field<SearchBox>(form, "searchBox"); search.Text = "FullTextNeedle"; Call(form, "ApplySearchFilter");
                    Check(Cards(form).Count(card => Prop<bool>(card, "IsFilteredIn")) == 1 && native.Visible, "search finds text beyond the displayed preview");
                    search.Text = ""; Call(form, "ApplySearchFilter");
                    Call(form, "GlobalMouseWheel", panel, new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120));
                    int offset = Field<int>(form, "scrollOffset");
                    Call(form, "RefreshCardList"); Loaded(form);
                    Check(ReferenceEquals(native, Prop<Panel>(Card(form, "photo.rtf"), "Card")) && Field<int>(form, "scrollOffset") == offset, "unchanged refresh reuses the card and preserves scroll position");
                    Check(ReferenceEquals(cached, Prop<NoteIndex.Entry>(Prop<object>(Card(form, "photo.rtf"), "File"), "Preview")), "unchanged refresh uses the in-memory text cache");
                    // Return to the top before inspecting visible-card sync labels.
                    Set(form, "scrollOffset", 0); Call(form, "RelayoutCards");
                    var label = Prop<Label>(photoCard, "TimeLabel");
                    Check(label.Text.Contains("已同步"), "signed-in cards show per-note sync status next to the date");
                    int generation = Field<int>(form, "indexGeneration");
                    record.LastError = "图片超过云端限制，请压缩后重试"; record.ErrorStamp = CloudStore.QuickStamp(photo);
                    Call(sync, "Update", "synthetic status"); Until(() => label.Text.Contains("同步失败"));
                    Check(Field<int>(form, "indexGeneration") == generation && ReferenceEquals(native, Prop<Panel>(photoCard, "Card"))
                        && Field<ToolTip>(form, "syncTips").GetToolTip(label).Contains("图片超过"), "failed sync updates only its status label and retains the failure reason tooltip");
                    record.LastError = null; Set(sync, "activeFile", "photo.rtf"); Call(sync, "Update", "synthetic status"); Until(() => label.Text.Contains("同步中"));
                    Set(sync, "activeFile", null); record.LocalStamp = "old"; Call(sync, "Update", "synthetic status"); Until(() => label.Text.Contains("待同步"));
                    Check(Field<int>(form, "indexGeneration") == generation, "syncing and pending transitions never rebuild/re-index the list");
                    // Save hook caches text without touching index.json on every keystroke.
                    string indexPath = Path.Combine(root, "index.json"); var before = File.GetLastWriteTimeUtc(indexPath);
                    Write(photo, "updated title\nupdated body"); NoteIndex.UpdateFromText(photo, "updated title\nupdated body");
                    Check(File.GetLastWriteTimeUtc(indexPath) == before, "save hook is memory-only instead of synchronously rewriting the full index");
                    Call(form, "RefreshCardList"); Loaded(form);
                    Check(ReferenceEquals(native, Prop<Panel>(Card(form, "photo.rtf"), "Card")) && preview.Text.Contains("updated title"), "saved-note updates reuse the existing native card");
                    Storage.WriteText(photo + ".meta.json", "{\"ThemeId\":3}"); Call(form, "RefreshCardList"); Loaded(form);
                    Check(native.BackColor == NoteTheme.All[3].Body && preview.BackColor == native.BackColor, "theme changes update the existing card without recreating it");
                    bool fonts = true, fits = true;
                    for (int round = 0; round < 4; round++)
                    foreach (var screen in Screen.AllScreens)
                    {
                        form.Location = new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40);
                        Until(() => !Field<bool>(form, "dpiRefreshQueued"));
                        foreach (var card in panel.Controls.OfType<Panel>().Where(c => c.Visible))
                        {
                            var body = card.Controls.OfType<RichTextBox>().Single(); var date = card.Controls.OfType<Label>().Single();
                            fonts &= Math.Abs(body.Font.SizeInPoints - 9) < 0.05 && Math.Abs(date.Font.SizeInPoints - 8) < 0.05;
                            int last = body.GetFirstCharIndexFromLine(body.Lines.Length - 1);
                            fits &= body.GetPositionFromCharIndex(last).Y + body.Font.Height <= body.ClientSize.Height + 2;
                        }
                        using (var shot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(shot, form.ClientRectangle); shot.Save(Path.Combine(root, "history-status-" + form.DeviceDpi + ".png")); }
                    }
                    Check(fonts && fits, "reused cards retain 9pt preview and 8pt date/status and fit after real monitor DPI changes");
                    Call(form, "DeleteNote", "00.rtf");
                    Check(!File.Exists(Path.Combine(folder, "00.rtf")) && Cards(form).All(card => Prop<string>(card, "FileName") != "00.rtf"), "deleting removes the card immediately and invalidates older index callbacks");
                    Loaded(form);
                    Session(sync, null); CloudStore.UserId = null; Set(sync, "state", null); Call(sync, "Update", "signed out");
                    Check(native.IsDisposed && !panel.Controls.OfType<Panel>().Any(), "logout immediately removes the previous account's native preview controls");
                    Loaded(form);
                    Check(Cards(form).Length == 44 && Cards(form).All(card => Prop<string>(card, "FileName") != "photo.rtf"), "logout cannot reintroduce previous-account previews from a delayed worker");
                    Check(panel.Controls.OfType<Panel>().Where(card => card.Visible).All(card => !card.Controls.OfType<Label>().Single().Text.Contains("同步")), "signed-out cards show no cloud status");
                    form.Dispose(); Application.DoEvents();
                    var changed = (Delegate)typeof(CloudSync).GetField("Changed", Flags).GetValue(sync);
                    Check(Field<bool>(form, "resourcesReleased") && (changed == null || changed.GetInvocationList().All(item => !ReferenceEquals(item.Target, form))), "disposing the list cancels work and unsubscribes from cloud status");
                }
                using (var closing = new history(folder))
                {
                    lock (Field<object>(index, "gate")) { closing.Show(); closing.Dispose(); }
                    Application.DoEvents(); Check(closing.IsDisposed, "closing during blocked indexing is safe");
                }
            }
            Console.WriteLine("History checks passed: " + checks + "; artifacts: " + root); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
