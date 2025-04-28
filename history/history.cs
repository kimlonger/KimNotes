using System.Drawing;
using System;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KimNotes.utils;

namespace KimNotes
{
    public partial class history : Form
    {
        private Color buttonColor = Color.FromArgb(180, 200, 220);
        private Color richTextBoxColor = Color.FromArgb(220, 230, 240);
        private string folderPath = @"D:\kimNotes\notes";
        private float dpiScaleFactor = 1.0f;
        private const int SEARCH_BOX_HEIGHT = 30;
        private const int SEARCH_BOX_MARGIN = 10;
        private const int BUTTON_SIZE = 24;
        private const int BUTTON_MARGIN = 5;
        private const int BOTTOM_MARGIN = 15;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private const int LOGPIXELSX = 88;
        private const int LOGPIXELSY = 90;

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
            Win32ApiHelper.SetProcessDpiAwareness(Win32ApiHelper.PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
            
            folderPath = path;
            this.BackColor = buttonColor;
            InitializeComponent();
            
            // 获取当前DPI缩放因子
            dpiScaleFactor = Win32ApiHelper.GetScalingFactor();

            // 订阅鼠标滚轮事件
            this.MouseWheel += new MouseEventHandler(Form2_MouseWheel);
            // 设置窗体启动位置为屏幕中央
            SetFormPosition();
            
            // 添加窗体大小改变事件处理
            this.Resize += (s, e) => AdjustLayoutForDpi();
            
            // 初始缩放窗体
            ScaleFormForDpi();
        }

        private int ScaleValue(int value)
        {
            // 只在屏幕实际处于缩放状态时应用缩放
            return dpiScaleFactor > 1.0f ? (int)(value * dpiScaleFactor) : value;
        }

        private float ScaleFontSize(float size)
        {
            // 只在屏幕实际处于缩放状态时应用缩放
            return dpiScaleFactor > 1.0f ? size * dpiScaleFactor : size;
        }

        // 新增：窗体初始大小随DPI放大
        private void ScaleFormForDpi()
        {
            if (dpiScaleFactor > 1.0f)
            {
                this.Width = (int)(this.Width * dpiScaleFactor);
                this.Height = (int)(this.Height * dpiScaleFactor);
            }
        }

        // 新增：DPI感知的布局调整
        private void AdjustLayoutForDpi()
        {
            if (dpiScaleFactor <= 1.0f)
                return;

            // 搜索框布局
            textBox1.Height = ScaleValue(SEARCH_BOX_HEIGHT);
            textBox1.Margin = new Padding(ScaleValue(SEARCH_BOX_MARGIN));
            textBox1.Font = new Font(textBox1.Font.FontFamily, ScaleFontSize(10.5f));
            textBox1.Location = new Point(
                ScaleValue(SEARCH_BOX_MARGIN),
                ScaleValue(SEARCH_BOX_MARGIN)
            );

            // 按钮布局
            button1.Size = new Size(ScaleValue(BUTTON_SIZE), ScaleValue(BUTTON_SIZE));
            button1.Location = new Point(
                this.ClientSize.Width - ScaleValue(BUTTON_SIZE + SEARCH_BOX_MARGIN),
                ScaleValue(SEARCH_BOX_MARGIN)
            );

            // 调整搜索框宽度
            textBox1.Width = button1.Left - textBox1.Left - ScaleValue(BUTTON_MARGIN);

            // Panel布局
            panel1.Padding = new Padding(0);
            int panelTop = textBox1.Bottom + ScaleValue(SEARCH_BOX_MARGIN);
            panel1.Location = new Point(ScaleValue(SEARCH_BOX_MARGIN), panelTop);
            panel1.Width = this.ClientSize.Width - ScaleValue(SEARCH_BOX_MARGIN * 2);
            panel1.Height = this.ClientSize.Height - panelTop - ScaleValue(BOTTOM_MARGIN);

            // 调整RichTextBox控件
            foreach (Control control in panel1.Controls)
            {
                if (control is RichTextBox richTextBox)
                {
                    richTextBox.Font = new Font("Calibri", ScaleFontSize(10.5f));
                    richTextBox.Margin = new Padding(ScaleValue(5));
                    richTextBox.Width = panel1.ClientSize.Width - ScaleValue(10);
                    richTextBox.Height = ScaleValue(85);
                }
            }
        }

        private void Form2_Load(object sender, EventArgs e)
        {
            // 设置搜索框的位置和大小
            textBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            textBox1.Height = ScaleValue(SEARCH_BOX_HEIGHT);
            textBox1.Margin = new Padding(ScaleValue(SEARCH_BOX_MARGIN));
            textBox1.Font = new Font(textBox1.Font.FontFamily, ScaleFontSize(10.5f));
            textBox1.Location = new Point(
                ScaleValue(SEARCH_BOX_MARGIN),
                ScaleValue(SEARCH_BOX_MARGIN)
            );
            
            // 设置按钮的位置和大小
            button1.FlatStyle = FlatStyle.Flat;
            button1.FlatAppearance.BorderSize = 0;
            button1.BackColor = buttonColor;
            button1.Size = new Size(ScaleValue(BUTTON_SIZE), ScaleValue(BUTTON_SIZE));
            button1.Location = new Point(
                this.ClientSize.Width - ScaleValue(BUTTON_SIZE + SEARCH_BOX_MARGIN),
                ScaleValue(SEARCH_BOX_MARGIN)
            );
            button1.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            
            // 调整搜索框的宽度，为按钮留出空间
            textBox1.Width = button1.Left - textBox1.Left - ScaleValue(BUTTON_MARGIN);
            
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
            int panelTop = textBox1.Bottom + ScaleValue(SEARCH_BOX_MARGIN);
            panel1.Location = new Point(ScaleValue(SEARCH_BOX_MARGIN), panelTop);
            panel1.Width = this.ClientSize.Width - ScaleValue(SEARCH_BOX_MARGIN * 2);
            panel1.Height = this.ClientSize.Height - panelTop - ScaleValue(BOTTOM_MARGIN);

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
                    Width = panel1.ClientSize.Width - ScaleValue(10),
                    Height = ScaleValue(85),
                    ScrollBars = RichTextBoxScrollBars.None,
                    ReadOnly = true,
                    Font = new Font("Calibri", ScaleFontSize(10.5f)),
                    Margin = new Padding(ScaleValue(5))
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
                contextMenu.Font = new Font(contextMenu.Font.FontFamily, ScaleFontSize(9f));

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
                topPosition += richTextBox.Height + ScaleValue(10);
            }

