using System.Drawing;
using System;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace KimNotes
{
    public partial class history : Form
    {
        private Color buttonColor = Color.FromArgb(180, 200, 220);
        private Color richTextBoxColor = Color.FromArgb(220, 230, 240);
        private string folderPath = @"D:\kimNotes\notes";

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
            // 设置DPI感知模式
            SetProcessDpiAwareness();
            
            InitializeComponent();
            
            // 应用DPI缩放
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.AutoScaleMode = AutoScaleMode.Dpi;
            
            // 启用双缓冲减少闪烁
            SetStyle(ControlStyles.OptimizedDoubleBuffer | 
                    ControlStyles.AllPaintingInWmPaint | 
                    ControlStyles.UserPaint, true);
            
            folderPath = path;

            // 注册鼠标滚轮事件
            this.MouseWheel += Form2_MouseWheel;

            // 创建半透明背景的搜索框
            textBox1.BackColor = Color.FromArgb(240, 240, 240);
            textBox1.BorderStyle = BorderStyle.FixedSingle;
        }

        // 设置DPI感知
        private void SetProcessDpiAwareness()
        {
            try
            {
                // 尝试设置DPI感知模式 - 适用于Windows 8.1及以上
                if (Environment.OSVersion.Version.Major >= 6 && Environment.OSVersion.Version.Minor >= 3)
                {
                    Win32ApiHelper.SetProcessDpiAwareness(Win32ApiHelper.PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
                }
                // 对于Windows Vista/7/8，使用旧的DPI感知API
                else if (Environment.OSVersion.Version.Major >= 6)
                {
                    Win32ApiHelper.SetProcessDPIAware();
                }
            }
            catch (Exception)
            {
                // 忽略错误，如果设置失败则回退到默认行为
            }
        }

        private void Form2_Load(object sender, EventArgs e)
        {
            button1.FlatStyle = FlatStyle.Flat;
            button1.FlatAppearance.BorderSize = 0;
            button1.BackColor = buttonColor;
            
            // 设置窗体启动位置为屏幕中央
            SetFormPosition();

            panel1.AutoScroll = true;
            panel1.HorizontalScroll.Enabled = false;
            panel1.HorizontalScroll.Visible = false;
            panel1.VerticalScroll.Enabled = true;
            panel1.VerticalScroll.Visible = true;

            panel1.BorderStyle = BorderStyle.None;
            panel1.Padding = new Padding(0);
            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            textBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            // 获取并按文件的最后修改时间排序
            var rtfFiles = Directory.GetFiles(folderPath, "*.rtf")
                .OrderByDescending(file => File.GetLastWriteTime(file)) // 按最后修改时间降序排序
                .ToArray();
            int topPosition = 0;

            foreach (var file in rtfFiles)
            {
                var richTextBox = new RichTextBox
                {
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    BackColor = richTextBoxColor,
                    BorderStyle = BorderStyle.None,
                    Width = panel1.ClientSize.Width - 12, // 减去滚动条的宽度
                    Height = 85,
                    ScrollBars = RichTextBoxScrollBars.None,
                    ReadOnly = true,
                    Font = new Font("Calibri", 10.5f)
                };

                try
                {
                    // 使用LoadFile方法加载RTF文件
                    richTextBox.LoadFile(file, RichTextBoxStreamType.RichText);

                    // 限制显示的行数，假设只显示前5行内容
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

                // 添加"打开便签"菜单项
                var openNoteMenuItem = new ToolStripMenuItem("打开便签", null, (s, k) =>
                {
                    // 触发与双击RichTextBox控件相同的逻辑
                    RichTextBox_DoubleClick(richTextBox, EventArgs.Empty);
                });
                contextMenu.Items.Add(openNoteMenuItem);
                var deleteMenuItem = new ToolStripMenuItem("删除便签", null, (s, k) =>
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

                panel1.Controls.Add(richTextBox);

                topPosition += richTextBox.Height + 10;
            }

            this.ActiveControl = panel1;
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
            
            // 设置窗体背景色
            this.BackColor = buttonColor;
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
                    Width = panel1.ClientSize.Width - 12, // 减去滚动条的宽度
                    Height = 85,
                    ScrollBars = RichTextBoxScrollBars.None,
                    ReadOnly = true,
                    Font = new Font("Calibri", 10.5f)
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

                topPosition += richTextBox.Height + 10; // 更新位置
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
                topPosition += richTextBox.Height + 10; // 更新下一个控件的位置
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