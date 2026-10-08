using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes.settings
{
    public partial class ConfigForm : Form
    {
        private static string appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KimNotes"
        );
        private static string noteConfig = Path.Combine(appDataPath, "config.txt");

        private Panel chromeBar;
        private Panel bodyPanel;
        private Panel footBar;
        private NoteTheme currentTheme;

        // 主题强调色联动对象（换主题时重刷）
        private SwitchControl sw1, sw2;
        private Button keycapBtn, browse1, browse2;
        private Label hintLabel, aboutVersion, aboutSite;

        // 视觉常量（96dpi 基准，运行时按 Dpi() 缩放）
        private static readonly Color TextC = Color.FromArgb(43, 47, 54);
        private static readonly Color MutedC = Color.FromArgb(138, 146, 158);
        private static readonly Color LineC = Color.FromArgb(236, 239, 243);
        private static readonly Color FieldBg = Color.FromArgb(245, 247, 250);
        private static readonly Color FieldLine = Color.FromArgb(223, 228, 234);
        private static readonly Color OffGray = Color.FromArgb(213, 219, 227);

        private Font nameFont, labelFont, smallFont;

        private const int HDR_H = 30;   // 分区标题（含下划线）
        private const int CGAP = 4;     // 下划线与内容间距
        private const int GAP = 20;     // 分区间距
        private const int ROW_H = 40;   // 单行高
        private const int CELL_H = 36;  // 网格单元高
        private const int LABW = 78;    // 行标签宽
        private const int CTRL_X = 84;  // 控件列统一起始（标签与控件间留呼吸感）
        private const int LAB2W = 36;   // 关于区标签宽
        private const int COLGAP = 14;  // 两列间距
        private const int SW_W = 40, SW_H = 22, SW_GAP = 10;
        private const int BROWSE_W = 64;

        public ConfigForm()
        {
            InitializeComponent();
            currentTheme = NoteTheme.Current();
            chromeBar = FormChrome.Apply(this, "设置", false, null, currentTheme.Chrome, currentTheme.ChromeText);
            this.Padding = new Padding(Dpi(5));
            BuildLayout();
        }

        private int Dpi(int v) => (int)Math.Round(v * (DeviceDpi / 96f));

        private Color Accent => currentTheme.IconHover;
        private Color AccentSoft => Blend(Accent, Color.White, 0.82f);

        protected override CreateParams CreateParams
        {
            get { return FormChrome.WithShadow(base.CreateParams); }
        }

        private void ConfigForm_Load(object sender, EventArgs e)
        {
            textBox1.ReadOnly = true;
            textBox3.ReadOnly = true;
            textBox4.ReadOnly = true;
            textBox3.DoubleClick += (s2, e2) => ChooseFolderPath(textBox3);
            textBox4.DoubleClick += (s2, e2) => ChooseFolderPath(textBox4);

            this.KeyPreview = true;
            this.KeyDown += ConfigForm_KeyDown;

            SetFormPosition();
            ReadData();
            SyncSwitchesFromChecks();
            UpdateKeycap();
            RestyleTheme(currentTheme);
        }

        // ===== 布局构建 =====
        private void BuildLayout()
        {
            nameFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            labelFont = new Font("Microsoft YaHei UI", 9f);
            smallFont = new Font("Microsoft YaHei UI", 8.5f);

            bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(Dpi(16), Dpi(16), Dpi(16), Dpi(8))
            };

            BuildGeneral();
            BuildHotkey();
            BuildStorage();
            BuildAbout();

            // 隐藏但保留的配置载体（云端备份 / 检查更新，功能未上线）
            checkBox2.Visible = false;
            buttonUpdate.Visible = false;
            // textBox1 仅作为快捷键原始值载体（键帽 button 负责显示/捕获），隐藏保留
            textBox1.Visible = false;
            // label5/label8 为设计器遗留控件，关于区改用新建 AutoSize 标签，隐藏保留
            label5.Visible = false;
            label8.Visible = false;
            bodyPanel.Controls.Add(textBox1);
            bodyPanel.Controls.Add(checkBox2);
            bodyPanel.Controls.Add(buttonUpdate);
            bodyPanel.Controls.Add(label5);
            bodyPanel.Controls.Add(label8);

            footBar = new Panel { Dock = DockStyle.Bottom, Height = Dpi(56), BackColor = Color.White };
            footBar.Paint += (s, e) =>
            {
                using (var pen = new Pen(LineC)) e.Graphics.DrawLine(pen, 0, 0, footBar.Width, 0);
            };
            BuildFooter();

            // 退役 groupBox（需要的子控件已迁出，其余随 groupBox 一并释放）
            foreach (var gb in new[] { groupBox1, groupBox2, groupBox3, groupBox4, groupBox5 })
            {
                this.Controls.Remove(gb);
                gb.Dispose();
            }

            Controls.Add(bodyPanel);
            Controls.Add(footBar);
            bodyPanel.BringToFront();

            this.ClientSize = new Size(Dpi(500), Dpi(544));
            this.MinimumSize = new Size(Dpi(460), Dpi(544));
        }

        // 通用：分区面板（Dock=Top，含下划线与底部间距），返回内容起始 y
        private Panel NewSection(string title, int height)
        {
            var sec = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dpi(height),
                BackColor = Color.White
            };
            sec.Paint += (s, e) =>
            {
                int uy = Dpi(HDR_H) - 1;
                using (var pen = new Pen(LineC)) e.Graphics.DrawLine(pen, 0, uy, sec.Width, uy);
            };
            var name = new Label
            {
                Text = title,
                AutoSize = true,
                Font = nameFont,
                ForeColor = TextC,
                Location = new Point(0, Dpi(5))
            };
            sec.Controls.Add(name);
            bodyPanel.Controls.Add(sec);
            sec.BringToFront(); // Dock=Top：BringToFront 保证按添加顺序自上而下
            return sec;
        }

        private void BuildGeneral()
        {
            var sec = NewSection("常规", HDR_H + CGAP + 4 + CELL_H + GAP);

            // checkBox1/checkBox3 保留为数据载体，隐藏；开关双向绑定
            checkBox1.Visible = false;
            checkBox3.Visible = false;
            sec.Controls.Add(checkBox1);
            sec.Controls.Add(checkBox3);

            sw1 = MakeSwitch(sec, "开机启动");
            sw2 = MakeSwitch(sec, "无痕模式");
            sw1.CheckedChanged += (s, e) => checkBox1.Checked = sw1.Checked;
            sw2.CheckedChanged += (s, e) => checkBox3.Checked = sw2.Checked;

            sec.Resize += (s, e) =>
            {
                int cw = sec.ClientSize.Width;
                int colW = (cw - Dpi(COLGAP)) / 2;
                int y = Dpi(HDR_H + CGAP + 4) + (Dpi(CELL_H) - sw1.Height) / 2;
                LayoutCell(sw1, 0, y);
                LayoutCell(sw2, colW + Dpi(COLGAP), y);
            };
        }

        private SwitchControl MakeSwitch(Panel parent, string text)
        {
            var sw = new SwitchControl { Size = new Size(Dpi(SW_W), Dpi(SW_H)) };
            var lb = new Label
            {
                Text = text,
                AutoSize = true,
                Font = labelFont,
                ForeColor = TextC
            };
            parent.Controls.Add(sw);
            parent.Controls.Add(lb);
            sw.Tag = lb;
            return sw;
        }

        private void LayoutCell(SwitchControl sw, int x, int y)
        {
            sw.Location = new Point(x, y);
            var lb = (Label)sw.Tag;
            lb.Location = new Point(x + sw.Width + Dpi(SW_GAP), y + (sw.Height - lb.Height) / 2);
        }

        private void BuildHotkey()
        {
            var sec = NewSection("快捷键", HDR_H + CGAP + ROW_H + GAP);

            var lab = new Label
            {
                Text = "快速截屏",
                AutoSize = true,
                Font = labelFont,
                ForeColor = MutedC
            };
            sec.Controls.Add(lab);

            keycapBtn = new Button
            {
                FlatStyle = FlatStyle.Flat,
                BackColor = FieldBg,
                ForeColor = TextC,
                Font = labelFont,
                Size = new Size(Dpi(128), Dpi(32)),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            keycapBtn.FlatAppearance.BorderColor = FieldLine;
            keycapBtn.FlatAppearance.BorderSize = 1;
            keycapBtn.Click += (s, e) => BeginCapture();
            sec.Controls.Add(keycapBtn);

            var hint = new Label
            {
                Text = "Ctrl 固定，点键帽后按一个键",
                AutoSize = false,
                Font = smallFont,
                ForeColor = MutedC,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            hintLabel = hint;
            sec.Controls.Add(hint);

            sec.Resize += (s, e) =>
            {
                int cw = sec.ClientSize.Width;
                int rowY = Dpi(HDR_H + CGAP);
                lab.Location = new Point(0, rowY + (Dpi(ROW_H) - lab.Height) / 2);
                int kx = Dpi(CTRL_X);
                keycapBtn.Location = new Point(kx, rowY + (Dpi(ROW_H) - keycapBtn.Height) / 2);
                int hx = keycapBtn.Right + Dpi(12);
                hint.Location = new Point(hx, rowY + (Dpi(ROW_H) - hint.Height) / 2);
                hint.Width = Math.Max(20, cw - hx);
            };
        }

        private void BuildStorage()
        {
            var sec = NewSection("存储", HDR_H + CGAP + ROW_H * 2 + GAP);

            var lab1 = MakeRowLabel(sec, "笔记位置");
            var lab2 = MakeRowLabel(sec, "截图位置");
            var field3 = MakePathField(sec, textBox3);
            var field4 = MakePathField(sec, textBox4);
            browse1 = MakeBrowse(sec, textBox3);
            browse2 = MakeBrowse(sec, textBox4);

            sec.Resize += (s, e) =>
            {
                int cw = sec.ClientSize.Width;
                int y0 = Dpi(HDR_H + CGAP);
                LayoutPathRow(lab1, field3, browse1, cw, y0);
                LayoutPathRow(lab2, field4, browse2, cw, y0 + Dpi(ROW_H));
            };
        }

        // 路径字段：外框提供上下留白与描边，内嵌无边框文本框避免文字贴边
        private Panel MakePathField(Panel parent, TextBox tb)
        {
            var box = new Panel { BackColor = FieldBg, Height = Dpi(32) };
            box.Paint += (s, e) =>
            {
                using (var pen = new Pen(FieldLine))
                    e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
            };
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = FieldBg;
            tb.ForeColor = TextC;
            tb.Font = new Font("Microsoft YaHei UI", 8.5f);
            tb.Dock = DockStyle.Fill;
            box.Padding = new Padding(Dpi(8), Dpi(6), Dpi(8), Dpi(6));
            box.Controls.Add(tb);
            box.DoubleClick += (s, e) => ChooseFolderPath(tb);
            parent.Controls.Add(box);
            return box;
        }

        private Label MakeRowLabel(Panel parent, string text)
        {
            var lb = new Label
            {
                Text = text,
                AutoSize = true,
                Font = labelFont,
                ForeColor = MutedC
            };
            parent.Controls.Add(lb);
            return lb;
        }

        private Button MakeBrowse(Panel parent, TextBox target)
        {
            var b = new Button
            {
                Text = "浏览",
                FlatStyle = FlatStyle.Flat,
                Font = smallFont,
                Size = new Size(Dpi(BROWSE_W), Dpi(32)),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 1;
            b.Click += (s, e) => ChooseFolderPath(target);
            parent.Controls.Add(b);
            return b;
        }

        private void LayoutPathRow(Label lab, Panel path, Button browse, int cw, int rowY)
        {
            int labH = lab.Height;
            lab.Location = new Point(0, rowY + (Dpi(ROW_H) - labH) / 2);
            int px = Dpi(CTRL_X);
            path.Location = new Point(px, rowY + (Dpi(ROW_H) - path.Height) / 2);
            browse.Location = new Point(cw - browse.Width, rowY + (Dpi(ROW_H) - browse.Height) / 2);
            path.Width = Math.Max(40, browse.Left - Dpi(12) - px);
        }

        private void BuildAbout()
        {
            var sec = NewSection("关于", HDR_H + CGAP + 4 + CELL_H + GAP);

            var lab2a = MakeAboutLabel(sec, "版本");
            var lab2b = MakeAboutLabel(sec, "官网");
            aboutVersion = new Label { AutoSize = true, Font = labelFont, ForeColor = TextC, Text = Application.ProductVersion };
            aboutSite = new Label { AutoSize = true, Font = smallFont, ForeColor = MutedC, Text = "kimlulu.com" };
            sec.Controls.Add(aboutVersion);
            sec.Controls.Add(aboutSite);
            aboutVersion.BringToFront();
            aboutSite.BringToFront();

            sec.Resize += (s, e) =>
            {
                int cw = sec.ClientSize.Width;
                int colW = (cw - Dpi(COLGAP)) / 2;
                int y = Dpi(HDR_H + CGAP + 4);
                int cy = y + (Dpi(CELL_H)) / 2;
                lab2a.Location = new Point(0, cy - lab2a.Height / 2);
                aboutVersion.Location = new Point(Math.Max(Dpi(LAB2W), lab2a.Width + Dpi(6)), cy - aboutVersion.Height / 2);
                int c2 = colW + Dpi(COLGAP);
                lab2b.Location = new Point(c2, cy - lab2b.Height / 2);
                aboutSite.Location = new Point(c2 + Math.Max(Dpi(LAB2W), lab2b.Width + Dpi(6)), cy - aboutSite.Height / 2);
            };
        }

        private Label MakeAboutLabel(Panel parent, string text)
        {
            var lb = new Label
            {
                Text = text,
                AutoSize = true,
                Font = smallFont,
                ForeColor = MutedC
            };
            parent.Controls.Add(lb);
            return lb;
        }

        private void BuildFooter()
        {
            button6.Text = "恢复默认";
            button7.Text = "保存配置";
            button6.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            button7.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            StyleGhost2(button6);
            button6.Size = new Size(Dpi(92), Dpi(32));
            StylePrimary(button7);
            button7.Size = new Size(Dpi(92), Dpi(32));
            footBar.Controls.Add(button6);
            footBar.Controls.Add(button7);
            footBar.Resize += (s, e) =>
            {
                int total = button6.Width + Dpi(12) + button7.Width;
                int x = (footBar.Width - total) / 2;
                int y = (footBar.Height - button6.Height) / 2;
                button6.Location = new Point(x, y);
                button7.Location = new Point(x + button6.Width + Dpi(12), y);
            };
        }

        private void StylePrimary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = Blend(Accent, Color.Black, 0.12f);
            b.Cursor = Cursors.Hand;
        }

        private static void StyleGhost2(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.White;
            b.ForeColor = TextC;
            b.FlatAppearance.BorderColor = FieldLine;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = FieldBg;
            b.Cursor = Cursors.Hand;
        }

        private void StyleBrowse(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.White;
            b.ForeColor = Accent;
            b.FlatAppearance.BorderColor = AccentSoft;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = AccentSoft;
        }

        // ===== 主题联动 =====
        private void RestyleTheme(NoteTheme t)
        {
            currentTheme = t;
            this.BackColor = Color.White;
            if (chromeBar != null)
            {
                chromeBar.BackColor = t.Chrome;
                chromeBar.Height = Dpi(38);
                if (chromeBar.Tag is Label l) l.ForeColor = t.ChromeText;
            }
            Color accent = Accent;
            if (sw1 != null) sw1.Accent = accent;
            if (sw2 != null) sw2.Accent = accent;
            if (button7 != null) StylePrimary(button7);
            if (browse1 != null) StyleBrowse(browse1);
            if (browse2 != null) StyleBrowse(browse2);
            Invalidate(true);
        }

        private void SyncSwitchesFromChecks()
        {
            if (sw1 != null) sw1.Checked = checkBox1.Checked;
            if (sw2 != null) sw2.Checked = checkBox3.Checked;
        }

        // ===== 快捷键键帽 =====
        private bool capturing;

        private void UpdateKeycap()
        {
            if (keycapBtn == null) return;
            string key = string.IsNullOrWhiteSpace(textBox1.Text) ? "—" : textBox1.Text.Trim();
            keycapBtn.Text = capturing ? "Ctrl  +  …" : "Ctrl  +  " + key;
            keycapBtn.ForeColor = capturing ? MutedC : TextC;
            if (hintLabel != null)
                hintLabel.Text = capturing ? "按下要与 Ctrl 组合的键，Esc 取消" : "Ctrl 固定，点键帽后按一个键";
        }

        private void BeginCapture()
        {
            capturing = true;
            UpdateKeycap();
            this.Focus();
        }

        private void ConfigForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (!capturing) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Escape)
            {
                capturing = false;
                UpdateKeycap();
                return;
            }
            // Ctrl 为固定修饰键：单独按下修饰键不结束捕获，等用户的那个键
            if (IsModifierKey(e.KeyCode)) return;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                textBox1.Text = "";
            }
            else
            {
                textBox1.Text = e.KeyCode.ToString();
            }
            capturing = false;
            UpdateKeycap();
        }

        private static bool IsModifierKey(Keys k)
        {
            switch (k)
            {
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu:
                    return true;
                default:
                    return false;
            }
        }

        // 顶部命中环涂成标题栏同色，无缝覆盖
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int band = this.Padding.Top + (chromeBar != null ? chromeBar.Height : 0);
            if (band > 0)
            {
                using (var b = new SolidBrush(chromeBar != null ? chromeBar.BackColor : this.BackColor))
                    e.Graphics.FillRectangle(b, 0, 0, this.ClientSize.Width, band);
            }
        }

        // 无边框边缘拉伸
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84)
            {
                int lp = m.LParam.ToInt32();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                int code = FormChrome.HitTest(this, this.PointToClient(new Point(x, y)), Math.Max(this.Padding.Left, 4));
                if (code != 0) m.Result = (IntPtr)code;
            }
        }

        private void SetFormPosition()
        {
            var screenBounds = Screen.PrimaryScreen.WorkingArea;
            int x = screenBounds.Width * 3 / 4 - this.Width;
            int y = (screenBounds.Height - this.Height) / 2;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(x, y);
        }

        private void ChooseFolderPath(TextBox textBox)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    textBox.Text = folderDialog.SelectedPath;
                    textBox.SelectionStart = textBox.Text.Length;
                    textBox.SelectionLength = 0;
                }
            }
        }

        private void ReadData()
        {
            if (!File.Exists(noteConfig)) return;
            string[] lines = File.ReadAllLines(noteConfig);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                switch (key)
                {
                    case "checkBox1": checkBox1.Checked = Convert.ToBoolean(value); break;
                    case "checkBox2": checkBox2.Checked = Convert.ToBoolean(value); break;
                    case "checkBox3": checkBox3.Checked = Convert.ToBoolean(value); break;
                    case "shortcutKey": textBox1.Text = value; break;
                    case "notesPath": textBox3.Text = value; break;
                    case "imagesPath": textBox4.Text = value; break;
                }
            }
        }

        private void SaveData()
        {
            using (StreamWriter sw = new StreamWriter(noteConfig))
            {
                sw.WriteLine($"checkBox1={checkBox1.Checked}");
                sw.WriteLine($"checkBox2={checkBox2.Checked}");
                sw.WriteLine($"checkBox3={checkBox3.Checked}");
                sw.WriteLine($"shortcutKey={textBox1.Text}");
                sw.WriteLine($"notesPath={textBox3.Text}");
                sw.WriteLine($"imagesPath={textBox4.Text}");
            }
        }

        private void button7_Click(object sender, EventArgs e)
        {
            SaveData();
            MyApplicationContext.RestartApplication();
            Application.Exit();
        }

        private void button6_Click(object sender, EventArgs e)
        {
            InitConfig.InitData();
            MyApplicationContext.RestartApplication();
            Application.Exit();
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e) { }
        private void button2_Click(object sender, EventArgs e) { }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
