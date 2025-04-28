using KimNotes.utils;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class note : Form
    {
        private Color formColor = Color.FromArgb(220, 230, 240);
        private Color richTextBoxColor = Color.FromArgb(220, 230, 240);
        private readonly ToolTip toolTip;
        private string currentFileName;
        private static int formCount = 0; // 用于跟踪窗体的实例数量

        //是否启用无痕模式
        private bool trace = Convert.ToBoolean(InitConfig.GetConfigValue("checkBox3"));
        //笔记存储位置
        private string notePath = InitConfig.GetConfigValue("notesPath");

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
        public note(string fileName = null)
        {
            Win32ApiHelper.SetProcessDpiAwareness(Win32ApiHelper.PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);

            InitializeComponent();

            // 字体设置（建议在设计器里设置，代码中仅保留默认值）
            richTextBox1.Font = new Font("Calibri", 10.5f);
            this.KeyPreview = true; // 允许窗体接收键盘事件
            SetFormPosition();
            this.BackColor = formColor; // 设置窗体背景颜色
            SetUpRichTextBox();
            SetUpButtons(button1, button2, button3, button5, button9, button4, button6, button7, button8, button10);
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
            toolTip.SetToolTip(button5, "翻译");
            toolTip.SetToolTip(button4, "大小写转换");
            toolTip.SetToolTip(button9, "便签列表");
            toolTip.SetToolTip(button6, "新建便签");
            toolTip.SetToolTip(button7, "置顶便签");
            toolTip.SetToolTip(button8, "截屏");
            toolTip.SetToolTip(button10, "配置");
            // toolTip.SetToolTip(button11, "吉祥物（实现中）");
            formCount++; // 增加窗体计数
            if (formCount == 1 && string.IsNullOrEmpty(fileName))
            {
                if (!trace)
                {
                    LoadLatestFileContent(); // 首次启动加载最新文件
                }
            }
            else if (!string.IsNullOrEmpty(fileName))
            {
                LoadFileContent(fileName); // 加载指定文件
                currentFileName = fileName;
            }
            FixDpiScaling(); // 修复DPI缩放问题
        }

        private void SetFormPosition()
        {
            // 获取屏幕的工作区域
            var screenBounds = Screen.PrimaryScreen.WorkingArea;

            // 计算窗体的位置
            int x = screenBounds.Width * 3 / 4; // 从左到右宽度的 3/4 位置
            int y = screenBounds.Height / 8;   // 从上到下高度的 1/4 位置

            // 设置窗体的位置
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(x, y);
        }

        private void FixDpiScaling()
        {
            // 重新计算窗体和控件大小
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float dpiScale = g.DpiX / 96f;
                if (dpiScale > 1.0f)
                {
                    // 调整窗体大小
                    this.Width = (int)(this.Width * dpiScale);
                    this.Height = (int)(this.Height * dpiScale);

                    // 调整按钮
                    AdjustButtonsForDpi(dpiScale);

                    // 调整RichTextBox大小
                    AdjustRichTextBoxSize();
                }
            }
        }

        private void AdjustButtonsForDpi(float dpiScale)
        {
            // 按钮之间的间距
            int buttonSpacing = 10; // 默认间距为 10 像素
            int currentLeft = buttonSpacing; // 按钮的起始位置

            // 获取所有按钮，并按名称自定义排序
            var buttons = this.Controls.OfType<Button>()
                .OrderBy(btn =>
                {
                    // 特殊处理 button10，使其总是位于最后
                    if (btn.Name == "button10")
                        return int.MaxValue; // 确保 button10 排在最后

                    // 提取数字并进行比较
                    string numberPart = new string(btn.Name.Where(char.IsDigit).ToArray());
                    return int.TryParse(numberPart, out int number) ? number : int.MaxValue;
                })
                .ToList();

            // 按顺序调整按钮
            foreach (Button btn in buttons)
            {
                // 调整按钮高度和宽度
                btn.Height = (int)(btn.Height * dpiScale);
                btn.Width = (int)(btn.Width * dpiScale);

                // 设置按钮位置：保持在窗体底部，水平排列
                btn.Top = this.ClientSize.Height - btn.Height - 10; // 保持按钮在窗体底部
                btn.Left = currentLeft; // 设置按钮的水平位置

                // 更新下一个按钮的起始位置
                currentLeft += btn.Width + buttonSpacing;
            }
        }

        private void AdjustRichTextBoxSize()
        {
            if (richTextBox1 != null)
            {
                int buttonHeight = 0;

                // 获取所有可见按钮中的最大高度，以确保不覆盖它们
                foreach (Control c in this.Controls)
                {
                    if (c is Button && c.Visible)
                    {
                        buttonHeight = Math.Max(buttonHeight, c.Height);
                    }
                }

                // 设置RichTextBox的高度，确保其底部在所有按钮上方，留出一些额外空间（例如20像素）
                richTextBox1.Height = this.ClientSize.Height - buttonHeight - 30; // 留出额外的间距（20px + 10px）

                // 设置RichTextBox的位置，确保它从窗体顶部开始，不覆盖其他控件。
                richTextBox1.Top = 10;
                richTextBox1.Left = 10;
                richTextBox1.Width = this.ClientSize.Width - 20; // 确保宽度适应窗体大小，留出左右边距。
            }
        }

        private void LoadLatestFileContent()
        {
            if (!Directory.Exists(notePath))
            {
                Directory.CreateDirectory(notePath);
                return;
            }
            var latestFile = new DirectoryInfo(notePath).GetFiles("*.rtf")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (latestFile != null)
            {
                // 使用LoadFile方法加载RTF文件
                richTextBox1.LoadFile(latestFile.FullName, RichTextBoxStreamType.RichText);
                currentFileName = latestFile.Name;
            }
        }

        private void LoadFileContent(string fileName)
        {
            string filePath = Path.Combine(notePath, fileName);
            if (File.Exists(filePath))
            {
                // 使用LoadFile方法加载RTF文件
                richTextBox1.LoadFile(filePath, RichTextBoxStreamType.RichText);
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
                button.BackColor = formColor;
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

            if (!Directory.Exists(notePath))
            {
                Directory.CreateDirectory(notePath);
            }

            string fileName;

            if (string.IsNullOrEmpty(currentFileName))
            {
                fileName = $"{DateTime.Now:yyyyMMddHHmmss}.rtf";
            }
            else
            {
                fileName = currentFileName;
            }

            string filePath = Path.Combine(notePath, fileName);

            try
            {
                //判断是否是无痕模式
                if (!trace)
                {
                    //保存
                    richTextBox1.SaveFile(filePath, RichTextBoxStreamType.RichText);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存笔记时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 修改后的按钮点击事件
        private void button9_Click(object sender, EventArgs e)
        {
            var screenshotForm = ScreenshotHelper.CaptureInteractive(InitConfig.GetConfigValue("imagesPath"), trace);
            screenshotForm?.Show();
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

            // 判断是否包含中文
            bool containsChinese = ContainsChinese(selectedText);

            Translator translator = new Translator();
            string translatedText;

            // 根据是否包含中文选择翻译方向
            if (containsChinese)
            {
                translatedText = translator.Translate(selectedText, "auto", "en");
            }
            else
            {
                translatedText = translator.Translate(selectedText, "auto", "zh");
            }
            int selectionStart = richTextBox1.SelectionStart + richTextBox1.SelectionLength;
            if (selectionStart == 0)
            {
                selectionStart = selectedText.Length;
            }
            string textToInsert = $"\n{translatedText}";
            // 在当前位置插入翻译后的文本
            richTextBox1.Text = richTextBox1.Text.Insert(selectionStart, textToInsert);
            // 更新光标位置
            richTextBox1.SelectionStart = selectionStart + textToInsert.Length;
            // 清除选区长度，避免高亮显示任何文本
            richTextBox1.SelectionLength = 0;
        }

        // 检查字符串是否包含中文字符的方法
        private bool ContainsChinese(string input)
        {
            return input.Any(c => c >= 0x4E00 && c <= 0x9FA5);
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
            Program.AppContext.AddNewForm2(notePath); // 使用全局上下文来管理新窗体
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



        private void button10_Click(object sender, EventArgs e)
        {
            Program.AppContext.AddNewForm3();
        }
    }


}