using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class Form1 : Form
    {
        private Color buttonColor = Color.FromArgb(184, 208, 233); // 淡蓝色
        private Color richTextBoxColor = Color.FromArgb(225, 235, 245); // 更浅的蓝色
        private int cornerRadius = 15;
        private readonly ToolTip toolTip;
        private string folderPath = @"D:\kimNotes";
        private string currentFileName;
        private static int formCount = 0; // 用于跟踪窗体的实例数量


        public Form1(string fileName = null)
        {
            InitializeComponent();
            this.ShowIcon = false;
            this.BackColor = buttonColor; // 设置窗体背景颜色
            SetUpRichTextBox();
            SetUpButtons(button1, button2, button3, button4, button6, button5, button7, button8);
            // 创建一个ToolTip实例并设置属性
            toolTip = new ToolTip
            {
                InitialDelay = 500,
                ReshowDelay = 500,
                ShowAlways = true
            };
            toolTip.SetToolTip(button1, "加粗");
            toolTip.SetToolTip(button2, "斜体");
            toolTip.SetToolTip(button3, "删除线");
            toolTip.SetToolTip(button4, "翻译");
            toolTip.SetToolTip(button5, "大小写转换");
            toolTip.SetToolTip(button6, "便签列表");
            toolTip.SetToolTip(button7, "新建便签");
            toolTip.SetToolTip(button8, "置顶当前便签");
            formCount++; // 增加窗体计数
            if (formCount == 1 && string.IsNullOrEmpty(fileName))
            {
                LoadLatestFileContent(); // 首次启动加载最新文件
            }
            else if (!string.IsNullOrEmpty(fileName))
            {
                LoadFileContent(fileName); // 加载指定文件
                currentFileName = fileName;
            }
        }
        private void LoadLatestFileContent()
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
                return; 
            }
            var latestFile = new DirectoryInfo(folderPath).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestFile != null)
            {
                richTextBox1.Text = File.ReadAllText(latestFile.FullName);
                currentFileName = latestFile.Name;
            }
        }

        private void LoadFileContent(string fileName)
        {
            string filePath = Path.Combine(folderPath, fileName);
            if (File.Exists(filePath))
            {
                richTextBox1.Text = File.ReadAllText(filePath);
                currentFileName = fileName;
            }
        }

        private void SetUpRichTextBox()
        {
            richTextBox1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            richTextBox1.BackColor = richTextBoxColor; 
            richTextBox1.BorderStyle = BorderStyle.None; 
        }

        private void SetUpButtons(params Button[] buttons)
        {
            foreach (var button in buttons)
            {
                button.FlatStyle = FlatStyle.Flat; 
                button.FlatAppearance.BorderSize = 0; 
                button.BackColor = buttonColor; 
                button.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            formCount--; // 窗体关闭时减少计数

            if (string.IsNullOrWhiteSpace(richTextBox1.Text))
            {
                return;
            }

            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            string fileName;

            if (string.IsNullOrEmpty(currentFileName))
            {
                fileName = $"{DateTime.Now:yyyyMMddHHmmss}.txt";
            }
            else
            {
                fileName = currentFileName;
            }

            string filePath = Path.Combine(folderPath, fileName);

            try
            {
                File.WriteAllText(filePath, richTextBox1.Text);
                MessageBox.Show($"笔记已保存到 {filePath}", "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存笔记时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void button1_Click(object sender, EventArgs e)
        {
            if (richTextBox1.SelectionLength > 0)
            {
                Font currentFont = richTextBox1.SelectionFont;

                if (currentFont != null)
                {
                    FontStyle newStyle = currentFont.Style;

                    // Toggle the Bold style while preserving Italic and Strikeout
                    if (currentFont.Style.HasFlag(FontStyle.Bold))
                    {
                        newStyle &= ~FontStyle.Bold; // Remove Bold
                    }
                    else
                    {
                        newStyle |= FontStyle.Bold; // Add Bold
                    }

                    richTextBox1.SelectionFont = new Font(currentFont.FontFamily, currentFont.Size, newStyle);
                }
            }
        }
        private void button2_Click(object sender, EventArgs e)
        {
            if (richTextBox1.SelectionLength > 0)
            {
                Font currentFont = richTextBox1.SelectionFont;

                if (currentFont != null)
                {
                    FontStyle newStyle = currentFont.Style;

                    // Toggle the Italic style while preserving Bold and Strikeout
                    if (currentFont.Style.HasFlag(FontStyle.Italic))
                    {
                        newStyle &= ~FontStyle.Italic; // Remove Italic
                    }
                    else
                    {
                        newStyle |= FontStyle.Italic; // Add Italic
                    }

                    richTextBox1.SelectionFont = new Font(currentFont.FontFamily, currentFont.Size, newStyle);
                }
            }
        }
        private void button3_Click(object sender, EventArgs e)
        {
            if (richTextBox1.SelectionLength > 0)
            {
                Font currentFont = richTextBox1.SelectionFont;

                if (currentFont != null)
                {
                    FontStyle newStyle = currentFont.Style;

                    // Toggle the Strikeout style while preserving Bold and Italic
                    if (currentFont.Style.HasFlag(FontStyle.Strikeout))
                    {
                        newStyle &= ~FontStyle.Strikeout; // Remove Strikeout
                    }
                    else
                    {
                        newStyle |= FontStyle.Strikeout; // Add Strikeout
                    }

                    richTextBox1.SelectionFont = new Font(currentFont.FontFamily, currentFont.Size, newStyle);
                }
            }
        }

        private void button4_Click(object sender, EventArgs e)
        {
            string selectedText;

            if (richTextBox1.SelectedText.Length > 0)
            {
                selectedText = richTextBox1.SelectedText;
            }
            else
            {
                int firstLineEndIndex = richTextBox1.Text.IndexOf('\n');
                if (firstLineEndIndex == -1)
                {
                    firstLineEndIndex = richTextBox1.Text.Length; 
                }

                selectedText = richTextBox1.Text.Substring(0, firstLineEndIndex).Trim();

                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    MessageBox.Show("请先选择要翻译的文本或确保第一行有内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            Translator translator = new Translator();

            string translatedText = translator.Translate(selectedText, "auto", "en");

            int selectionStart = richTextBox1.SelectionStart + richTextBox1.SelectionLength;

            if (selectionStart == 0)
            {
                selectionStart = selectedText.Length; 
            }

            string textToInsert = $"\n{translatedText}";
            richTextBox1.Text = richTextBox1.Text.Insert(selectionStart, textToInsert);

            richTextBox1.SelectionStart = selectionStart + textToInsert.Length;
            richTextBox1.SelectionLength = 0;
        }
        private void button5_Click(object sender, EventArgs e)
        {
            string selectedText;

            if (richTextBox1.SelectedText.Length > 0)
            {
                selectedText = richTextBox1.SelectedText;
            }
            else
            {
                int firstLineEndIndex = richTextBox1.Text.IndexOf('\n');
                if (firstLineEndIndex == -1)
                {
                    firstLineEndIndex = richTextBox1.Text.Length; 
                }

                selectedText = richTextBox1.Text.Substring(0, firstLineEndIndex).Trim();

                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    MessageBox.Show("请先选择要处理的文本或确保第一行有内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
          
            string transformedText;
            if (selectedText == selectedText.ToLower())
            {
                transformedText = selectedText.ToUpper();
            }
            else if (selectedText == selectedText.ToUpper())
            {
                transformedText = selectedText.ToLower();
            }
            else
            {
                MessageBox.Show("请选择全部为小写或全部为大写的英文文本。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int selectionStart = richTextBox1.SelectionStart + richTextBox1.SelectionLength;

            if (selectionStart == 0)
            {
                selectionStart = selectedText.Length; // 设置为第一行末尾
            }

            string textToInsert = $"\n{transformedText}";
            richTextBox1.Text = richTextBox1.Text.Insert(selectionStart, textToInsert);

            richTextBox1.SelectionStart = selectionStart + textToInsert.Length;
            richTextBox1.SelectionLength = 0;
        }

        private void button6_Click(object sender, EventArgs e)
        {
            Program.AppContext.AddNewForm2(); // 使用全局上下文来管理新窗体
        }
        private void button7_Click(object sender, EventArgs e)
        {
            Program.AppContext.AddNewForm(); // 使用全局上下文来管理新窗体
        }

        private void button8_Click(object sender, EventArgs e)
        {
            if (this.TopMost)
            {
                this.TopMost = false;
            }
            else
            {
                this.TopMost = true;
            }
           
        }
    }
}