            this.ActiveControl = panel1;
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
            int x = screenBounds.Width * 3 / 4 - this.Width;
            int y = (screenBounds.Height - this.Height) / 2;

            // 设置窗体的位置
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(x, y);
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

        private void RefreshRichTextBoxList()
        {
            panel1.Controls.Clear(); // 清除现有控件

            int topPosition = 0;
            // 获取并按文件的最后修改时间排序
            var rtfFiles = Directory.GetFiles(folderPath, "*.rtf")
                .OrderByDescending(file => File.GetLastWriteTime(file)) // 按最后修改时间降序排序
                .ToArray();

            foreach (var file in rtfFiles)
            {
                var richTextBox = new RichTextBox
                {
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    BackColor = richTextBoxColor,
                    BorderStyle = BorderStyle.None,
                    Width = panel1.ClientSize.Width - ScaleValue(24), // 考虑滚动条和边距
                    Height = ScaleValue(85),
                    ScrollBars = RichTextBoxScrollBars.None,
                    ReadOnly = true,
                    Font = new Font("Calibri", ScaleFontSize(10.5f)),
                    Margin = new Padding(ScaleValue(5))
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
                richTextBox.DoubleClick += RichTextBox_DoubleClick; // 确保双击事件仍然有效
                richTextBox.Top = topPosition;

                // 创建并配置右键菜单，与Form2_Load方法内相同
                var contextMenu = new ContextMenuStrip();
                contextMenu.Renderer = new CustomToolStripRenderer(); // 应用自定义风格
                                                                      // 添加"打开便签"菜单项
                var openNoteMenuItem = new ToolStripMenuItem("打开便签", null, (s, k) =>
                {
                    // 触发与双击RichTextBox控件相同的逻辑
                    RichTextBox_DoubleClick(richTextBox, EventArgs.Empty);
                });
                contextMenu.Items.Add(openNoteMenuItem);
                var deleteMenuItem = new ToolStripMenuItem("删除", null, (s, k) =>
                {
                    try
                    {
                        string filePath = Path.Combine(folderPath, richTextBox.Name);
                        File.Delete(filePath); // 删除文件

                        RefreshRichTextBoxList(); // 刷新列表
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"删除文件时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });

                contextMenu.Items.Add(deleteMenuItem);
                richTextBox.ContextMenuStrip = contextMenu;

                panel1.Controls.Add(richTextBox); // 将新创建的RichTextBox添加到panel1中

                topPosition += richTextBox.Height + ScaleValue(10); // 更新位置
            }
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            string filterText = textBox1.Text.ToLower(); // 获取用户输入并转换为小写

            // 临时列表用于存储可见的RichTextBox
            var visibleRichTextBoxes = new List<RichTextBox>();

            foreach (Control control in panel1.Controls)
            {
                if (control is RichTextBox richTextBox)
                {
                    // 检查RichTextBox的内容是否包含用户输入的文本
                    bool containsFilter = richTextBox.Text.ToLower().Contains(filterText);
                    richTextBox.Visible = containsFilter; // 根据匹配结果设置可见性

                    if (containsFilter)
                    {
                        // 添加到临时列表
                        visibleRichTextBoxes.Add(richTextBox);
                    }
                }
            }

            // 重新排序可见的RichTextBox的位置
            int topPosition = 0;
            foreach (var richTextBox in visibleRichTextBoxes)
            {
                richTextBox.Top = topPosition;
                topPosition += richTextBox.Height + ScaleValue(10); // 更新下一个控件的位置
            }
        }
    }

    class CustomToolStripRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Color bgColor = e.Item.Selected ? Color.FromArgb(220, 240, 250) : Color.FromArgb(245, 245, 245); // 调整选中与未选中背景色
            e.Graphics.FillRectangle(new SolidBrush(bgColor), e.Item.ContentRectangle);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            // 设置整个菜单的背景色
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(245, 245, 245)), e.AffectedBounds);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // 设置文本颜色
            e.TextColor = Color.Black;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            // 定制分隔符样式
            var darkLine = new Rectangle(e.Item.ContentRectangle.Left, e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2), e.Item.ContentRectangle.Width, 1);
            var lightLine = new Rectangle(e.Item.ContentRectangle.Left, e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height / 2) + 1, e.Item.ContentRectangle.Width, 1);
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(220, 220, 220)), darkLine);
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(245, 245, 245)), lightLine);
        }
    }
}