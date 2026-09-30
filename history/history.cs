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
        private Color formBackColor = Color.FromArgb(234, 240, 247);
        private readonly Color searchBackColor = Color.White;
        private readonly Color searchTextColor = Color.FromArgb(43, 47, 54);
        private readonly Color cardBackColor = Color.White;
        private readonly Color cardBorderColor = Color.FromArgb(227, 232, 239);
        private readonly Color cardHoverColor = Color.FromArgb(242, 246, 251);
        private readonly Color titleColor = Color.FromArgb(43, 47, 54);
        private readonly Color mutedColor = Color.FromArgb(138, 146, 158);
        private readonly Color highlightColor = Color.FromArgb(255, 224, 138);
        private Panel chromeBar;

        private string folderPath = InitConfig.GetConfigValue("notesPath") ?? "";

        private const int SEARCH_BOX_HEIGHT = 34;
        private const int SEARCH_BOX_MARGIN = 12;
        private const int BOTTOM_MARGIN = 10;
        private const int CARD_GAP = 8;
        private const int CARD_PADDING_H = 13;
        private const int CARD_PADDING_TOP = 10;
        private const int CARD_PADDING_BOTTOM = 10;

        private readonly List<HistoryCardMeta> allCards = new List<HistoryCardMeta>();
        private readonly Timer searchDebounceTimer = new Timer();
        private int scrollOffset = 0;
        private int contentHeight = 0;
        private bool showingSearchPlaceholder = false;

        private sealed class HistoryCardMeta
        {
            public Panel Card { get; set; }
            public RichTextBox TitleBox { get; set; }
            public RichTextBox PreviewBox { get; set; }
            public Label TimeLabel { get; set; }
            public ToolIconButton Gear { get; set; }
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
            formBackColor = th.Body;
            BackColor = formBackColor;
            chromeBar = FormChrome.Apply(this, "便签列表", false, null, th.Chrome, th.ChromeText);
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
            textBox1.MouseWheel += GlobalMouseWheel;

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

            textBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            textBox1.Height = Dpi(SEARCH_BOX_HEIGHT);
            textBox1.Font = new Font("Microsoft YaHei UI", DpiF(9f));
            textBox1.BackColor = searchBackColor;
            textBox1.ForeColor = titleColor;
            textBox1.BorderStyle = BorderStyle.FixedSingle;
            textBox1.Location = new Point(Dpi(SEARCH_BOX_MARGIN), Dpi(SEARCH_BOX_MARGIN));
            textBox1.Cursor = Cursors.IBeam;

            textBox1.GotFocus += (s, ev) => ClearSearchPlaceholder();
            textBox1.LostFocus += (s, ev) => EnsureSearchPlaceholder();
            EnsureSearchPlaceholder();

            button1.Visible = true;
            button1.Enabled = true;
            button1.Text = "🔍";
            button1.Font = new Font("Segoe UI", DpiF(9f), FontStyle.Regular);
            button1.FlatStyle = FlatStyle.Flat;
            button1.FlatAppearance.BorderSize = 0;
            button1.BackColor = searchBackColor;
            button1.ForeColor = mutedColor;
            button1.Cursor = Cursors.Default;
            button1.TabStop = false;
            button1.Click += (s, ev) => textBox1.Focus();

            panel1.AutoScroll = false;
            panel1.HorizontalScroll.Enabled = false;
            panel1.HorizontalScroll.Visible = false;
            panel1.VerticalScroll.Enabled = false;
            panel1.VerticalScroll.Visible = false;
            panel1.BorderStyle = BorderStyle.None;
            panel1.Padding = new Padding(0);
            panel1.BackColor = formBackColor;
            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            AdjustLayoutForDpi();
            LoadHistoryItems();
            ActiveControl = panel1;
        }

        private void AdjustLayoutForDpi()
        {
            textBox1.Height = Dpi(SEARCH_BOX_HEIGHT);
            textBox1.Font = new Font("Microsoft YaHei UI", DpiF(9f), FontStyle.Regular);
            textBox1.Location = new Point(Dpi(SEARCH_BOX_MARGIN), Dpi(SEARCH_BOX_MARGIN));
            textBox1.Width = ClientSize.Width - Dpi(SEARCH_BOX_MARGIN * 2);

            int rightIconSize = Math.Max(Dpi(18), textBox1.Height - Dpi(4));
            button1.Size = new Size(rightIconSize, rightIconSize);
            button1.Location = new Point(
                textBox1.Right - button1.Width - Dpi(2),
                textBox1.Top + (textBox1.Height - button1.Height) / 2
            );

            int panelTop = textBox1.Bottom + Dpi(SEARCH_BOX_MARGIN);
            panel1.Location = new Point(Dpi(SEARCH_BOX_MARGIN), panelTop);
            panel1.Width = ClientSize.Width - Dpi(SEARCH_BOX_MARGIN * 2);
            panel1.Height = ClientSize.Height - panelTop - Dpi(BOTTOM_MARGIN);

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
            int titleHeight = Dpi(22);
            int lineHeight = Dpi(18);
            var summaryLines = string.IsNullOrEmpty(summary) ? new string[0] : summary.Split('\n');
            int previewHeight = summaryLines.Length * lineHeight;
            int previewTop = CARD_PADDING_TOP + titleHeight + Dpi(4);
            int cardHeight = Dpi(previewTop + previewHeight + CARD_PADDING_BOTTOM);

            var card = new Panel
            {
                Height = cardHeight,
                Width = panel1.ClientSize.Width - Dpi(4),
                BackColor = cardBackColor,
                BorderStyle = BorderStyle.None,
                Cursor = Cursors.Default,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Tag = fileName
            };
            EnableDoubleBuffer(card);
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(cardBorderColor))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                }
            };

            var timeLabel = new Label
            {
                AutoSize = false,
                Location = new Point(0, Dpi(CARD_PADDING_TOP) + Dpi(3)),
                Width = Dpi(96),
                Height = Dpi(16),
                Font = new Font("Microsoft YaHei UI", DpiF(8f), FontStyle.Regular),
                ForeColor = mutedColor,
                Text = modified.ToString("yyyy-MM-dd"),
                TextAlign = ContentAlignment.TopRight,
                Cursor = Cursors.Default
            };

            var titleBox = MakeTextBox(title, true, titleHeight);
            titleBox.Location = new Point(Dpi(CARD_PADDING_H), Dpi(CARD_PADDING_TOP));

            // 卡片右上角齿轮：进设置页
            var gear = new ToolIconButton { IconId = "gear" };
            gear.Size = new Size(Dpi(22), Dpi(22));
            gear.Click += (s, ev) => Program.AppContext.AddNewForm3();

            var previewBox = MakeTextBox(summary, false, previewHeight);
            previewBox.Location = new Point(Dpi(CARD_PADDING_H), Dpi(previewTop));
            previewBox.Visible = previewHeight > 0;

            card.Controls.Add(previewBox);
            card.Controls.Add(titleBox);
            card.Controls.Add(timeLabel);
            card.Controls.Add(gear);

            AttachCardInteraction(card, fileName);
            AttachCardInteraction(titleBox, fileName);
            AttachCardInteraction(previewBox, fileName);
            AttachCardInteraction(timeLabel, fileName);

            var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
            menu.Items.Add(new ToolStripMenuItem("打开便签", null, (s, ev) => OpenNote(fileName)));
            menu.Items.Add(new ToolStripMenuItem("删除便签", null, (s, ev) => DeleteNote(fileName)));
            card.ContextMenuStrip = menu;
            titleBox.ContextMenuStrip = menu;
            previewBox.ContextMenuStrip = menu;
            timeLabel.ContextMenuStrip = menu;

            return new HistoryCardMeta
            {
                Card = card,
                TitleBox = titleBox,
                PreviewBox = previewBox,
                TimeLabel = timeLabel,
                Gear = gear,
                FileName = fileName,
                SearchText = (title + "\n" + summary).ToLowerInvariant(),
                CardHeight = cardHeight,
                IsFilteredIn = true
            };
        }

        private RichTextBox MakeTextBox(string text, bool isTitle, int height)
        {
            return new RichTextBox
            {
                Text = text,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = cardBackColor,
                ForeColor = isTitle ? titleColor : mutedColor,
                Font = new Font("Microsoft YaHei UI", DpiF(isTitle ? 9.5f : 8.6f), isTitle ? FontStyle.Bold : FontStyle.Regular),
                ScrollBars = RichTextBoxScrollBars.None,
                WordWrap = !isTitle,
                Multiline = true,
                HideSelection = true,
                TabStop = false,
                ShortcutsEnabled = false,
                DetectUrls = false,
                Cursor = Cursors.Default,
                Height = Math.Max(height, Dpi(8))
            };
        }

        // 在标题/摘要里高亮搜索关键词
        private void ApplyHighlight(HistoryCardMeta item, string keyword)
        {
            HighlightBox(item.TitleBox, keyword);
            HighlightBox(item.PreviewBox, keyword);
        }

        private void HighlightBox(RichTextBox box, string keyword)
        {
            if (box == null || box.IsDisposed) return;

            box.SelectAll();
            box.SelectionBackColor = cardBackColor;
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
            var back = hover ? cardHoverColor : cardBackColor;
            card.BackColor = back;
            foreach (Control c in card.Controls)
            {
                if (c is RichTextBox rtb) rtb.BackColor = back;
            }
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

                int innerWidth = item.Card.Width - Dpi(CARD_PADDING_H * 2) - Dpi(96);
                item.TitleBox.Width = Math.Max(innerWidth, Dpi(40));
                item.PreviewBox.Width = item.Card.Width - Dpi(CARD_PADDING_H * 2);

                int gw = Dpi(22);
                item.Gear.Size = new Size(gw, gw);
                item.Gear.Location = new Point(item.Card.Width - Dpi(CARD_PADDING_H) - gw, Dpi(CARD_PADDING_TOP));
                item.TimeLabel.Width = Dpi(96);
                item.TimeLabel.Left = item.Gear.Left - Dpi(6) - item.TimeLabel.Width;
                item.TimeLabel.Top = Dpi(CARD_PADDING_TOP) + Dpi(3);

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
            string keyword = showingSearchPlaceholder
                ? string.Empty
                : (textBox1.Text ?? string.Empty).Trim();
            string keywordLower = keyword.ToLowerInvariant();

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

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            searchDebounceTimer.Stop();
            searchDebounceTimer.Start();
        }

        private void EnsureSearchPlaceholder()
        {
            if (!string.IsNullOrWhiteSpace(textBox1.Text))
            {
                return;
            }

            showingSearchPlaceholder = true;
            textBox1.ForeColor = mutedColor;
            textBox1.Text = "搜索历史便签";
        }

        private void ClearSearchPlaceholder()
        {
            if (!showingSearchPlaceholder)
            {
                return;
            }

            showingSearchPlaceholder = false;
            textBox1.Text = string.Empty;
            textBox1.ForeColor = titleColor;
        }

        private static void EnableDoubleBuffer(Control control)
        {
            control.GetType()
                .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(control, true, null);
        }

    }
}
