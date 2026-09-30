using KimNotes.utils;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class note : Form
    {
        private const int EM_LINESCROLL = 0x00B6;
        private const int EM_GETFONTEX = 0x043C;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // 获取从 charIndex 开始使用同一字体的字符数（RichTextBox 未公开该接口，走原生消息）
        private int GetFontRunLength(int charIndex)
        {
            int len = SendMessage(richTextBox1.Handle, EM_GETFONTEX, (IntPtr)charIndex, IntPtr.Zero).ToInt32();
            return len > 0 ? len : 1;
        }
        private Color formColor = Color.FromArgb(234, 240, 247);        // 便签底 #eaf0f7
        private Color richTextBoxColor = Color.FromArgb(234, 240, 247);
        private Color toolbarColor = Color.FromArgb(234, 240, 247);     // 工具栏与正文同色，整体统一
        private Color toolbarLineColor = Color.FromArgb(205, 216, 228); // 工具栏顶部分隔线
        private NoteTheme currentTheme;
        private readonly ToolTip toolTip;
        private readonly Timer autoSaveTimer;
        private bool hasUnsavedChanges;
        private bool suppressTextChangedTracking;
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
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW：无边框下保留柔和投影（便笺卡片感）
                return cp;
            }
        }
        public note(string fileName = null)
        {
            InitializeComponent();

            // 字体设置（建议在设计器里设置，代码中仅保留默认值）
            richTextBox1.Font = new Font("Calibri", 10.5f);
            this.KeyPreview = true; // 允许窗体接收键盘事件
            SetFormPosition();
            this.BackColor = formColor; // 设置窗体背景颜色
            SetUpRichTextBox();
            BuildChrome();
            BuildToolbar();
            ApplyTheme(NoteTheme.Current());
            BuildMorePanel();
            this.Resize += (s2, e2) => LayoutMorePanel();
            this.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) HideMore(); };
            richTextBox1.MouseClick += (s2, e2) => HideMore();
            bodyPanel.MouseClick += (s2, e2) => HideMore();
            toolbarPanel.MouseClick += (s2, e2) => HideMore();
            this.Deactivate += (s2, e2) => HideMore();
            // Win11 启用系统圆角，配合投影形成便笺卡片感（Win10 自动忽略）
            this.Load += (s2, e2) =>
            {
                try { int pref = 2; DwmSetWindowAttribute(this.Handle, 33, ref pref, 4); } catch { }
            };
            // 创建一个ToolTip实例并设置属性
            toolTip = new ToolTip
            {
                InitialDelay = 500,
                ReshowDelay = 500,
                ShowAlways = true
            };
            toolTip.SetToolTip(button1, "加粗");
            toolTip.SetToolTip(button2, "切换项目符号");
            toolTip.SetToolTip(button4, "大小写转换");
            toolTip.SetToolTip(button5, "翻译");
            toolTip.SetToolTip(button8, "截屏");
            toolTip.SetToolTip(todoButton, "插入待办");
            toolTip.SetToolTip(moreBtn, "设置");
            toolTip.SetToolTip(button9, "便签列表");
            toolTip.SetToolTip(button6, "新建便签");
            toolTip.SetToolTip(button7, "固定便签");
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

            SystemEvents.SessionEnding += OnSessionEnding;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Application.ApplicationExit += OnApplicationExit;

            autoSaveTimer = new Timer();
            autoSaveTimer.Interval = 30 * 1000; // 30秒自动保存
            autoSaveTimer.Tick += (s, e) => SaveCurrentNoteSafe();
            autoSaveTimer.Start();
        }

        private void SetFormPosition()
        {
            // 获取屏幕的工作区域
            var screenBounds = Screen.PrimaryScreen.WorkingArea;

            // 计算窗体的位置
            int x = screenBounds.Width * 3 / 4; // 从左到右宽度的 3/4 位置
            int y = screenBounds.Height / 8;   // 从上到下高度的 1/4 位置

            // 夹到工作区内，避免窗体超出屏幕
            x = Math.Max(0, Math.Min(x, screenBounds.Width - this.Width));
            y = Math.Max(0, Math.Min(y, screenBounds.Height - this.Height));

            // 设置窗体的位置
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(x, y);
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
                suppressTextChangedTracking = true;
                try
                {
                    // 使用LoadFile方法加载RTF文件
                    richTextBox1.LoadFile(latestFile.FullName, RichTextBoxStreamType.RichText);
                    currentFileName = latestFile.Name;
                    hasUnsavedChanges = false;
                }
                finally
                {
                    suppressTextChangedTracking = false;
                }
            }
        }

        private void LoadFileContent(string fileName)
        {
            string filePath = Path.Combine(notePath, fileName);
            if (File.Exists(filePath))
            {
                suppressTextChangedTracking = true;
                try
                {
                    // 使用LoadFile方法加载RTF文件
                    richTextBox1.LoadFile(filePath, RichTextBoxStreamType.RichText);
                    currentFileName = fileName;
                    hasUnsavedChanges = false;
                }
                finally
                {
                    suppressTextChangedTracking = false;
                }
            }
        }

        private void SetUpRichTextBox()
        {
            richTextBox1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            richTextBox1.BackColor = richTextBoxColor;
            richTextBox1.ForeColor = Color.FromArgb(43, 47, 54); // 正文 #2b2f36
            richTextBox1.BorderStyle = BorderStyle.None;
            richTextBox1.ScrollBars = RichTextBoxScrollBars.None; // 隐藏滚动条
            richTextBox1.WordWrap = true;
            richTextBox1.KeyDown += RichTextBox1_KeyDown;
            richTextBox1.MouseWheel += RichTextBox1_MouseWheel;
            richTextBox1.MouseUp += RichTextBox1_MouseUp;
            richTextBox1.TextChanged += RichTextBox1_TextChanged;
        }

        private void RichTextBox1_MouseWheel(object sender, MouseEventArgs e)
        {
            // 保持可滚动（隐藏滚动条后手动滚动行）
            int lines = e.Delta > 0 ? -3 : 3;
            SendMessage(richTextBox1.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)lines);
        }

        private void RichTextBox1_TextChanged(object sender, EventArgs e)
        {
            if (suppressTextChangedTracking)
            {
                return;
            }

            hasUnsavedChanges = true;
        }

        private void RichTextBox1_KeyDown(object sender, KeyEventArgs e)
        {
            // 检查是否是 Ctrl+V 组合键
            if (e.Control && e.KeyCode == Keys.V)
            {
                e.Handled = true; // 阻止默认的粘贴行为
                HandlePaste();
            }
        }

        private void HandlePaste()
        {
            if (Clipboard.ContainsText())
            {
                // 先取光标处字体，粘贴后恢复（原逻辑硬编码 Calibri）
                Font fontAtCaret = richTextBox1.SelectionFont ?? richTextBox1.Font;
                string plainText = Clipboard.GetText(TextDataFormat.Text);
                int start = richTextBox1.SelectionStart;
                richTextBox1.SelectedText = plainText;
                
                // 设置选中文本的字体
                richTextBox1.Select(start, plainText.Length);
                richTextBox1.SelectionFont = fontAtCaret;
                richTextBox1.SelectionLength = 0; // 清除选择
            }
        }

        // 点击行首的 ☐/☑ 勾选框时切换勾选状态
        private void RichTextBox1_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            int charIndex = richTextBox1.GetCharIndexFromPosition(e.Location);
            int lineIdx = richTextBox1.GetLineFromCharIndex(charIndex);
            int lineStart = richTextBox1.GetFirstCharIndexFromLine(lineIdx);
            if (charIndex != lineStart) return; // 只有点到行首勾选框才切换

            if (lineIdx < 0 || lineIdx >= richTextBox1.Lines.Length) return;
            string lineText = richTextBox1.Lines[lineIdx];
            if (string.IsNullOrEmpty(lineText)) return;

            char first = lineText[0];
            if (first != TodoUtils.BoxOpen && first != TodoUtils.BoxDone) return;

            char newBox = first == TodoUtils.BoxOpen ? TodoUtils.BoxDone : TodoUtils.BoxOpen;
            richTextBox1.Select(lineStart, 1);
            richTextBox1.SelectedText = newBox.ToString();
            richTextBox1.SelectionLength = 0;
            hasUnsavedChanges = true;
            TodoUtils.Invalidate(currentFileName);
        }

        // 「插入待办」：弹窗输入内容+可选提醒时间，生成规范待办行插到当前行上方
        private void TodoInsert_Click(object sender, EventArgs e)
        {
            using (var dlg = new TodoDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string line = TodoUtils.FormatLine(false, dlg.TodoText, dlg.Due);
                int lineIdx = richTextBox1.GetLineFromCharIndex(richTextBox1.SelectionStart);
                int lineStart = richTextBox1.GetFirstCharIndexFromLine(lineIdx);
                richTextBox1.Select(lineStart, 0);
                richTextBox1.SelectedText = line + "\n";
                richTextBox1.SelectionLength = 0;
                hasUnsavedChanges = true;
                TodoUtils.Invalidate(currentFileName);
            }
        }

        // 工具栏为运行期代码构建（设计器不展示），见 BuildToolbar
        private Panel toolbarPanel;
        private ToolIconButton todoButton;
        private ToolIconButton button1, button2, button4, button5, button6, button7, button8, button9;
        private Panel bodyPanel;
        private Panel chromePanel;
        private PictureBox captionIcon;
        private Label captionTitle;
        private ToolIconButton moreBtn, closeBtn;

        // 仿 Windows 便笺：无边框、便签色铺满全窗；顶部无缝 chrome 条放 ⋯ 与 ✕（不属于内容区）
        private void BuildChrome()
        {
            float s = DeviceDpi / 96f;
            this.FormBorderStyle = FormBorderStyle.None;
            // 内缩一圈作为拉伸命中环（子控件不贴边，表单才能收到边缘命中测试）
            this.Padding = new Padding((int)(5 * s));

            chromePanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = (int)(36 * s),
                BackColor = Color.White // 白色标题栏，与浅蓝内容区强对比、层次明确
            };
            // 底部分隔线（随显隐渐变）
            chromePanel.Paint += (s2, e2) =>
            {
                if (chromeFade <= 0.05f) return;
                using (var pen = new Pen(Blend(chromePanel.BackColor, Color.FromArgb(227, 232, 239), chromeFade)))
                    e2.Graphics.DrawLine(pen, 0, chromePanel.Height - 1, chromePanel.Width, chromePanel.Height - 1);
            };
            chromePanel.MouseDown += (s2, e2) =>
            {
                if (e2.Button == MouseButtons.Left)
                {
                    Win32ApiHelper.ReleaseCapture();
                    Win32ApiHelper.SendMessage(this.Handle, Win32ApiHelper.WM_NCLBUTTONDOWN, Win32ApiHelper.HT_CAPTION, 0);
                }
            };

            captionIcon = new PictureBox
            {
                Size = new Size((int)(18 * s), (int)(18 * s)),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            if (this.Icon != null) captionIcon.Image = this.Icon.ToBitmap();
            captionTitle = new Label
            {
                Text = "小羊便签",
                AutoSize = true,
                ForeColor = Color.FromArgb(74, 84, 96),
                Font = new Font("Microsoft YaHei UI", 9.5f * s)
            };

            moreBtn = new ToolIconButton { IconId = "more" };
            moreBtn.Size = new Size((int)(30 * s), (int)(30 * s));
            moreBtn.Click += (s2, e2) => ToggleMore();
            closeBtn = new ToolIconButton { IconId = "close" };
            closeBtn.Size = new Size((int)(30 * s), (int)(30 * s));
            closeBtn.Click += (s2, e2) => this.Close();

            // 标题栏常驻显示（白色 + 分隔线 + 图标标题 + ⋯✕）
            chromeFade = 1f;
            chromeShown = true;
            ApplyChromeFade();

            chromePanel.Controls.Add(captionIcon);
            chromePanel.Controls.Add(captionTitle);
            chromePanel.Controls.Add(moreBtn);
            chromePanel.Controls.Add(closeBtn);
            chromePanel.Resize += (s2, e2) => LayoutChrome(s);
            Controls.Add(chromePanel);
            LayoutChrome(s);
        }

        private bool chromeShown;
        private float chromeFade;   // 0=隐藏(融入内容) .. 1=显示(白底)
        private float chromeScale = 1f;

        private void ApplyChromeFade()
        {
            if (chromePanel == null) return;
            chromePanel.BackColor = Blend(formColor, Color.White, chromeFade);
            bool vis = chromeFade > 0.5f;
            captionIcon.Visible = vis;
            captionTitle.Visible = vis;
            moreBtn.Visible = vis;
            closeBtn.Visible = vis;
            chromePanel.Invalidate();
            this.Invalidate();
        }

        private static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // 应用主题：正文/工具栏/标题栏/图标整套配色切换
        public void ApplyTheme(NoteTheme t)
        {
            currentTheme = t;
            formColor = t.Body;
            richTextBoxColor = t.Body;
            toolbarColor = t.Body;
            toolbarLineColor = t.Divider;

            this.BackColor = t.Body;
            if (bodyPanel != null) bodyPanel.BackColor = t.Body;
            if (richTextBox1 != null) { richTextBox1.BackColor = t.Body; richTextBox1.ForeColor = t.Text; }
            if (toolbarPanel != null) toolbarPanel.BackColor = t.Body;
            if (chromePanel != null) chromePanel.BackColor = t.Chrome;
            if (captionTitle != null) captionTitle.ForeColor = t.ChromeText;

            ToolIconButton.IconColor = t.Icon;
            ToolIconButton.IconHoverColor = t.IconHover;
            bool dark = (t.Body.R * 0.3f + t.Body.G * 0.59f + t.Body.B * 0.11f) < 128;
            ToolIconButton.HoverBackColor = dark ? ControlPaint.Light(t.Body, 0.25f) : ControlPaint.Dark(t.Body, 0.06f);
            ToolIconButton.HoverBorderColor = dark ? ControlPaint.Light(t.Body, 0.45f) : ControlPaint.Dark(t.Body, 0.18f);

            if (swatchButtons.Count > 0) UpdateSwatchChecks();
            this.Invalidate(true);
        }

        // ⋯ 下拉面板：仿便笺，落在便签内部顶部整宽；只含 主题色板 / 便签列表 / 设置
        private Panel morePanel;
        private TableLayoutPanel swatchTable;
        private Panel moreRowList, moreRowSet;
        private readonly List<Button> swatchButtons = new List<Button>();
        private bool moreOpen;

        private void BuildMorePanel()
        {
            morePanel = new Panel { Visible = false, BackColor = Color.White };
            morePanel.Paint += (s2, e2) =>
            {
                using (var pen = new Pen(Color.FromArgb(227, 232, 239)))
                    e2.Graphics.DrawRectangle(pen, 0, 0, morePanel.Width - 1, morePanel.Height - 1);
            };

            // 顶部整宽主题色板条
            swatchTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                ColumnCount = NoteTheme.All.Count,
                RowCount = 1,
                BackColor = Color.White,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            swatchTable.ColumnStyles.Clear();
            for (int i = 0; i < NoteTheme.All.Count; i++)
                swatchTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / NoteTheme.All.Count));
            swatchTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            for (int i = 0; i < NoteTheme.All.Count; i++)
            {
                var theme = NoteTheme.All[i];
                var b = new Button
                {
                    Dock = DockStyle.Fill,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = theme.Body,
                    Margin = Padding.Empty,
                    TabStop = false,
                    Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 11f),
                    ForeColor = Color.FromArgb(43, 47, 54),
                    Text = ""
                };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s2, e2) =>
                {
                    NoteTheme.Save(theme.Id);
                    foreach (Form f in Application.OpenForms)
                    {
                        if (f is note n) n.ApplyTheme(theme);
                    }
                    UpdateSwatchChecks();
                };
                swatchButtons.Add(b);
                swatchTable.Controls.Add(b, i, 0);
            }
            UpdateSwatchChecks();

            var rowList = BuildMenuRow("notes", "便签列表", () => Program.AppContext.AddNewForm2(InitConfig.GetConfigValue("notesPath")));
            var rowSet = BuildMenuRow("gear", "设置", () => Program.AppContext.AddNewForm3());
            moreRowList = rowList;
            moreRowSet = rowSet;

            // Dock=Top 叠加顺序：后加入者靠上
            morePanel.Controls.Add(rowSet);
            morePanel.Controls.Add(rowList);
            morePanel.Controls.Add(swatchTable);
            morePanel.Height = 44 * 3;

            this.Controls.Add(morePanel);
            LayoutMorePanel();
        }

        private Panel BuildMenuRow(string iconId, string text, Action action)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.White };
            var ic = new ToolIconButton { IconId = iconId, Size = new Size(28, 28), Location = new Point(12, 8) };
            var lb = new Label
            {
                Text = text,
                AutoSize = true,
                Location = new Point(48, 14),
                ForeColor = Color.FromArgb(43, 47, 54),
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
            row.Controls.Add(ic);
            row.Controls.Add(lb);
            // 行高变化时图标/文字垂直居中
            row.Resize += (s2, e2) =>
            {
                ic.Location = new Point(12, (row.Height - ic.Height) / 2);
                lb.Location = new Point(48, (row.Height - lb.Height) / 2);
            };

            void Hi(bool on) { row.BackColor = on ? Color.FromArgb(242, 246, 251) : Color.White; }
            row.MouseEnter += (s2, e2) => Hi(true);
            row.MouseLeave += (s2, e2) => Hi(false);
            ic.MouseEnter += (s2, e2) => Hi(true);
            ic.MouseLeave += (s2, e2) => Hi(false);
            lb.MouseEnter += (s2, e2) => Hi(true);
            lb.MouseLeave += (s2, e2) => Hi(false);

            EventHandler act = (s2, e2) => { action(); HideMore(); };
            row.Click += act;
            ic.Click += act;
            lb.Click += act;
            return row;
        }

        private void UpdateSwatchChecks()
        {
            var cur = NoteTheme.Current();
            for (int i = 0; i < swatchButtons.Count && i < NoteTheme.All.Count; i++)
                swatchButtons[i].Text = NoteTheme.All[i].Id == cur.Id ? "✓" : "";
        }

        private void LayoutMorePanel()
        {
            if (morePanel == null || chromePanel == null) return;
            // 从窗口顶端开始盖住标题栏；底边=正文第4行顶端（实测行高），完整盖住前三行且不碰第四行
            int i0 = richTextBox1.GetFirstCharIndexFromLine(0);
            int i1 = richTextBox1.GetFirstCharIndexFromLine(1);
            int realLineH;
            int top0;
            if (i1 >= 0)
            {
                top0 = richTextBox1.GetPositionFromCharIndex(i0).Y;
                realLineH = richTextBox1.GetPositionFromCharIndex(i1).Y - top0;
            }
            else
            {
                top0 = 0;
                realLineH = (int)(richTextBox1.Font.Height * 1.3);
            }
            if (realLineH < 4) realLineH = richTextBox1.Font.Height;
            int contentTop = bodyPanel.Top + richTextBox1.Top + top0;
            int h = Math.Max(44 * 3, contentTop + realLineH * 3 + 1);
            morePanel.Bounds = new Rectangle(0, 0, this.ClientSize.Width, h);
            // 两行菜单平分色板以下的剩余高度，不留空白
            int rowH = (h - swatchTable.Height) / 2;
            if (moreRowList != null) moreRowList.Height = rowH;
            if (moreRowSet != null) moreRowSet.Height = rowH;
            // 顶部两角圆弧，贴合窗口圆角，完美覆盖
            morePanel.Region = RoundedTopRegion(morePanel.Width, h, (int)(8 * (DeviceDpi / 96f)));
        }

        private static Region RoundedTopRegion(int w, int h, int r)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = r * 2;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(w - d, 0, d, d, 270, 90);
            path.AddLine(w, r, w, h);
            path.AddLine(w, h, 0, h);
            path.AddLine(0, h, 0, r);
            path.CloseFigure();
            return new Region(path);
        }

        private void ToggleMore()
        {
            moreOpen = !moreOpen;
            if (moreOpen)
            {
                LayoutMorePanel();
                morePanel.BringToFront();
            }
            morePanel.Visible = moreOpen;
        }

        private void HideMore()
        {
            moreOpen = false;
            if (morePanel != null) morePanel.Visible = false;
        }

        private void LayoutChrome(float s)
        {
            int cy = (chromePanel.Height - closeBtn.Height) / 2;
            closeBtn.Location = new Point(chromePanel.Width - (int)(6 * s) - closeBtn.Width, cy);
            moreBtn.Location = new Point(closeBtn.Left - (int)(2 * s) - moreBtn.Width, cy);
            captionIcon.Location = new Point((int)(10 * s), (chromePanel.Height - captionIcon.Height) / 2);
            captionTitle.Location = new Point(captionIcon.Right + (int)(7 * s), (chromePanel.Height - captionTitle.Height) / 2);
        }

        // 无边框拉伸：边缘命中环返回系统拉伸码（含拉伸光标）
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84) // WM_NCHITTEST
            {
                int lp = m.LParam.ToInt32();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                var pt = this.PointToClient(new Point(x, y));
                int b = Math.Max(this.Padding.Left, 4);
                bool l = pt.X <= b, r = pt.X >= this.ClientSize.Width - b;
                bool t = pt.Y <= b, bo = pt.Y >= this.ClientSize.Height - b;
                if (l && t) m.Result = (IntPtr)13;
                else if (r && t) m.Result = (IntPtr)14;
                else if (l && bo) m.Result = (IntPtr)16;
                else if (r && bo) m.Result = (IntPtr)17;
                else if (l) m.Result = (IntPtr)10;
                else if (r) m.Result = (IntPtr)11;
                else if (t) m.Result = (IntPtr)12;
                else if (bo) m.Result = (IntPtr)15;
            }
        }

        // 顶部+左右拉伸命中环在标题栏高度内涂成标题栏同色，使白色标题栏完美覆盖到边到顶；
        // 标题栏以下的左/右/底命中环为便签色，与内容同色不可见
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int band = this.Padding.Top + (chromePanel != null ? chromePanel.Height : 0);
            if (band > 0)
            {
                using (var b = new SolidBrush(chromePanel != null ? chromePanel.BackColor : formColor))
                    e.Graphics.FillRectangle(b, 0, 0, this.ClientSize.Width, band);
            }
        }

        // 工具栏：普通面板 + 手动等间距布局，按钮均匀铺满底部；DPI 缩放防裁切
        private void BuildToolbar()
        {
            float s = DeviceDpi / 96f;
            // 工具栏面板与按钮由 Designer 创建（设计期可见），运行期在此升级样式
            toolbarPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = (int)(42 * s),
                BackColor = toolbarColor
            };
            // 顶部一条分隔线，勾出工具栏区域（2px、加深更明显）
            toolbarPanel.Paint += (s2, e2) =>
            {
                using (var brush = new SolidBrush(toolbarLineColor))
                {
                    e2.Graphics.FillRectangle(brush, 0, 0, toolbarPanel.Width, 2);
                }
            };

            // 运行期创建工具栏按钮（自绘矢量图标）并接线
            button2 = new ToolIconButton { IconId = "bullet" };   button2.Click += button2_Click;  // 项目符号
            button1 = new ToolIconButton { IconId = "bold" };     button1.Click += button1_Click;  // 加粗
            button4 = new ToolIconButton { IconId = "case" };     button4.Click += button5_Click;  // 大小写
            button5 = new ToolIconButton { IconId = "translate" };button5.Click += button4_Click;  // 翻译
            button6 = new ToolIconButton { IconId = "add" };      button6.Click += button7_Click;  // 新建便签
            button7 = new ToolIconButton { IconId = "pin" };      button7.Click += button8_Click;  // 固定
            button8 = new ToolIconButton { IconId = "scissors" }; button8.Click += button9_Click;  // 截屏
            button9 = new ToolIconButton { IconId = "notes" };    button9.Click += button6_Click;  // 便签列表
            todoButton = new ToolIconButton { IconId = "todo" };  todoButton.Click += TodoInsert_Click; // 插入待办

            // 保持用户原有顺序：≡ B Aa T ➕ 📌 ✂ 📋，新增「插入待办」放末尾
            var ordered = new[] { button2, button1, button4, button5, button6, button7, button8, button9, todoButton };
            foreach (var b in ordered)
            {
                StyleToolButton(b, s);
                toolbarPanel.Controls.Add(b);
            }

            Controls.Add(toolbarPanel);
            RelayoutToolbar();
            this.Resize += (s2, e2) => RelayoutToolbar();

            // 正文容器：用面板 Padding 提供内边距（Dock 填充会丢掉设计器的留白）
            int m = (int)(12 * s);
            bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(m, m, m, 0),
                BackColor = richTextBoxColor
            };
            richTextBox1.Dock = DockStyle.Fill;
            bodyPanel.Controls.Add(richTextBox1);
            Controls.Add(bodyPanel);
            bodyPanel.BringToFront();

            FitWidthToToolbar();
        }

        // 工具栏最小所需宽度：按钮总宽 + 最小间隔
        private int ToolbarNeededWidth()
        {
            int n = toolbarPanel.Controls.Count;
            int w = (n + 1) * 2;
            foreach (Control c in toolbarPanel.Controls) w += c.Width;
            return w;
        }

        // 按钮等间距均匀铺满工具栏底部，避免左挤右空
        private void RelayoutToolbar()
        {
            if (toolbarPanel == null || toolbarPanel.IsDisposed) return;
            var btns = toolbarPanel.Controls.Cast<Control>().ToList(); // 自绘控件非 Button，需按 Control 取
            if (btns.Count == 0) return;

            int clientW = toolbarPanel.ClientSize.Width;
            int clientH = toolbarPanel.ClientSize.Height;
            int totalW = btns.Sum(b => b.Width);
            int gap = (clientW - totalW) / (btns.Count + 1);
            if (gap < 2) gap = 2;

            int x = gap;
            int y = (clientH - btns[0].Height) / 2;
            foreach (var b in btns)
            {
                b.Location = new Point(x, y);
                x += b.Width + gap;
            }
        }

        // 宽度取「工具栏所需」与「舒适最小宽度」的较大者；高度按宽度约 1.05 倍，比例协调不显窄
        private void FitWidthToToolbar()
        {
            float s = DeviceDpi / 96f;
            toolbarPanel.PerformLayout();
            int toolNeed = Math.Max(ToolbarNeededWidth(), 240);
            int width = Math.Max(toolNeed, (int)(420 * s));
            int height = Math.Max((int)(width * 1.05f), (int)(360 * s));

            // 夹到工作区内，避免小屏溢出
            var wa = Screen.FromControl(this).WorkingArea;
            width = Math.Min(width, wa.Width);
            height = Math.Min(height, wa.Height);

            this.MinimumSize = new Size(toolNeed, 240);
            this.ClientSize = new Size(width, height);
        }

        // ToolIconButton 自绘图标与悬停效果（设计器同绘），这里只做尺寸/间距/焦点处理
        private void StyleToolButton(ToolIconButton b, float s)
        {
            b.Size = new Size((int)(32 * s), (int)(32 * s));
            b.Margin = new Padding((int)(1 * s));
            b.Click += (s2, e2) => richTextBox1.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveCurrentNoteSafe();
            base.OnFormClosing(e);

            formCount--; // 窗体关闭时减少计数
            UnregisterShutdownHandlers();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            SaveCurrentNoteSafe();
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            SaveCurrentNoteSafe();
        }

        private void OnApplicationExit(object sender, EventArgs e)
        {
            SaveCurrentNoteSafe();
        }

        private void UnregisterShutdownHandlers()
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            Application.ApplicationExit -= OnApplicationExit;

            if (autoSaveTimer != null)
            {
                autoSaveTimer.Stop();
                autoSaveTimer.Dispose();
            }
        }

        private void SaveCurrentNoteSafe()
        {
            if (trace)
            {
                return;
            }

            if (richTextBox1 == null || richTextBox1.IsDisposed)
            {
                return;
            }

            if (!hasUnsavedChanges)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(richTextBox1.Text))
            {
                return;
            }

            if (!Directory.Exists(notePath))
            {
                Directory.CreateDirectory(notePath);
            }

            if (string.IsNullOrEmpty(currentFileName))
            {
                currentFileName = $"{DateTime.Now:yyyyMMddHHmmss}.rtf";
            }

            string filePath = Path.Combine(notePath, currentFileName);

            try
            {
                richTextBox1.SaveFile(filePath, RichTextBoxStreamType.RichText);
                hasUnsavedChanges = false;
            }
            catch
            {
                // 关机/退出过程中避免弹窗打断流程
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
                // 保存原始选择位置和长度
                int originalStart = richTextBox1.SelectionStart;
                int originalLength = richTextBox1.SelectionLength;

                // 检查选中文本是否全部为粗体
                bool allBold = IsAllTextBold(originalStart, originalLength);

                // 逐字符应用粗体状态
                ApplyBoldToRange(originalStart, originalLength, !allBold);

                // 恢复原始选择
                richTextBox1.Select(originalStart, originalLength);
                richTextBox1.Focus();
            }
            else
            {
                // 处理当前行
                int currentLine = richTextBox1.GetLineFromCharIndex(richTextBox1.SelectionStart);
                int lineStart = richTextBox1.GetFirstCharIndexFromLine(currentLine);
                int lineLength = GetLineLength(currentLine);

                bool allBold = IsAllTextBold(lineStart, lineLength);
                ApplyBoldToRange(lineStart, lineLength, !allBold);

                richTextBox1.Select(lineStart, lineLength);
                richTextBox1.Focus();
            }
        }

        private bool IsAllTextBold(int start, int length)
        {
            if (length <= 0) return false;

            // 按字体区段检查，避免逐字符 Select 造成卡顿
            int index = start;
            int end = start + length;
            while (index < end)
            {
                richTextBox1.Select(index, 1);
                Font font = richTextBox1.SelectionFont;
                if (font == null || !font.Bold)
                {
                    return false;
                }
                index += Math.Max(1, GetFontRunLength(index));
            }
            return true;
        }

        private void ApplyBoldToRange(int start, int length, bool makeBold)
        {
            if (length <= 0) return;

            int index = start;
            int end = start + length;
            while (index < end)
            {
                richTextBox1.Select(index, 1);
                Font currentFont = richTextBox1.SelectionFont;
                int runLength = Math.Max(1, GetFontRunLength(index));
                runLength = Math.Min(runLength, end - index);

                if (currentFont != null)
                {
                    FontStyle newStyle = makeBold ?
                        (currentFont.Style | FontStyle.Bold) :
                        (currentFont.Style & ~FontStyle.Bold);

                    richTextBox1.Select(index, runLength);
                    richTextBox1.SelectionFont = new Font(currentFont.FontFamily, currentFont.Size, newStyle);
                }
                index += runLength;
            }
        }

        private int GetLineLength(int lineNumber)
        {
            int lineStart = richTextBox1.GetFirstCharIndexFromLine(lineNumber);
            int nextLineStart = richTextBox1.GetFirstCharIndexFromLine(lineNumber + 1);

            if (nextLineStart == -1) // 最后一行
            {
                return richTextBox1.Text.Length - lineStart;
            }
            else
            {
                return nextLineStart - lineStart;
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

            // 判断是否包含中文，决定翻译方向
            bool containsChinese = ContainsChinese(selectedText);
            string toLang = containsChinese ? "en" : "zh";

            // 记录插入位置（异步完成时选区可能已变化）
            int insertPos = richTextBox1.SelectionStart + richTextBox1.SelectionLength;

            var button = sender as Button;
            if (button != null) button.Enabled = false;

            // 放到后台线程执行，避免网络等待时界面假死
            System.Threading.Tasks.Task.Run(() => new Translator().Translate(selectedText, "auto", toLang))
                .ContinueWith(t =>
                {
                    if (button != null) button.Enabled = true;

                    if (t.IsFaulted || t.Result == null)
                    {
                        MessageBox.Show("翻译失败，请检查网络或稍后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string textToInsert = $"\n{t.Result}";
                    // 用选区插入，避免整段重赋 Text 导致全文 RTF 格式丢失
                    richTextBox1.Select(insertPos, 0);
                    richTextBox1.SelectedText = textToInsert;
                    richTextBox1.SelectionStart = insertPos + textToInsert.Length;
                    richTextBox1.SelectionLength = 0;
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
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

            string textToInsert = $"\n{transformedText}";
            // 用选区插入，避免整段重赋 Text 导致全文 RTF 格式丢失
            int insertPos = richTextBox1.SelectionStart + richTextBox1.SelectionLength;
            richTextBox1.SelectedText = textToInsert;
            richTextBox1.SelectionStart = insertPos + textToInsert.Length;
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



        private void button2_Click(object sender, EventArgs e)
        {
            if (richTextBox1.SelectionLength > 0)
            {
                // 切换项目符号状态
                richTextBox1.SelectionBullet = !richTextBox1.SelectionBullet;
            }
            else
            {
                // 如果没有选中文本，获取当前行
                int currentLine = richTextBox1.GetLineFromCharIndex(richTextBox1.SelectionStart);
                int lineStart = richTextBox1.GetFirstCharIndexFromLine(currentLine);
                int lineEnd = richTextBox1.Text.Length;
                
                if (currentLine < richTextBox1.GetLineFromCharIndex(richTextBox1.Text.Length))
                {
                    lineEnd = richTextBox1.GetFirstCharIndexFromLine(currentLine + 1);
                }
                
                // 选中当前行
                richTextBox1.Select(lineStart, lineEnd - lineStart);
                // 切换项目符号状态
                richTextBox1.SelectionBullet = !richTextBox1.SelectionBullet;
                // 取消选择
                richTextBox1.SelectionLength = 0;
            }
        }

        public void SetText(string text)
        {
            richTextBox1.Text = text;
            hasUnsavedChanges = true; // 外部写入的内容也要参与自动保存
        }
    }


}
