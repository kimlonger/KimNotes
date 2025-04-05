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

        public history()
        {
            this.BackColor = buttonColor;
            InitializeComponent();
            // 订阅鼠标滚轮事件
            this.MouseWheel += new MouseEventHandler(Form2_MouseWheel);
            // 设置窗体启动位置为屏幕中央
            this.StartPosition = FormStartPosition.CenterScreen;
        }

        private void Form2_Load(object sender, EventArgs e)
        {
            button1.FlatStyle = FlatStyle.Flat;
            button1.FlatAppearance.BorderSize = 0;
            button1.BackColor = buttonColor;
            button1.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            panel1.AutoScroll = true;
            panel1.HorizontalScroll.Enabled = false;
            panel1.HorizontalScroll.Visible = false;
            panel1.VerticalScroll.Enabled = true;
            panel1.VerticalScroll.Visible = true;

            panel1.BorderStyle = BorderStyle.None;
            panel1.Padding = new Padding(0);
            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            textBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            var rtfFiles = Directory.GetFiles(folderPath, "*.rtf");

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
                panel1.Controls.Add(richTextBox);

                topPosition += richTextBox.Height + 10;
            }

            this.ActiveControl = panel1;
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
}