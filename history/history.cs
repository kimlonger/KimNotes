using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    public partial class history : AppWindow
    {
        // 视觉刷新配色（与 mockup / 主题统一）
        private Color formBackColor = Color.White;
        private readonly Color titleColor = Color.FromArgb(43, 47, 54);
        private readonly Color mutedColor = Color.FromArgb(138, 146, 158);
        private Panel chromeBar;
        private SearchBox searchBox;
        private string selectedFileName;

        private string folderPath = InitConfig.GetConfigValue("notesPath") ?? "";

        private const int CARD_GAP = 10;
        private const int CARD_PADDING_H = 13;
        private const int CARD_ACTION_TOP = 5;
        private const int CARD_ACTION_SIZE = 24;
        private const int CARD_PREVIEW_TOP = CARD_ACTION_TOP + CARD_ACTION_SIZE + 3;
        private const int CARD_BOTTOM_PADDING = 12;

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
            public HistoryCard Card { get; set; }
            public RichTextBox PreviewBox { get; set; }
            public Label TimeLabel { get; set; }
            public ActionIconButton OpenButton { get; set; }
            public ActionIconButton DeleteButton { get; set; }
            public ActionIconButton VersionsButton { get; set; }
            public string FileName { get; set; }
            public string SearchText { get; set; }
            public int CardHeight { get; set; }
            public bool IsFilteredIn { get; set; }
            public NoteTheme Theme { get; set; }
            public HistoryFile File { get; set; }
            public string DisplayTitle { get; set; }
            public string Status { get; set; }
            public string HighlightedKeyword { get; set; }
            public Font PreviewFont { get; set; }
            public Font DateFont { get; set; }
        }

        private sealed class HistoryCard : Panel
        {
            private bool hovered, selected;
            private NoteTheme theme;

            public HistoryCard()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            public void ApplyTheme(NoteTheme value)
            {
                if (theme == value) return;
                theme = value;
                BackColor = theme.Body;
                Invalidate();
            }

            public void SetHovered(bool value)
            {
                if (hovered == value) return;
                hovered = value;
                Invalidate();
            }

            public void SetSelected(bool value)
            {
                if (selected == value) return;
                selected = value;
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using (var surround = new SolidBrush(Parent?.BackColor ?? BackColor))
                    e.Graphics.FillRectangle(surround, e.ClipRectangle);
                if (theme == null || Width < 5 || Height < 5) return;

                var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
                int radius = Math.Min((int)Math.Round(9 * DeviceDpi / 96f), Math.Min(bounds.Width, bounds.Height) / 2);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = ToolIconButton.RoundedPath(bounds, radius))
                using (var surface = new SolidBrush(theme.Body))
                {
                    e.Graphics.FillPath(surface, path);
                    if (hovered || selected)
                        using (var pen = new Pen(selected ? theme.IconHover : theme.Divider)) e.Graphics.DrawPath(pen, path);
                }
            }
        }

        private sealed class HistoryPreview : RichTextBox
        {
            protected override void WndProc(ref Message m)
            {
                // Let the card handle clicks and scrolling without selecting preview text.
                if (m.Msg == 0x0084) // WM_NCHITTEST
                {
                    m.Result = new IntPtr(-1); // HTTRANSPARENT
                    return;
                }
                base.WndProc(ref m);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
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
            formBackColor = Color.White; // 列表底用白，卡片用主题色（仿便笺）
            BackColor = formBackColor;
            this.Padding = new Padding(5);
            chromeBar = FormChrome.ApplyStandardHeader(this, "便签列表", th);

            ClientSize = CompactLayout.DefaultNoteSize(UiDpi.Factor, Screen.PrimaryScreen.WorkingArea.Size);
            SetFormPosition();

            searchDebounceTimer.Interval = 120;
            searchDebounceTimer.Tick += (s, e) =>
            {
                searchDebounceTimer.Stop();
                ApplySearchFilter();
            };

            MouseWheel += GlobalMouseWheel;
            panel1.MouseWheel += GlobalMouseWheel;

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
            CloseVersions();
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
        private int historyLayoutDpi;

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _probeLineH = -1;
            if (!IsHandleCreated || searchBox == null || dpiRefreshQueued) return;
            dpiRefreshQueued = true;
            try { BeginInvoke((Action)(() =>
            {
                dpiRefreshQueued = false;
                if (IsDisposed || resourcesReleased) return;
                int newDpi = DeviceDpi > 0 ? DeviceDpi : 96;
                if (historyLayoutDpi > 0 && historyLayoutDpi != newDpi)
                    scrollOffset = (int)Math.Round(scrollOffset * (double)newDpi / historyLayoutDpi);
                historyLayoutDpi = newDpi;
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
            })); }
            catch (InvalidOperationException) { dpiRefreshQueued = false; }
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
            historyLayoutDpi = DeviceDpi > 0 ? DeviceDpi : 96;
            EnableDoubleBuffer(panel1);

            searchBox = new SearchBox { Placeholder = "搜索历史便签", AccessibleName = "搜索历史便签" };
            searchBox.Input.AccessibleName = searchBox.AccessibleName;
            searchBox.UseOutlineTheme(NoteTheme.Current(), formBackColor);
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
            int availW = Math.Max(0, this.ClientSize.Width - ring * 2 - Dpi(16));
            int top = (chromeBar != null ? chromeBar.Bottom : ring) + Dpi(12);

            searchBox.SetBounds(left, top, availW, Dpi(34));

            int panelTop = searchBox.Bottom + Dpi(12);
            panel1.SetBounds(left, panelTop, availW, Math.Max(0, this.ClientSize.Height - ring - Dpi(12) - panelTop));
            versionsPanel?.SetBounds(left, top, availW, Math.Max(0, this.ClientSize.Height - ring - Dpi(12) - top));

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
            int x = screenBounds.Left + Math.Max(0, screenBounds.Width * 3 / 4 - Width);
            int y = screenBounds.Top + Math.Max(0, (screenBounds.Height - Height) / 2);
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
                selectedFileName = null;
                CloseVersions();
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
                string detail = observedCloud?.NoteError(item.FileName) ?? state;
                string source = observedCloud?.NoteSourceDevice(item.FileName);
                if (source != null)
                {
                    string noteOrigin = "来源设备：" + source;
                    detail = string.IsNullOrEmpty(detail) ? noteOrigin : detail + Environment.NewLine + noteOrigin;
                }
                syncTips.SetToolTip(item.TimeLabel, detail);
                item.TimeLabel.AccessibleDescription = detail;
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
                if (themeChanged)
                {
                    item.Theme = item.Card == null ? null : ReadCardTheme(file.Path);
                    item.HighlightedKeyword = null;
                }
                SetPreviewContent(item);
                next.Add(item);
            }
            foreach (var removed in previous.Values) removed.Card?.Dispose();
            if (selectedFileName != null && previous.ContainsKey(selectedFileName)) selectedFileName = null;
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
            item.CardHeight = Dpi(CARD_PREVIEW_TOP) + lines * RealLineHeight() + Dpi(CARD_BOTTOM_PADDING);
            if (item.Card == null) return;
            item.Card.ApplyTheme(item.Theme);
            item.PreviewBox.BackColor = item.Theme.Body;
            item.VersionsButton.Theme = item.OpenButton.Theme = item.DeleteButton.Theme = item.Theme;
            item.VersionsButton.BackColor = item.OpenButton.BackColor = item.DeleteButton.BackColor = item.Theme.Body;
            if (item.PreviewBox.Text != content)
            {
                item.PreviewBox.Text = content;
                item.HighlightedKeyword = null;
            }
            if (item.Card.Parent != null) ApplyHighlight(item, currentKeyword);
        }

        private void CreateHistoryCard(HistoryCardMeta item)
        {
            string fileName = item.FileName;
            if (item.Theme == null) item.Theme = ReadCardTheme(item.File.Path);
            var card = new HistoryCard
            {
                Height = item.CardHeight, Width = panel1.ClientSize.Width - Dpi(4),
                Cursor = Cursors.Default, Margin = Padding.Empty, Padding = Padding.Empty, Tag = fileName
            };
            card.ApplyTheme(item.Theme);
            card.SetSelected(string.Equals(selectedFileName, fileName, StringComparison.OrdinalIgnoreCase));
            var timeLabel = new Label
            {
                AutoSize = false, Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular),
                ForeColor = mutedColor, TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Default,
                AutoEllipsis = true
            };
            var dateFont = timeLabel.Font;
            timeLabel.Disposed += (s, e) =>
            {
                if (!resourcesReleased) syncTips.SetToolTip(timeLabel, null);
                dateFont.Dispose();
            };
            var contentBox = MakeTextBox(item.Theme.Body, RealLineHeight());
            card.Controls.Add(contentBox); card.Controls.Add(timeLabel);
            AttachCardInteraction(card, card, fileName);
            AttachCardInteraction(timeLabel, card, fileName);
            item.VersionsButton = AddCardAction(card, "clock", "历史版本", () => OpenVersions(item));
            item.OpenButton = AddCardAction(card, "open-note", "打开便签", () => OpenNote(fileName));
            item.DeleteButton = AddCardAction(card, "trash", "删除便签", () =>
            {
                var choice = MessageBox.Show(this, "确定删除这条便签吗？", "删除便签",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (choice == DialogResult.Yes && !card.IsDisposed) DeleteNote(fileName);
            });
            item.Card = card; item.PreviewBox = contentBox; item.TimeLabel = timeLabel;
            item.PreviewFont = contentBox.Font; item.DateFont = dateFont;
            SetPreviewContent(item);
            panel1.Controls.Add(card);
            ApplyHighlight(item, currentKeyword);
        }

        private ActionIconButton AddCardAction(HistoryCard card, string icon, string label, Action action)
        {
            var button = new ActionIconButton(action) { IconId = icon, IconSize = 16, AccessibleName = label };
            button.MouseEnter += (s, e) => card.SetHovered(true);
            button.MouseLeave += (s, e) => card.SetHovered(card.ClientRectangle.Contains(card.PointToClient(Cursor.Position)));
            button.MouseWheel += GlobalMouseWheel;
            button.Disposed += (s, e) => { if (!resourcesReleased) syncTips.SetToolTip(button, null); };
            card.Controls.Add(button);
            syncTips.SetToolTip(button, label);
            return button;
        }

        private NoteTheme ReadCardTheme(string path)
        {
            int themeId = Storage.Metadata(path).ThemeId;
            return NoteTheme.All.FirstOrDefault(theme => theme.Id == themeId) ?? NoteTheme.Current();
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
                // The list is visible by the time asynchronous indexing measures the first card.
                // Hide the native editor before attaching it to the list.
                Visible = false,
                Multiline = true,
                WordWrap = false,
                Text = "A\nA",
                Parent = panel1
            })
            {
                int y0 = probe.GetPositionFromCharIndex(0).Y;
                int y1 = probe.GetPositionFromCharIndex(2).Y;
                _probeLineH = Math.Max(y1 - y0, probe.Font.Height);
            }
            return _probeLineH;
        }

        private RichTextBox MakeTextBox(Color surface, int height)
        {
            var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            var box = new HistoryPreview
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = surface,
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

        // Search matches use the note's accent in the text, without a fill behind it.
        private void ApplyHighlight(HistoryCardMeta item, string keyword)
        {
            if (item.Card == null || string.Equals(item.HighlightedKeyword, keyword, StringComparison.OrdinalIgnoreCase)) return;
            HighlightBox(item.PreviewBox, keyword, ControlPaint.Dark(item.Theme.IconHover, 0.15f));
            item.HighlightedKeyword = keyword;
        }

        private void HighlightBox(RichTextBox box, string keyword, Color matchColor)
        {
            if (box == null || box.IsDisposed) return;

            box.SelectAll();
            box.SelectionColor = box.ForeColor;

            if (!string.IsNullOrEmpty(keyword))
            {
                string text = box.Text ?? "";
                int idx = 0;
                while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    box.Select(idx, keyword.Length);
                    box.SelectionColor = matchColor;
                    idx += keyword.Length;
                }
            }

            box.Select(0, 0);
        }

        private void AttachCardInteraction(Control control, HistoryCard card, string fileName)
        {
            control.Cursor = Cursors.Default;
            control.MouseEnter += (s, e) => card.SetHovered(true);
            control.MouseLeave += (s, e) => card.SetHovered(card.ClientRectangle.Contains(card.PointToClient(Cursor.Position)));
            control.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SelectCard(card, fileName); };
            control.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenNote(fileName); };
            control.MouseWheel += GlobalMouseWheel;
        }

        private void SelectCard(HistoryCard card, string fileName)
        {
            if (string.Equals(selectedFileName, fileName, StringComparison.OrdinalIgnoreCase)) return;
            allCards.FirstOrDefault(item => string.Equals(item.FileName, selectedFileName, StringComparison.OrdinalIgnoreCase))?.Card?.SetSelected(false);
            selectedFileName = fileName;
            card.SetSelected(true);
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
                if (string.Equals(selectedFileName, fileName, StringComparison.OrdinalIgnoreCase)) selectedFileName = null;
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
                        LayoutCard(item, width, actualTop);
                    }
                }
                naturalTop += item.CardHeight + Dpi(CARD_GAP);
            }

            contentHeight = Math.Max(0, naturalTop - Dpi(CARD_GAP));

            panel1.ResumeLayout();
            RefreshSyncLabels();
        }

        private void LayoutCard(HistoryCardMeta item, int width, int actualTop)
        {
            int padding = Dpi(CARD_PADDING_H);
            int actionGap = Dpi(2);
            int headerTop = Dpi(CARD_ACTION_TOP);
            int headerHeight = Dpi(CARD_ACTION_SIZE);
            int actionSize = Math.Min(headerHeight, Math.Max(0, (width - padding * 2 - actionGap * 2) / 3));
            int actionTop = headerTop + (headerHeight - actionSize) / 2;
            int deleteLeft = width - padding - actionSize;
            int openLeft = deleteLeft - actionGap - actionSize;
            int versionsLeft = openLeft - actionGap - actionSize;
            item.VersionsButton.SetBounds(versionsLeft, actionTop, actionSize, actionSize);
            item.OpenButton.SetBounds(openLeft, actionTop, actionSize, actionSize);
            item.DeleteButton.SetBounds(deleteLeft, actionTop, actionSize, actionSize);
            item.TimeLabel.SetBounds(padding, headerTop, Math.Max(0, versionsLeft - Dpi(6) - padding), headerHeight);

            // The viewport clips partially scrolled rows. Tab only to actions it shows in full.
            bool canTab = actionSize > 0 && actualTop + actionTop >= 0
                && actualTop + actionTop + actionSize <= panel1.ClientSize.Height;
            item.VersionsButton.TabStop = item.OpenButton.TabStop = item.DeleteButton.TabStop = canTab;

            int previewTop = Dpi(CARD_PREVIEW_TOP);
            item.PreviewBox.SetBounds(padding, previewTop, Math.Max(0, width - padding * 2),
                Math.Max(Dpi(8), item.CardHeight - previewTop - Dpi(CARD_BOTTOM_PADDING)));
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
                item.IsFilteredIn = item.File.Preview?.VerifiedEmpty != true
                    && (string.IsNullOrEmpty(keywordLower) || (item.SearchText ?? "").Contains(keywordLower));
                if (item.IsFilteredIn) ApplyHighlight(item, keyword);
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
