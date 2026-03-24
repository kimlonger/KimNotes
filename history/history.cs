using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class history : Form
    {
        // 参考设置页的视觉：中性背景 + 深色正文
        private readonly Color formBackColor = SystemColors.Control;
        private readonly Color searchBackColor = SystemColors.Window;
        private readonly Color searchTextColor = SystemColors.ControlText;
        private readonly Color cardBackColor = Color.FromArgb(224, 232, 242);
        private readonly Color cardHoverColor = Color.FromArgb(214, 224, 236);
        private readonly Color textColor = SystemColors.ControlText;
        private readonly Color secondaryTextColor = Color.FromArgb(96, 96, 96);

        private string folderPath = @"D:\kimNotes\notes";

        private const int SEARCH_BOX_HEIGHT = 34;
        private const int SEARCH_BOX_MARGIN = 12;
        private const int BOTTOM_MARGIN = 10;
        private const int CARD_GAP = 4;
        private const int CARD_PADDING_H = 12;
        private const int CARD_PADDING_TOP = 8;
        private const int CARD_PADDING_BOTTOM = 8;

        private readonly List<HistoryCardMeta> allCards = new List<HistoryCardMeta>();
        private readonly Timer searchDebounceTimer = new Timer();
        private int scrollOffset = 0;
        private int contentHeight = 0;
        private bool showingSearchPlaceholder = false;

        private sealed class HistoryCardMeta
        {
            public Panel Card { get; set; }
            public Label PreviewLabel { get; set; }
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
                return cp;
            }
        }

        public history(string path)
        {
            folderPath = path;
            BackColor = formBackColor;
            InitializeComponent();

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

        private void Form2_Load(object sender, EventArgs e)
        {
            EnableDoubleBuffer(panel1);

            textBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            textBox1.Height = Dpi(SEARCH_BOX_HEIGHT);
            textBox1.Margin = new Padding(Dpi(SEARCH_BOX_MARGIN));
            textBox1.Font = new Font("Segoe UI", DpiF(8.8f));
            textBox1.BackColor = searchBackColor;
            textBox1.ForeColor = searchTextColor;
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
            button1.ForeColor = secondaryTextColor;
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
            textBox1.Font = new Font("Segoe UI", DpiF(8.8f), FontStyle.Regular);
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

            using (var reader = new RichTextBox())
            {
                foreach (var file in rtfFiles)
                {
                    var lines = ReadPreviewLines(reader, file, 5); // 最多5行
                    var card = CreateHistoryCard(file, lines);
                    panel1.Controls.Add(card.Card);
                    allCards.Add(card);
                }
            }

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

                return lines.Length == 0 ? new[] { "（空便签）" } : lines;
            }
            catch
            {
                return new[] { "（读取失败）" };
            }
        }

        private HistoryCardMeta CreateHistoryCard(string filePath, string[] lines)
        {
            string fileName = Path.GetFileName(filePath);
            DateTime modified = File.GetLastWriteTime(filePath);

            string previewText = string.Join(Environment.NewLine, lines);
            int lineCount = Math.Max(1, Math.Min(5, lines.Length));
            int lineHeight = Dpi(17);
            int previewHeight = lineCount * lineHeight;
            int timeHeight = Dpi(14);
            int cardHeight = Dpi(CARD_PADDING_TOP + CARD_PADDING_BOTTOM + 18) + previewHeight;

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

            var timeLabel = new Label
            {
                AutoSize = false,
                Location = new Point(card.Width - Dpi(CARD_PADDING_H) - Dpi(96), Dpi(CARD_PADDING_TOP)),
                Width = Dpi(96),
                Height = timeHeight,
                Font = new Font("Segoe UI", DpiF(7.8f), FontStyle.Regular),
                ForeColor = secondaryTextColor,
                Text = modified.ToString("yyyy-MM-dd"),
                TextAlign = ContentAlignment.TopRight,
                Cursor = Cursors.Default
            };

            var previewLabel = new Label
            {
                AutoSize = false,
                Location = new Point(Dpi(CARD_PADDING_H), Dpi(CARD_PADDING_TOP + 18)),
                Width = card.Width - Dpi(CARD_PADDING_H * 2),
                Height = previewHeight,
                Font = new Font("Segoe UI", DpiF(8.6f), FontStyle.Regular),
                ForeColor = textColor,
                Text = previewText,
                TextAlign = ContentAlignment.TopLeft,
                Cursor = Cursors.Default
            };

            card.Controls.Add(previewLabel);
            card.Controls.Add(timeLabel);

            AttachCardInteraction(card, fileName);
            AttachCardInteraction(previewLabel, fileName);
            AttachCardInteraction(timeLabel, fileName);

            var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
            menu.Items.Add(new ToolStripMenuItem("打开便签", null, (s, ev) => OpenNote(fileName)));
            menu.Items.Add(new ToolStripMenuItem("删除便签", null, (s, ev) => DeleteNote(fileName)));
            card.ContextMenuStrip = menu;
            previewLabel.ContextMenuStrip = menu;
            timeLabel.ContextMenuStrip = menu;

            return new HistoryCardMeta
            {
                Card = card,
                PreviewLabel = previewLabel,
                TimeLabel = timeLabel,
                FileName = fileName,
                SearchText = previewText.ToLowerInvariant(), // 不显示编号，也不按文件名展示
                CardHeight = cardHeight,
                IsFilteredIn = true
            };
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
            card.BackColor = hover ? cardHoverColor : cardBackColor;
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
                item.PreviewLabel.Width = item.Card.Width - Dpi(CARD_PADDING_H * 2);
                item.TimeLabel.Width = Dpi(96);
                item.TimeLabel.Left = item.Card.Width - Dpi(CARD_PADDING_H) - item.TimeLabel.Width;
                item.TimeLabel.Top = Dpi(CARD_PADDING_TOP);

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
                : (textBox1.Text ?? string.Empty).Trim().ToLowerInvariant();

            panel1.SuspendLayout();
            foreach (var item in allCards)
            {
                item.IsFilteredIn = string.IsNullOrEmpty(keyword) || item.SearchText.Contains(keyword);
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
            textBox1.ForeColor = Color.FromArgb(105, 116, 130);
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
            textBox1.ForeColor = searchTextColor;
        }

        private static void EnableDoubleBuffer(Control control)
        {
            control.GetType()
                .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(control, true, null);
        }

    }
}
