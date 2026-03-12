using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class history : Form
    {
        private Color buttonColor = Color.FromArgb(180, 200, 220);
        private Color richTextBoxColor = Color.FromArgb(220, 230, 240);
        private string folderPath = @"D:\kimNotes\notes";
        private const int SEARCH_BOX_HEIGHT = 30;
        private const int SEARCH_BOX_MARGIN = 10;
        private const int BUTTON_SIZE = 24;
        private const int BUTTON_MARGIN = 5;
        private const int BOTTOM_MARGIN = 15;

        // 添加DPI感知
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // 启用 WS_EX_COMPOSITED
                return cp;
            }
        }

        public history(string path)
        {
            folderPath = path;
            BackColor = buttonColor;
            InitializeComponent();

            // 订阅鼠标滚轮事件
            MouseWheel += new MouseEventHandler(Form2_MouseWheel);
            // 设置窗体启动位置为屏幕中央
            SetFormPosition();
            // 添加窗体大小改变事件处理
            Resize += (s, e) => AdjustLayoutForDpi();
        }

        private int Dpi(int value) => (int)Math.Round(value * (DeviceDpi / 96f));
        private float DpiF(float value) => value * (DeviceDpi / 96f);

        // 新增：DPI感知的布局调整
        private void AdjustLayoutForDpi()
        {
            // 搜索框布局
            textBox1.Height = Dpi(SEARCH_BOX_HEIGHT);
            textBox1.Margin = new Padding(Dpi(SEARCH_BOX_MARGIN));
            textBox1.Font = new Font(textBox1.Font.FontFamily, DpiF(10.5f));
            textBox1.Location = new Point(
                Dpi(SEARCH_BOX_MARGIN),
                Dpi(SEARCH_BOX_MARGIN)
            );

            // 按钮布局
            button1.Size = new Size(Dpi(BUTTON_SIZE), Dpi(BUTTON_SIZE));
            button1.Location = new Point(
                ClientSize.Width - Dpi(BUTTON_SIZE + SEARCH_BOX_MARGIN),
                Dpi(SEARCH_BOX_MARGIN)
            );

            // 调整搜索框宽度
            textBox1.Width = button1.Left - textBox1.Left - Dpi(BUTTON_MARGIN);

            // Panel布局
            panel1.Padding = new Padding(0);
            int panelTop = textBox1.Bottom + Dpi(SEARCH_BOX_MARGIN);
            panel1.Location = new Point(Dpi(SEARCH_BOX_MARGIN), panelTop);
            panel1.Width = ClientSize.Width - Dpi(SEARCH_BOX_MARGIN * 2);
            panel1.Height = ClientSize.Height - panelTop - Dpi(BOTTOM_MARGIN);

            // 调整RichTextBox控件
            foreach (Control control in panel1.Controls)
            {
                if (control is RichTextBox richTextBox)
                {
                    richTextBox.Font = new Font("Calibri", DpiF(10.5f));
                    richTextBox.Margin = new Padding(Dpi(5));
                    richTextBox.Width = panel1.ClientSize.Width - Dpi(10);
                    richTextBox.Height = Dpi(85);
                }
            }

            LayoutVisibleItems();
        }

        private void Form2_Load(object sender, EventArgs e)
        {
            // 设置搜索框的位置和大小
            textBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            textBox1.Height = Dpi(SEARCH_BOX_HEIGHT);
            textBox1.Margin = new Padding(Dpi(SEARCH_BOX_MARGIN));
            textBox1.Font = new Font(textBox1.Font.FontFamily, DpiF(10.5f));
            textBox1.Location = new Point(
                Dpi(SEARCH_BOX_MARGIN),
                Dpi(SEARCH_BOX_MARGIN)
            );

            // 设置按钮的位置和大小
            button1.FlatStyle = FlatStyle.Flat;
            button1.FlatAppearance.BorderSize = 0;
            button1.BackColor = buttonColor;
            button1.Size = new Size(Dpi(BUTTON_SIZE), Dpi(BUTTON_SIZE));
            button1.Location = new Point(
                ClientSize.Width - Dpi(BUTTON_SIZE + SEARCH_BOX_MARGIN),
                Dpi(SEARCH_BOX_MARGIN)
            );
            button1.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // 调整搜索框的宽度，为按钮留出空间
            textBox1.Width = button1.Left - textBox1.Left - Dpi(BUTTON_MARGIN);

            // 设置panel1的位置和大小
            panel1.AutoScroll = true;
            panel1.HorizontalScroll.Enabled = false;
            panel1.HorizontalScroll.Visible = false;
            panel1.VerticalScroll.Enabled = true;
            panel1.VerticalScroll.Visible = true;
            panel1.BorderStyle = BorderStyle.None;
            panel1.Padding = new Padding(0);
            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // 计算panel1的顶部位置，确保不会与搜索框重叠
            int panelTop = textBox1.Bottom + Dpi(SEARCH_BOX_MARGIN);
            panel1.Location = new Point(Dpi(SEARCH_BOX_MARGIN), panelTop);
            panel1.Width = ClientSize.Width - Dpi(SEARCH_BOX_MARGIN * 2);
            panel1.Height = ClientSize.Height - panelTop - Dpi(BOTTOM_MARGIN);

            LoadHistoryItems();
            ActiveControl = panel1;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AdjustLayoutForDpi();
        }

        // 设置窗体位置的方法
        private void SetFormPosition()
        {
            // 获取屏幕的工作区域
            var screenBounds = Screen.PrimaryScreen.WorkingArea;

            // 计算窗体的位置
            int x = screenBounds.Width * 3 / 4 - Width;
            int y = (screenBounds.Height - Height) / 2;

            // 设置窗体的位置
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }

        private void RichTextBox_DoubleClick(object sender, EventArgs e)
        {
            // 获取被双击的RichTextBox控件
            var richTextBox = sender as RichTextBox;

            if (richTextBox != null)
            {
                Program.AppContext.AddNewForm(richTextBox.Name);
            }
        }

        private void Form2_MouseWheel(object sender, MouseEventArgs e)
        {
            // 滚动panel1的内容
            panel1.AutoScrollPosition =
                new Point(panel1.AutoScrollPosition.X, panel1.AutoScrollPosition.Y - e.Delta);
        }

        private void LoadHistoryItems()
        {
            panel1.Controls.Clear();

            // 获取并按文件的最后修改时间排序
            var rtfFiles = Directory.GetFiles(folderPath, "*.rtf")
                .OrderByDescending(file => File.GetLastWriteTime(file))
                .ToArray();

            int topPosition = 0;
            foreach (var file in rtfFiles)
            {
                var richTextBox = new RichTextBox
                {
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    BackColor = richTextBoxColor,
                    BorderStyle = BorderStyle.None,
                    Width = panel1.ClientSize.Width - Dpi(10),
                    Height = Dpi(85),
                    ScrollBars = RichTextBoxScrollBars.None,
                    ReadOnly = true,
                    Font = new Font("Calibri", DpiF(10.5f)),
                    Margin = new Padding(Dpi(5))
                };

                try
                {
                    richTextBox.LoadFile(file, RichTextBoxStreamType.RichText);
                    string[] lines = richTextBox.Lines.Take(5).ToArray();
                    richTextBox.Text = string.Join(Environment.NewLine, lines);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载文件时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                richTextBox.Name = Path.GetFileName(file);
                richTextBox.DoubleClick += RichTextBox_DoubleClick;
                richTextBox.Top = topPosition;

                var contextMenu = new ContextMenuStrip();
                contextMenu.Renderer = new CustomToolStripRenderer();
                contextMenu.Font = new Font(contextMenu.Font.FontFamily, DpiF(9f));

                var openNoteMenuItem = new ToolStripMenuItem("打开便签", null, (s, k) =>
                {
                    RichTextBox_DoubleClick(richTextBox, EventArgs.Empty);
                });
                contextMenu.Items.Add(openNoteMenuItem);

                var deleteMenuItem = new ToolStripMenuItem("删除便签", null, (s, k) =>
                {
                    try
                    {
                        string filePath = Path.Combine(folderPath, richTextBox.Name);
                        File.Delete(filePath);
                        RefreshRichTextBoxList();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"删除文件时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
                contextMenu.Items.Add(deleteMenuItem);
                richTextBox.ContextMenuStrip = contextMenu;

                panel1.Controls.Add(richTextBox);
                topPosition += richTextBox.Height + Dpi(10);
            }

            LayoutVisibleItems();
        }

        private void RefreshRichTextBoxList()
        {
            LoadHistoryItems();
        }

        private void LayoutVisibleItems()
        {
            int topPosition = 0;
            foreach (var richTextBox in panel1.Controls.OfType<RichTextBox>().Where(r => r.Visible))
            {
                richTextBox.Top = topPosition;
                topPosition += richTextBox.Height + Dpi(10);
            }
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            string filterText = textBox1.Text.ToLower();

            var visibleRichTextBoxes = panel1.Controls
                .OfType<RichTextBox>()
                .ToList();

            foreach (var richTextBox in visibleRichTextBoxes)
            {
                bool containsFilter = richTextBox.Text.ToLower().Contains(filterText);
                richTextBox.Visible = containsFilter;
            }

            LayoutVisibleItems();
        }
    }

    class CustomToolStripRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Color bgColor = e.Item.Selected ? Color.FromArgb(220, 240, 250) : Color.FromArgb(245, 245, 245);
            e.Graphics.FillRectangle(new SolidBrush(bgColor), e.Item.ContentRectangle);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(245, 245, 245)), e.AffectedBounds);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.Black;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var darkLine = new Rectangle(e.Item.ContentRectangle.Left, e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2), e.Item.ContentRectangle.Width, 1);
            var lightLine = new Rectangle(e.Item.ContentRectangle.Left, e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2) + 1, e.Item.ContentRectangle.Width, 1);
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(220, 220, 220)), darkLine);
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(245, 245, 245)), lightLine);
        }
    }
}
