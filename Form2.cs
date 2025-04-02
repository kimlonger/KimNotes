using System.Drawing;
using System;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace KimNotes
{
    public partial class Form2 : Form
    {
        private Color buttonColor = Color.FromArgb(180, 200, 220);
        private Color richTextBoxColor = Color.FromArgb(220, 230, 240);
        private string folderPath = @"D:\kimNotes";

        public Form2()
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

            // 设置边框样式和内边距
            panel1.BorderStyle = BorderStyle.None;
            panel1.Padding = new Padding(0);
            panel1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

           // richTextBox1.BorderStyle = BorderStyle.None;
            textBox1.Anchor = AnchorStyles.Top | AnchorStyles.Left;

          

            var txtFiles = Directory.GetFiles(folderPath, "*.txt");

            int topPosition = 0;

            foreach (var file in txtFiles)
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

                string[] lines = File.ReadAllLines(file);
                richTextBox.Text = string.Join(Environment.NewLine, lines.Take(5));
                richTextBox.Name = Path.GetFileName(file);
                // 订阅DoubleClick事件
                richTextBox.DoubleClick += RichTextBox_DoubleClick;
                richTextBox.Top = topPosition;
                panel1.Controls.Add(richTextBox);

                topPosition += richTextBox.Height + 10;
            }

            // 设置焦点到panel1，以便于滚动条响应鼠标滚轮
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