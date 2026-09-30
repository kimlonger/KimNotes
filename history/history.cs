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
        private int scrollOffset = 0;
        private int contentHeight = 0;
        private string currentKeyword = "";

        private sealed class HistoryCardMeta
        {
            public Panel Card { get; set; }
            public RichTextBox PreviewBox { get; set; }
            public Label TimeLabel { get; set; }
            public string FileName { get; set; }
            public string SearchText { get; set; }
            public int CardHeight { get; set; }
            public bool IsFilteredIn { get; set; }
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
        }

        private int Dpi(int value) => (int)Math.Round(value * (DeviceDpi / 96f));
        private float DpiF(float value) => value * (DeviceDpi / 96f);

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

            searchBox.Height = Dpi(44);
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

        private void LoadHistoryItems()
        {
            panel1.SuspendLayout();
            panel1.Controls.Clear();
            allCards.Clear();

            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            var rtfFiles = Directory.GetFiles(folderPath, "*.rtf")
                .OrderByDescending(file => File.GetLastWriteTime(file))
                .ToArray();

            var index = NoteIndex.Load();
            index.PruneTo(rtfFiles.Select(Path.GetFileName));
            bool indexChanged = false;

            using (var reader = new RichTextBox())
            {
                foreach (var file in rtfFiles)
                {
                    string fileName = Path.GetFileName(file);
                    DateTime modified = File.GetLastWriteTime(file);

                    string title;
                    string summary;
                    if (!index.TryGet(fileName, modified, out title, out summary))
                    {
                        var lines = ReadPreviewLines(reader, file, 5);
                        title = lines.Length > 0 ? lines[0] : "（空便签）";
                        summary = lines.Length > 1
                            ? string.Join("\n", lines.Skip(1).Take(4))
                            : "";
                        index.Set(fileName, modified, title, summary);
                        indexChanged = true;
                    }

                    var card = CreateHistoryCard(fileName, title, summary, modified);
                    panel1.Controls.Add(card.Card);
                    allCards.Add(card);
                }
            }

            if (indexChanged) index.Save();

            panel1.ResumeLayout();
            RelayoutCards();
        }

        private string[] ReadPreviewLines(RichTextBox reader, string file, int maxLines)
        {
            try
            {
                reader.Clear();
                reader.LoadFile(file, RichTextBoxStreamType.RichText);

                var lines = reader.Text
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Select(x => x.TrimEnd())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Take(maxLines)
                    .ToArray();

                return lines;
            }
            catch
            {
                return new[] { "（读取失败）" };
            }
        }

        private HistoryCardMeta CreateHistoryCard(string fileName, string title, string summary, DateTime modified)
        {
            // 仿便笺列表：卡片=主题色块，右上日期小字，下方直接铺内容行，右下折角装饰
            string content = string.IsNullOrEmpty(summary) ? title : title + "\n" + summary;
            int lineCount = 1 + (string.IsNullOrEmpty(summary) ? 0 : summary.Split('\n').Length);
            int lineHeight = RealLineHeight(); // 实测行高，避免文字被卡片底边切半
            int dateH = Dpi(18);
            int padTop = Dpi(8);
            int contentTop = padTop + dateH;
            int contentHeight = lineCount * lineHeight;
            int cardHeight = contentTop + contentHeight + Dpi(12);

            var card = new Panel
            {
                Height = cardHeight,
                Width = panel1.ClientSize.Width - Dpi(4),
                BackColor = themeBodyColor,
                BorderStyle = BorderStyle.None,
                Cursor = Cursors.Default,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Tag = fileName
            };
            EnableDoubleBuffer(card);
            card.Paint += (s, e) =>
            {
                int f = Dpi(12);
                var pts = new Point[]
                {
                    new Point(card.Width - f, card.Height),
                    new Point(card.Width, card.Height - f),
                    new Point(card.Width, card.Height)
                };
                using (var brush = new SolidBrush(ControlPaint.Dark(themeBodyColor, 0.12f)))
                    e.Graphics.FillPolygon(brush, pts);
            };

            var timeLabel = new Label
            {
                AutoSize = false,
                Location = new Point(0, padTop),
                Width = Dpi(96),
                Height = dateH,
                Font = new Font("Microsoft YaHei UI", DpiF(8f), FontStyle.Regular),
                ForeColor = mutedColor,
                Text = modified.ToString("yyyy-MM-dd"),
                TextAlign = ContentAlignment.TopRight,
                Cursor = Cursors.Default
            };

            var contentBox = MakeTextBox(content, false, contentHeight);
            contentBox.Location = new Point(Dpi(CARD_PADDING_H), contentTop);

            card.Controls.Add(contentBox);
            card.Controls.Add(timeLabel);

            AttachCardInteraction(card, fileName);
            AttachCardInteraction(contentBox, fileName);
            AttachCardInteraction(timeLabel, fileName);

            var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
            menu.Items.Add(new ToolStripMenuItem("打开便签", null, (s, ev) => OpenNote(fileName)));
            menu.Items.Add(new ToolStripMenuItem("删除便签", null, (s, ev) => DeleteNote(fileName)));
            card.ContextMenuStrip = menu;
            contentBox.ContextMenuStrip = menu;
            timeLabel.ContextMenuStrip = menu;

            return new HistoryCardMeta
            {
                Card = card,
                PreviewBox = contentBox,
                TimeLabel = timeLabel,
                FileName = fileName,
                SearchText = content.ToLowerInvariant(),
                CardHeight = cardHeight,
                IsFilteredIn = true
            };
        }

        // 用隐藏探针实测 RichTextBox 真实行高（估算值会切字）
        private int _probeLineH = -1;
        private int RealLineHeight()
        {
            if (_probeLineH > 0) return _probeLineH;
            using (var probe = new RichTextBox
            {
                Font = new Font("Microsoft YaHei UI", DpiF(9f)),
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
            return new RichTextBox
            {
                Text = text,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = themeBodyColor,
                ForeColor = titleColor,
                Font = new Font("Microsoft YaHei UI", DpiF(9f), FontStyle.Regular),
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
        }

        // 在内容里高亮搜索关键词；baseColor 需与卡片当前底色一致（悬停会变）
        private void ApplyHighlight(HistoryCardMeta item, string keyword)
        {
            HighlightBox(item.PreviewBox, keyword, item.Card.BackColor);
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
            var back = hover ? ControlPaint.Dark(themeBodyColor, 0.05f) : themeBodyColor;
            card.BackColor = back;
            foreach (Control c in card.Controls)
            {
                if (c is RichTextBox rtb) rtb.BackColor = back;
            }
            // 底色变了要重刷选区底色，否则露出旧色白块
            var meta = allCards.FirstOrDefault(m => m.Card == card);
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
                var index = NoteIndex.Load();
                index.Remove(fileName);
                index.Save();
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
            ApplySearchFilter();
        }

        private void RelayoutCards()
        {
            if (panel1.IsDisposed) return;

            panel1.SuspendLayout();
            int naturalTop = 0;
            int width = Math.Max(0, panel1.ClientSize.Width - Dpi(4));

            foreach (var item in allCards)
            {
                if (!item.IsFilteredIn)
                {
                    item.Card.Visible = false;
                    continue;
                }

                item.Card.Width = width;
                item.Card.Height = item.CardHeight;

                // 日期右上；内容铺满卡片宽度
                int dateW = Dpi(96);
                item.TimeLabel.Width = dateW;
                item.TimeLabel.Left = item.Card.Width - Dpi(CARD_PADDING_H) - dateW;
                item.TimeLabel.Top = Dpi(8);
                item.PreviewBox.Width = item.Card.Width - Dpi(CARD_PADDING_H * 2);

                int actualTop = naturalTop + scrollOffset;
                bool inView = actualTop + item.Card.Height >= 0 && actualTop <= panel1.ClientSize.Height;

                item.Card.Visible = inView;
                if (inView)
                {
                    item.Card.Left = 0;
                    item.Card.Top = actualTop;
                }

                naturalTop += item.Card.Height + Dpi(CARD_GAP);
            }

            contentHeight = Math.Max(0, naturalTop - Dpi(CARD_GAP));

            panel1.ResumeLayout();
        }

        private void ApplySearchFilter()
        {
            string keyword = (searchBox != null ? searchBox.Text : string.Empty) ?? string.Empty;
            keyword = keyword.Trim();
            string keywordLower = keyword.ToLowerInvariant();
            currentKeyword = keyword;

            panel1.SuspendLayout();
            foreach (var item in allCards)
            {
                item.IsFilteredIn = string.IsNullOrEmpty(keywordLower) || item.SearchText.Contains(keywordLower);
                if (item.IsFilteredIn) ApplyHighlight(item, keywordLower);
            }
            panel1.ResumeLayout();

            scrollOffset = 0;
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
