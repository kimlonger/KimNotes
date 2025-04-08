using KimNotes.settings;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KimNotes
{
    public partial class note : Form
    {
        private Color formColor = Color.FromArgb(220, 230, 240);
        private Color richTextBoxColor = Color.FromArgb(220, 230, 240);
        private readonly ToolTip toolTip;
        private string currentFileName;
        private static int formCount = 0; // 用于跟踪窗体的实例数量
        private string noteConfig = "D:\\kimNotes\\config\\config.txt";

        //是否开机启动
        private bool startup = true;
        //是否自动更新
        private bool automaticUpdate = true;
        //热键
        private string hotKey = "F1";
        //图片存储位置
        private string imagePath = "D:\\kimNotes\\images";
        //笔记存储位置
        private string notePath = "D:\\kimNotes\\notes";
        // P/Invoke 声明
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // 定义热键ID（可以是任意值）
        private const int HOTKEY_ID = 1;

        public note(string fileName = null)
        {
           
            InitializeComponent();
            //初始化配置
            if (!File.Exists(noteConfig))
            {
                // 如果配置文件不存在，则创建文件夹和文件，并调用数据初始化方法
                Directory.CreateDirectory(Path.GetDirectoryName(noteConfig));
                File.Create(noteConfig).Close();
                InitData();
            }
            //读取文件配置
            ReadData();
            RegisterGlobalHotKey();
            // 根据 startup 值设置开机启动
            SetStartup(startup);
            //监听键盘事件  获取hotKey的值  监听用户使用摁下快捷键  Ctrl+ hotKey 如果有  则启动截屏功能 即 调用button9_Click方法
            this.KeyPreview = true; // 允许窗体接收键盘事件
            this.StartPosition = FormStartPosition.Manual;
            // 计算屏幕左边和高度的1/4位置
            int targetX = (Screen.PrimaryScreen.WorkingArea.Width / 4);
            int targetY = (Screen.PrimaryScreen.WorkingArea.Height / 4);
            // 设置窗体位置
            this.Location = new Point(targetX, targetY);
            this.BackColor = formColor; // 设置窗体背景颜色
            SetUpRichTextBox();
            SetUpButtons(button1, button2, button3, button4, button6, button5, button7, button8, button9, button10);
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
            toolTip.SetToolTip(button8, "置顶便签");
            toolTip.SetToolTip(button9, "截屏");
            toolTip.SetToolTip(button10, "配置");
           // toolTip.SetToolTip(button11, "吉祥物（实现中）");
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
        // 注册全局热键
        private void RegisterGlobalHotKey()
        {
            uint hotkey = (uint)Keys.F1; // 默认值
            Keys parsedKey;
            if (Enum.TryParse<Keys>(hotKey, true, out parsedKey))
            {
                // 如果解析成功，则转换为对应的整数键值
                hotkey = (uint)parsedKey;
            }

            // 注册全局热键，这里假设 0x2 是 Ctrl 键的修饰符
            RegisterHotKey(this.Handle, HOTKEY_ID, 0x2, hotkey);
        }

        // 注销全局热键
        private void UnregisterGlobalHotKey()
        {
            UnregisterHotKey(this.Handle, HOTKEY_ID);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
            {
                // 当用户按下 Ctrl + HotKey 时执行截屏功能
                button9_Click(this, new EventArgs());
            }
        }

        private void SetStartup(bool enable)
        {
            string appName = "小羊便签"; // 设置你的应用程序名称
            string exePath = Application.ExecutablePath; // 获取当前应用程序的路径

            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (enable)
                {
                    key.SetValue(appName, exePath); // 添加到开机启动项
                }
                else
                {
                    key.DeleteValue(appName, false); // 从开机启动项中删除
                }
            }
        }

        private void InitData()
        {
            using (StreamWriter sw = new StreamWriter(noteConfig))
            {
                sw.WriteLine($"checkBox1=True");
                sw.WriteLine($"checkBox2=True");
                sw.WriteLine($"shortcutKey=F1");
                sw.WriteLine($"notesPath=D:\\kimNotes\\notes");
                sw.WriteLine($"imagesPath=D:\\kimNotes\\images");

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
                // 使用RichTextBox的SaveFile方法以RTF格式保存
                richTextBox1.SaveFile(filePath, RichTextBoxStreamType.RichText);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存笔记时发生错误: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UnregisterGlobalHotKey(); // 窗体关闭时注销快捷键
        }

        // 修改后的按钮点击事件
        private void button9_Click(object sender, EventArgs e)
        {
            var screenshotForm = ScreenshotHelper.CaptureInteractive(imagePath);
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

       

        private void button10_Click(object sender, EventArgs e)
        {
            Form f = new ConfigForm();
            f.Show();
        }

        private void ReadData()
        {
            string[] lines = File.ReadAllLines(noteConfig);
            foreach (string line in lines)
            {
                string[] parts = line.Split('=');
                switch (parts[0])
                {
                    case "checkBox1":
                            startup= Convert.ToBoolean(parts[1]);
                        break;
                    case "checkBox2":
                        automaticUpdate = Convert.ToBoolean(parts[1]);
                        break;
                    case "shortcutKey":
                       hotKey = parts[1];
                        break;
                    case "notesPath":
                      notePath = parts[1];
                        break;
                    case "imagesPath":
                      imagePath = parts[1];
                        break;
                }
            }
        }
    }
}