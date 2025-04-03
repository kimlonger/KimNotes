using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class Form1 : Form
    {
        private Color formColor = Color.FromArgb(220, 230, 240);
        private Color richTextBoxColor = Color.FromArgb(220, 230, 240);
        private readonly ToolTip toolTip;
        private string folderPath = @"D:\kimNotes";
        private string currentFileName;
        private static int formCount = 0; // 用于跟踪窗体的实例数量

        // 在 Form 类中添加以下 API 声明
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;


        public Form1(string fileName = null)
        {
            InitializeComponent();
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
            toolTip.SetToolTip(button8, "置顶当前便签");
            toolTip.SetToolTip(button9, "截屏(待实现)");
            toolTip.SetToolTip(button10, "配置(待实现)");
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
            var latestFile = new DirectoryInfo(folderPath).GetFiles("*.rtf")
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
            string filePath = Path.Combine(folderPath, fileName);
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

            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
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

            string filePath = Path.Combine(folderPath, fileName);

            try
            {
                // 使用RichTextBox的SaveFile方法以RTF格式保存
                richTextBox1.SaveFile(filePath, RichTextBoxStreamType.RichText);
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

        public class ScreenOverlay : Form
        {
            private Point selectionStart;
            private Rectangle selectionRect;
            private readonly Bitmap screenSnapshot;

            public Rectangle SelectedArea { get; private set; }

            public ScreenOverlay()
            {
                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Maximized;
                this.TopMost = true;
                this.DoubleBuffered = true;
                this.Cursor = Cursors.Cross;

                // 截取全屏作为背景
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                screenSnapshot = new Bitmap(bounds.Width, bounds.Height);
                using (Graphics g = Graphics.FromImage(screenSnapshot))
                {
                    g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
                }

                this.Paint += OverlayPaint;
                this.MouseDown += OverlayMouseDown;
                this.MouseMove += OverlayMouseMove;
                this.MouseUp += OverlayMouseUp;
                this.KeyPress += OverlayKeyPress;
            }

            // 添加边缘调整大小功能
            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x84;
                const int HTCLIENT = 1;
                const int HTLEFT = 10;
                const int HTRIGHT = 11;
                const int HTTOP = 12;
                const int HTTOPLEFT = 13;
                const int HTTOPRIGHT = 14;
                const int HTBOTTOM = 15;
                const int HTBOTTOMLEFT = 16;
                const int HTBOTTOMRIGHT = 17;

                if (m.Msg == WM_NCHITTEST)
                {
                    int borderWidth = 10; // 边缘检测宽度
                    Point pos = new Point(m.LParam.ToInt32());
                    pos = this.PointToClient(pos);

                    if (pos.X <= borderWidth && pos.Y <= borderWidth)
                        m.Result = (IntPtr)HTTOPLEFT;
                    else if (pos.X >= this.ClientSize.Width - borderWidth && pos.Y <= borderWidth)
                        m.Result = (IntPtr)HTTOPRIGHT;
                    else if (pos.X <= borderWidth && pos.Y >= this.ClientSize.Height - borderWidth)
                        m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (pos.X >= this.ClientSize.Width - borderWidth && pos.Y >= this.ClientSize.Height - borderWidth)
                        m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (pos.X <= borderWidth)
                        m.Result = (IntPtr)HTLEFT;
                    else if (pos.X >= this.ClientSize.Width - borderWidth)
                        m.Result = (IntPtr)HTRIGHT;
                    else if (pos.Y <= borderWidth)
                        m.Result = (IntPtr)HTTOP;
                    else if (pos.Y >= this.ClientSize.Height - borderWidth)
                        m.Result = (IntPtr)HTBOTTOM;
                    else
                        m.Result = (IntPtr)HTCLIENT;
                    return;
                }
                base.WndProc(ref m);
            }

            private void OverlayMouseDown(object sender, MouseEventArgs e)
            {
                selectionStart = e.Location;
                selectionRect = Rectangle.Empty;
            }

            private void OverlayMouseMove(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;

                int x = Math.Min(selectionStart.X, e.X);
                int y = Math.Min(selectionStart.Y, e.Y);
                int width = Math.Abs(e.X - selectionStart.X);
                int height = Math.Abs(e.Y - selectionStart.Y);

                selectionRect = new Rectangle(x, y, width, height);
                this.Invalidate();
            }

            // 修改 OverlayMouseUp 方法
            private void OverlayMouseUp(object sender, MouseEventArgs e)
            {
                if (selectionRect.Width <= 0 || selectionRect.Height <= 0) return;

                // 转换为屏幕绝对坐标
                Point screenTopLeft = this.PointToScreen(selectionRect.Location);
                SelectedArea = new Rectangle(
                    screenTopLeft.X,
                    screenTopLeft.Y,
                    selectionRect.Width,
                    selectionRect.Height
                );
                this.DialogResult = DialogResult.OK;
                this.Close();
            }

            private void OverlayKeyPress(object sender, KeyPressEventArgs e)
            {
                if (e.KeyChar == (char)Keys.Escape) this.Close();
            }

            private void OverlayPaint(object sender, PaintEventArgs e)
            {
                using (TextureBrush brush = new TextureBrush(screenSnapshot))
                {
                    e.Graphics.FillRectangle(brush, this.ClientRectangle);
                }

                // 绘制选区
                if (!selectionRect.IsEmpty)
                {
                    using (Pen pen = new Pen(Color.Red, 2))
                    {
                        e.Graphics.DrawRectangle(pen, selectionRect);
                    }

                    // 高亮选区外部区域
                    using (Region region = new Region(this.ClientRectangle))
                    {
                        region.Exclude(selectionRect);
                        e.Graphics.FillRegion(new SolidBrush(Color.FromArgb(128, Color.Black)), region);
                    }
                }
            }

            // 在ScreenOverlay类中添加：
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                // 显示坐标信息
                if (!selectionRect.IsEmpty)
                {
                    string info = $"{selectionRect.Width} x {selectionRect.Height}";
                    e.Graphics.DrawString(info, this.Font, Brushes.White,
                        selectionRect.X + 5, selectionRect.Y + 5);
                }
            }

            // 添加网格线辅助定位
            private void DrawGrid(Graphics g)
            {
                using (Pen gridPen = new Pen(Color.FromArgb(50, Color.White)))
                {
                    for (int x = 0; x < this.Width; x += 50)
                        g.DrawLine(gridPen, x, 0, x, this.Height);
                    for (int y = 0; y < this.Height; y += 50)
                        g.DrawLine(gridPen, 0, y, this.Width, y);
                }
            }
        }

        // 修改后的按钮点击事件
        private void button9_Click(object sender, EventArgs e)
        {
            using (ScreenOverlay overlay = new ScreenOverlay())
            {
                if (overlay.ShowDialog() != DialogResult.OK) return;

                Rectangle area = overlay.SelectedArea;
                Bitmap screenshot = new Bitmap(area.Width, area.Height);
                using (Graphics g = Graphics.FromImage(screenshot))
                {
                    g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                }

                Form screenshotForm = new Form();
                screenshotForm.FormBorderStyle = FormBorderStyle.None;
                screenshotForm.TopMost = true;
                screenshotForm.ShowInTaskbar = false; // 防止出现在任务栏

                // 精准设置窗体位置
                screenshotForm.StartPosition = FormStartPosition.Manual;
                screenshotForm.Location = new Point(area.Left, area.Top);
                screenshotForm.ClientSize = new Size(area.Width, area.Height);

                PictureBox pb = new PictureBox();

                pb.Image = screenshot;
                pb.SizeMode = PictureBoxSizeMode.StretchImage;
                pb.Dock = DockStyle.Fill;
                screenshotForm.Controls.Add(pb);

                // 保留原有交互功能
                pb.MouseWheel += (s, me) =>
                {
                    float scale = me.Delta > 0 ? 1.1f : 0.9f;
                    screenshotForm.Width = (int)(screenshotForm.Width * scale);
                    screenshotForm.Height = (int)(screenshotForm.Height * scale);
                };

                pb.DoubleClick += (s, me) => screenshotForm.Close();
                pb.MouseDown += (s, me) =>
                {
                    if (me.Button == MouseButtons.Left)
                    {
                        ReleaseCapture();
                        SendMessage(screenshotForm.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                    }
                };

                screenshotForm.Show();
            }
        }


        //// 在ScreenOverlay类中添加：
        //protected override void OnPaint(PaintEventArgs e)
        //{
        //    base.OnPaint(e);

        //    // 显示坐标信息
        //    if (!selectionRect.IsEmpty)
        //    {
        //        string info = $"{selectionRect.Width} x {selectionRect.Height}";
        //        e.Graphics.DrawString(info, this.Font, Brushes.White,
        //            selectionRect.X + 5, selectionRect.Y + 5);
        //    }
        //}

        //// 添加网格线辅助定位
        //private void DrawGrid(Graphics g)
        //{
        //    using (Pen gridPen = new Pen(Color.FromArgb(50, Color.White)))
        //    {
        //        for (int x = 0; x < this.Width; x += 50)
        //            g.DrawLine(gridPen, x, 0, x, this.Height);
        //        for (int y = 0; y < this.Height; y += 50)
        //            g.DrawLine(gridPen, 0, y, this.Width, y);
        //    }
        //}
    }
}