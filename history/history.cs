using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    public partial class history : Form
    {
        // 视觉刷新配色（与 mockup / 主题统一）
        private Color formBackColor = Color.White;
        private readonly Color titleColor = Color.FromArgb(43, 47, 54);
        private readonly Color mutedColor = Color.FromArgb(138, 146, 158);
        private readonly Color highlightColor = Color.FromArgb(255, 224, 138);
        private Panel chromeBar;
        private SearchBox searchBox;
        private Color themeBodyColor = Color.FromArgb(234, 240, 247);

        private string folderPath = InitConfig.GetConfigValue("notesPath") ?? "";

        private const int CARD_GAP = 10;
        private const int CARD_PADDING_H = 13;

        private readonly List<HistoryCardMeta> allCards = new List<HistoryCardMeta>();
        private readonly Timer searchDebounceTimer = new Timer();
        private readonly Timer refreshTimer = new Timer { Interval = 250 };
        private int scrollOffset = 0;
        private int contentHeight = 0;
        private string currentKeyword = "";
        private bool resourcesReleased;
        private readonly ToolTip syncTips = new ToolTip { AutoPopDelay = 15000 };
        private readonly Timer statusTimer = new Timer { Interval = 120 };
        private CloudSync observedCloud;
        private string displayedUserId;
        private System.Threading.CancellationTokenSource indexingCancel;
        private int indexGeneration;
        private bool previewLoading;
        private bool refreshAfterIndexing;

        private sealed class HistoryFile
        {
            public string Path, Name, MetadataStamp;
            public DateTime Modified;
            public long Length;
            public NoteIndex.Entry Preview;
        }

        private sealed class HistoryCardMeta
        {
            public Panel Card { get; set; }
            public RichTextBox PreviewBox { get; set; }
            public Label TimeLabel { get; set; }
            public string FileName { get; set; }
            public string SearchText { get; set; }
            public int CardHeight { get; set; }
            public bool IsFilteredIn { get; set; }
            public Color BodyColor { get; set; }
            public HistoryFile File { get; set; }
            public string DisplayTitle { get; set; }
            public string Status { get; set; }
            public Font PreviewFont { get; set; }
            public Font DateFont { get; set; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED，降低闪烁
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW 投影
                return cp;
            }
        }

        public history(string path)
        {
            folderPath = path;
            BackColor = formBackColor;
            InitializeComponent();

            // 统一 chrome + 主题色 + 边缘拉伸命中环
            var th = NoteTheme.Current();
            themeBodyColor = th.Body;
            formBackColor = Color.White; // 列表底用白，卡片用主题色（仿便笺）
            BackColor = formBackColor;
            chromeBar = FormChrome.Apply(this, "便签列表", true,
                (s, e) => Program.AppContext.AddNewForm3(), th.Chrome, th.ChromeText);
            this.Padding = new Padding(5);

            SetFormPosition();

            searchDebounceTimer.Interval = 120;
            searchDebounceTimer.Tick += (s, e) =>
            {
                searchDebounceTimer.Stop();
                ApplySearchFilter();
            };

            MouseWheel += GlobalMouseWheel;
            panel1.MouseWheel += GlobalMouseWheel;

            Resize += (s, e) => AdjustLayoutForDpi();
            refreshTimer.Tick += (s, e) => { refreshTimer.Stop(); RefreshCardList(); };
            Storage.NotesChanged += QueueRefresh;
            displayedUserId = CloudStore.UserId;
            statusTimer.Tick += (s, e) => { statusTimer.Stop(); RefreshSyncLabels(); };
            ObserveCloud();
            Activated += (s, e) => QueueRefresh();
            FormClosed += (s, e) => ReleaseResources();
        }

        private void ReleaseResources()
        {
            if (resourcesReleased) return;
            resourcesReleased = true;
            Storage.NotesChanged -= QueueRefresh;
            if (observedCloud != null) observedCloud.Changed -= CloudChanged;
            indexingCancel?.Cancel();
            indexGeneration++;
            statusTimer.Stop();
            statusTimer.Dispose();
            syncTips.Dispose();
            refreshTimer.Stop();
            searchDebounceTimer.Stop();
            refreshTimer.Dispose();
            searchDebounceTimer.Dispose();
        }

        private void QueueRefresh()
        {
            if (IsDisposed || !IsHandleCreated || resourcesReleased) return;
            if (InvokeRequired) { PostToUi(QueueRefresh); return; }
            ObserveCloud();
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        private int Dpi(int value) => (int)Math.Round(value * (DeviceDpi / 96f));
        private bool dpiRefreshQueued;

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _probeLineH = -1;
            if (!IsHandleCreated || searchBox == null || dpiRefreshQueued) return;
            dpiRefreshQueued = true;
            BeginInvoke((Action)(() =>
            {
                dpiRefreshQueued = false;
                if (IsDisposed || resourcesReleased) return;
                foreach (var item in allCards)
                {
                    if (item.Card != null)
                    {
                        item.PreviewBox.Font = item.PreviewFont;
                        item.TimeLabel.Font = item.DateFont;
                    }
                    SetPreviewContent(item);
                }
                AdjustLayoutForDpi();
            }));
        }

        // 无边框边缘拉伸
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84)
            {
                int lp = m.LParam.ToInt32();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                int code = FormChrome.HitTest(this, this.PointToClient(new Point(x, y)), Math.Max(this.Padding.Left, 4));
                if (code != 0) m.Result = (IntPtr)code;
            }
        }

        // 顶部命中环涂成标题栏同色，无缝覆盖
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int band = this.Padding.Top + (chromeBar != null ? chromeBar.Height : 0);
            if (band > 0)
            {
                using (var b = new SolidBrush(chromeBar != null ? chromeBar.BackColor : formBackColor))
                    e.Graphics.FillRectangle(b, 0, 0, this.ClientSize.Width, band);
            }
        }

        private void Form2_Load(object sender, EventArgs e)
        {
            EnableDoubleBuffer(panel1);

            // 自包含搜索组件：圆角灰底/聚焦变白/占位符/内嵌线稿放大镜
            searchBox = new SearchBox { Placeholder = "搜索历史便签" };
            searchBox.TextChanged += SearchTextChanged;
            searchBox.Input.MouseWheel += GlobalMouseWheel;
            this.Controls.Add(searchBox);
            searchBox.BringToFront();

            panel1.AutoScroll = false;
            panel1.HorizontalScroll.Enabled = false;
            panel1.HorizontalScroll.Visible = false;
            panel1.VerticalScroll.Enabled = false;
            panel1.VerticalScroll.Visible = false;
            panel1.BorderStyle = BorderStyle.None;
            panel1.Padding = new Padding(0);
            panel1.BackColor = formBackColor;
            panel1.Anchor = AnchorStyles.None;

            AdjustLayoutForDpi();
            LoadHistoryItems();
            ActiveControl = panel1;
        }

        private void AdjustLayoutForDpi()
        {
            if (searchBox == null || panel1 == null) return; // 构造期 Resize 早于 Load，控件尚未创建
            // 全部按 chrome 底边确定性计算，避免与标题栏重叠或放大镜出框
            int ring = this.Padding.Left;
            int left = ring + Dpi(8);
            int availW = this.ClientSize.Width - ring * 2 - Dpi(16);
            int top = (chromeBar != null ? chromeBar.Bottom : ring) + Dpi(12);

            searchBox.Height = Dpi(34);
            searchBox.Location = new Point(left, top);
            searchBox.Width = availW;

            int panelTop = searchBox.Bottom + Dpi(12);
            panel1.Location = new Point(left, panelTop);
            panel1.Width = availW;
            panel1.Height = Math.Max(0, this.ClientSize.Height - ring - Dpi(12) - panelTop);

            RelayoutCards();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AdjustLayoutForDpi();
        }

        private void SetFormPosition()
        {
            var screenBounds = Screen.PrimaryScreen.WorkingArea;
            int x = screenBounds.Width * 3 / 4 - Width;
            int y = (screenBounds.Height - Height) / 2;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }

        private void GlobalMouseWheel(object sender, MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs handled)
            {
                if (handled.Handled) return;
                handled.Handled = true;
            }
            if (e.Delta == 0) return;
            ScrollPanelByWheel(e.Delta);
        }

        private void ScrollPanelByWheel(int delta)
        {
            if (contentHeight <= panel1.ClientSize.Height)
            {
                scrollOffset = 0;
                RelayoutCards();
                return;
            }

            int step = Dpi(42);
            scrollOffset += (delta > 0 ? step : -step);

            int minOffset = panel1.ClientSize.Height - contentHeight;
            if (minOffset > 0) minOffset = 0;

            if (scrollOffset > 0) scrollOffset = 0;
            if (scrollOffset < minOffset) scrollOffset = minOffset;

            RelayoutCards();
        }

        private void ObserveCloud()
        {
            if (observedCloud == CloudSync.Current) return;
            if (observedCloud != null) observedCloud.Changed -= CloudChanged;
            observedCloud = CloudSync.Current;
            if (observedCloud != null) observedCloud.Changed += CloudChanged;
        }

        private void CloudChanged()
        {
            if (resourcesReleased || IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { PostToUi(CloudChanged); return; }
            if (displayedUserId != CloudStore.UserId)
            {
                // Never leave the previous account's previews visible during a reload.
                displayedUserId = CloudStore.UserId;
                indexingCancel?.Cancel(); indexGeneration++; previewLoading = false;
                foreach (var item in allCards) item.Card?.Dispose();
                allCards.Clear(); scrollOffset = 0; contentHeight = 0;
                QueueRefresh();
            }
            statusTimer.Stop(); statusTimer.Start();
        }

        private void RefreshSyncLabels()
        {
            foreach (var item in allCards)
            {
                if (item.Card == null || !item.Card.Visible) continue;
                var state = observedCloud?.NoteStatus(item.FileName);
                item.Status = state;
                string text = item.File.Modified.ToString("yyyy-MM-dd") + (state == null ? "" : " · " + state);
                if (item.TimeLabel.Text != text) item.TimeLabel.Text = text;
                item.TimeLabel.ForeColor = state == "同步失败" || state == "需重新登录" ? Color.FromArgb(169, 79, 55) : mutedColor;
                syncTips.SetToolTip(item.TimeLabel, observedCloud?.NoteError(item.FileName) ?? state);
            }
        }

        private void PostToUi(Action action)
        {
            try
            {
                if (!resourcesReleased && !IsDisposed && IsHandleCreated)
                    BeginInvoke((Action)(() => { if (!resourcesReleased && !IsDisposed) action(); }));
            }
            catch (InvalidOperationException) { /* The window closed between checking and posting. */ }
        }

        private void LoadHistoryItems()
        {
            ObserveCloud();
            if (previewLoading) { refreshAfterIndexing = true; return; }
            previewLoading = true; refreshAfterIndexing = false;
            int generation = ++indexGeneration;
            string owner = CloudStore.UserId;
            indexingCancel?.Dispose();
            var cancel = indexingCancel = new System.Threading.CancellationTokenSource();
            var worker = new System.Threading.Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(folderPath);
                    var index = NoteIndex.Load();
                    var snapshots = new List<HistoryFile>();
                    foreach (var path in Directory.GetFiles(folderPath, "*.rtf"))
                    {
                        if (cancel.IsCancellationRequested) return;
                        try
                        {
                            var info = new FileInfo(path);
                            var meta = new FileInfo(path + ".meta.json");
                            NoteIndex.Entry preview;
                            index.TryGet(path, info.LastWriteTime, info.Length, out preview);
                            snapshots.Add(new HistoryFile { Path = path, Name = info.Name, Modified = info.LastWriteTime,
                                Length = info.Length, Preview = preview,
                                MetadataStamp = meta.Exists ? meta.LastWriteTimeUtc.Ticks + ":" + meta.Length : "" });
                        }
                        catch (IOException) { /* The note may have been atomically replaced/deleted during enumeration. */ }
                        catch (UnauthorizedAccessException error) { Program.LogError(error); }
                    }
                    var files = snapshots.OrderByDescending(file => file.Modified).ToArray();
                    if (cancel.IsCancellationRequested) return;
                    PostToUi(() => { if (generation == indexGeneration && owner == CloudStore.UserId) ApplyFileSnapshot(files); });
                    using (var reader = new RichTextBox())
                    {
                        var completed = new List<KeyValuePair<HistoryFile, NoteIndex.Entry>>();
                        var lastPublish = DateTime.UtcNow;
                        bool first = true;
                        Action publish = () =>
                        {
                            if (completed.Count == 0) return;
                            var batch = completed.ToArray(); completed.Clear(); lastPublish = DateTime.UtcNow;
                            PostToUi(() =>
                            {
                                if (generation != indexGeneration || owner != CloudStore.UserId) return;
                                foreach (var result in batch) ApplyPreview(result.Key, result.Value);
                                UpdateSearchFilter(false);
                            });
                        };
                        foreach (var file in files)
                        {
                            if (cancel.IsCancellationRequested) return;
                            if (file.Preview != null) continue;
                            NoteIndex.Entry result;
                            try
                            {
                                result = NoteIndex.ReadPreview(reader, file.Path, file.Modified, file.Length);
                                var latest = new FileInfo(file.Path);
                                if (!latest.Exists || latest.LastWriteTime != file.Modified || latest.Length != file.Length)
                                { PostToUi(QueueRefresh); continue; }
                                index.Set(file.Path, result);
                            }
                            catch (Exception error)
                            {
                                Program.LogError(error);
                                result = NoteIndex.FromText("（读取失败）", file.Modified, file.Length);
                            }
                            completed.Add(new KeyValuePair<HistoryFile, NoteIndex.Entry>(file, result));
                            if (first || completed.Count >= 16 || DateTime.UtcNow - lastPublish >= TimeSpan.FromMilliseconds(50))
                            { publish(); first = false; }
                        }
                        publish();
                    }
                    if (!cancel.IsCancellationRequested) index.Save();
                }
                catch (Exception error) { Program.LogError(error); }
                finally
                {
                    PostToUi(() =>
                    {
                        if (generation != indexGeneration) return;
                        previewLoading = false;
                        if (refreshAfterIndexing) QueueRefresh();
                    });
                }
            }) { IsBackground = true, Name = "KimNotes history index" };
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
        }

        private void ApplyFileSnapshot(HistoryFile[] files)
        {
            var previous = allCards.ToDictionary(item => item.FileName, StringComparer.OrdinalIgnoreCase);
            var next = new List<HistoryCardMeta>();
            foreach (var file in files)
            {
                if (!CloudStore.CanShow(file.Path)) continue;
                HistoryCardMeta item;
                if (!previous.TryGetValue(file.Name, out item)) item = new HistoryCardMeta { FileName = file.Name, IsFilteredIn = true };
                else previous.Remove(file.Name);
                bool themeChanged = item.File == null || item.File.MetadataStamp != file.MetadataStamp;
                item.File = file;
                if (themeChanged) item.BodyColor = item.Card == null ? Color.Empty : ReadCardColor(file.Path);
                SetPreviewContent(item);
                next.Add(item);
            }
            foreach (var removed in previous.Values) removed.Card?.Dispose();
            allCards.Clear(); allCards.AddRange(next);
            UpdateSearchFilter(false);
        }

        private void ApplyPreview(HistoryFile file, NoteIndex.Entry preview)
        {
            var item = allCards.FirstOrDefault(card => card.FileName == file.Name);
            if (item == null || item.File.Modified != file.Modified || item.File.Length != file.Length || !CloudStore.CanShow(file.Path)) return;
            item.File.Preview = preview;
            SetPreviewContent(item);
        }

        private void SetPreviewContent(HistoryCardMeta item)
        {
            var preview = item.File.Preview;
            item.DisplayTitle = preview == null ? "正在加载…" : observedCloud?.DisplayTitle(item.FileName, preview.Title) ?? preview.Title;
            string content = item.DisplayTitle + (string.IsNullOrEmpty(preview?.Summary) ? "" : "\n" + preview.Summary);
            item.SearchText = preview?.SearchText ?? "";
            int lines = 1 + (string.IsNullOrEmpty(preview?.Summary) ? 0 : preview.Summary.Split('\n').Length);
            item.CardHeight = Dpi(8 + 18 + 12) + lines * RealLineHeight();
            if (item.Card == null) return;
            item.Card.BackColor = item.BodyColor;
            item.PreviewBox.BackColor = item.BodyColor;
            if (item.PreviewBox.Text != content) item.PreviewBox.Text = content;
        }

        private void CreateHistoryCard(HistoryCardMeta item)
        {
            string fileName = item.FileName;
            if (item.BodyColor.IsEmpty) item.BodyColor = ReadCardColor(item.File.Path);
            var card = new Panel
            {
                Height = item.CardHeight, Width = panel1.ClientSize.Width - Dpi(4),
                BackColor = item.BodyColor, BorderStyle = BorderStyle.None,
                Cursor = Cursors.Default, Margin = Padding.Empty, Padding = Padding.Empty, Tag = fileName
            };
            EnableDoubleBuffer(card);
            card.Paint += (s, e) =>
            {
                int f = Dpi(12);
                var pts = new[] { new Point(card.Width - f, card.Height), new Point(card.Width, card.Height - f), new Point(card.Width, card.Height) };
                using (var brush = new SolidBrush(ControlPaint.Dark(item.BodyColor, 0.12f))) e.Graphics.FillPolygon(brush, pts);
            };
            var timeLabel = new Label
            {
                AutoSize = false, Height = Dpi(18), Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular),
                ForeColor = mutedColor, TextAlign = ContentAlignment.TopRight, Cursor = Cursors.Default,
                AutoEllipsis = true
            };
            var dateFont = timeLabel.Font;
            timeLabel.Disposed += (s, e) => dateFont.Dispose();
            var contentBox = MakeTextBox("", false, RealLineHeight());
            card.Controls.Add(contentBox); card.Controls.Add(timeLabel);
            AttachCardInteraction(card, fileName);
            AttachCardInteraction(contentBox, fileName);
            AttachCardInteraction(timeLabel, fileName);
            var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
            menu.Items.Add(new ToolStripMenuItem("打开便签", null, (s, ev) => OpenNote(fileName)));
            menu.Items.Add(new ToolStripMenuItem("删除便签", null, (s, ev) => DeleteNote(fileName)));
            card.ContextMenuStrip = contentBox.ContextMenuStrip = timeLabel.ContextMenuStrip = menu;
            card.Disposed += (s, e) => menu.Dispose();
            item.Card = card; item.PreviewBox = contentBox; item.TimeLabel = timeLabel;
            item.PreviewFont = contentBox.Font; item.DateFont = dateFont;
            SetPreviewContent(item);
            panel1.Controls.Add(card);
            ApplyHighlight(item, currentKeyword);
        }

        private Color ReadCardColor(string path)
        {
            int themeId = Storage.Metadata(path).ThemeId;
            return (NoteTheme.All.FirstOrDefault(theme => theme.Id == themeId) ?? NoteTheme.Current()).Body;
        }

        // 用隐藏探针实测 RichTextBox 真实行高（估算值会切字）
        private int _probeLineH = -1;
        private int RealLineHeight()
        {
            if (_probeLineH > 0) return _probeLineH;
            using (var font = new Font("Microsoft YaHei UI", 9f))
            using (var probe = new RichTextBox
            {
                Font = font,
                Parent = panel1,
                Visible = false,
                Multiline = true,
                WordWrap = false,
                Text = "A\nA"
            })
            {
                int y0 = probe.GetPositionFromCharIndex(0).Y;
                int y1 = probe.GetPositionFromCharIndex(2).Y;
                _probeLineH = Math.Max(y1 - y0, probe.Font.Height);
            }
            return _probeLineH;
        }

        private RichTextBox MakeTextBox(string text, bool isTitle, int height)
        {
            var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            var box = new RichTextBox
            {
                Text = text,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = themeBodyColor,
                ForeColor = titleColor,
                Font = font,
                ScrollBars = RichTextBoxScrollBars.None,
                WordWrap = true,
                Multiline = true,
                HideSelection = true,
                TabStop = false,
                ShortcutsEnabled = false,
                DetectUrls = false,
                Cursor = Cursors.Default,
                Height = Math.Max(height, Dpi(8))
            };
            box.Disposed += (s, e) => font.Dispose();
            return box;
        }

        // 在内容里高亮搜索关键词；baseColor 需与卡片当前底色一致（悬停会变）
        private void ApplyHighlight(HistoryCardMeta item, string keyword)
        {
            if (item.Card != null) HighlightBox(item.PreviewBox, keyword, item.Card.BackColor);
        }

        private void HighlightBox(RichTextBox box, string keyword, Color baseColor)
        {
            if (box == null || box.IsDisposed) return;

            box.SelectAll();
            box.SelectionBackColor = baseColor;
            box.SelectionColor = box.ForeColor;

            if (!string.IsNullOrEmpty(keyword))
            {
                string text = box.Text ?? "";
                int idx = 0;
                while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    box.Select(idx, keyword.Length);
                    box.SelectionBackColor = highlightColor;
                    idx += keyword.Length;
                }
            }

            box.SelectionLength = 0;
            box.DeselectAll();
        }

        private void AttachCardInteraction(Control control, string fileName)
        {
            control.Cursor = Cursors.Default;
            control.MouseEnter += (s, e) => SetCardHover(control, true);
            control.MouseLeave += (s, e) => SetCardHover(control, false);
            control.MouseDoubleClick += (s, e) => OpenNote(fileName);
            control.MouseWheel += GlobalMouseWheel;
        }

        private void SetCardHover(Control control, bool hover)
        {
            Panel card = control as Panel ?? control.Parent as Panel;
            if (card == null) return;
            var meta = allCards.FirstOrDefault(m => m.Card == card);
            var color = meta?.BodyColor ?? themeBodyColor;
            var back = hover ? ControlPaint.Dark(color, 0.05f) : color;
            card.BackColor = back;
            foreach (Control c in card.Controls)
            {
                if (c is RichTextBox rtb) rtb.BackColor = back;
            }
            // 底色变了要重刷选区底色，否则露出旧色白块
            if (meta != null) ApplyHighlight(meta, currentKeyword);
        }

        private void OpenNote(string fileName)
        {
            Program.AppContext.AddNewForm(fileName);
        }

        private void DeleteNote(string fileName)
        {
            try
            {
                string filePath = Path.Combine(folderPath, fileName);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                foreach (var opened in Application.OpenForms.OfType<note>().Where(n =>
                    string.Equals(n.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)).ToArray())
                    opened.DiscardAndClose();
                foreach (var suffix in new[] { ".bak", ".meta.json", ".meta.json.bak" })
                    if (File.Exists(filePath + suffix)) File.Delete(filePath + suffix);
                CloudSync.Current?.Deleted(filePath);
                var index = NoteIndex.Load();
                index.Remove(filePath);
                System.Threading.ThreadPool.QueueUserWorkItem(_ => index.Save());
                indexingCancel?.Cancel(); indexGeneration++; previewLoading = false;
                var removed = allCards.FirstOrDefault(item => item.FileName == fileName);
                if (removed != null) { allCards.Remove(removed); removed.Card?.Dispose(); }
                UpdateSearchFilter(false);
                RefreshCardList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除文件时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshCardList()
        {
            LoadHistoryItems();
        }

        private void RelayoutCards()
        {
            if (panel1.IsDisposed) return;

            panel1.SuspendLayout();
            contentHeight = Math.Max(0, allCards.Where(item => item.IsFilteredIn).Sum(item => item.CardHeight + Dpi(CARD_GAP)) - Dpi(CARD_GAP));
            scrollOffset = Math.Max(Math.Min(0, panel1.ClientSize.Height - contentHeight), Math.Min(0, scrollOffset));
            int naturalTop = 0;
            int width = Math.Max(0, panel1.ClientSize.Width - Dpi(4));

            foreach (var item in allCards)
            {
                if (!item.IsFilteredIn)
                {
                    if (item.Card != null) item.Card.Visible = false;
                    continue;
                }
                // Only visible cards need native RichEdit handles. Keep any existing card
                // for later reuse instead of rebuilding it on Activated or sync events.
                int actualTop = naturalTop + scrollOffset;
                bool inView = actualTop + item.CardHeight >= 0 && actualTop <= panel1.ClientSize.Height;
                if (inView && item.Card == null) CreateHistoryCard(item);
                if (item.Card != null)
                {
                    item.Card.Visible = inView;
                    if (inView)
                    {
                        item.Card.SetBounds(0, actualTop, width, item.CardHeight);
                        item.TimeLabel.SetBounds(Dpi(CARD_PADDING_H), Dpi(8), Math.Max(0, width - Dpi(CARD_PADDING_H * 2)), Dpi(18));
                        item.PreviewBox.SetBounds(Dpi(CARD_PADDING_H), Dpi(26), Math.Max(0, width - Dpi(CARD_PADDING_H * 2)), Math.Max(Dpi(8), item.CardHeight - Dpi(38)));
                    }
                }
                naturalTop += item.CardHeight + Dpi(CARD_GAP);
            }

            contentHeight = Math.Max(0, naturalTop - Dpi(CARD_GAP));

            panel1.ResumeLayout();
            RefreshSyncLabels();
        }

        private void ApplySearchFilter()
        {
            UpdateSearchFilter(true);
        }

        private void UpdateSearchFilter(bool resetScroll)
        {
            string keyword = (searchBox != null ? searchBox.Text : string.Empty) ?? string.Empty;
            keyword = keyword.Trim();
            string keywordLower = keyword.ToLowerInvariant();
            currentKeyword = keyword;

            panel1.SuspendLayout();
            foreach (var item in allCards)
            {
                item.IsFilteredIn = string.IsNullOrEmpty(keywordLower) || (item.SearchText ?? "").Contains(keywordLower);
                if (item.IsFilteredIn) ApplyHighlight(item, keywordLower);
            }
            panel1.ResumeLayout();

            if (resetScroll) scrollOffset = 0;
            RelayoutCards();
        }

        private void SearchTextChanged(object sender, EventArgs e)
        {
            searchDebounceTimer.Stop();
            searchDebounceTimer.Start();
        }

        private static void EnableDoubleBuffer(Control control)
        {
            control.GetType()
                .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(control, true, null);
        }

    }
}
