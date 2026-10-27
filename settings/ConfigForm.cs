using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes.settings
{
    public partial class ConfigForm : Form
    {
        private Panel chromeBar;
        private Panel bodyPanel;
        private Panel footBar;
        private NoteTheme currentTheme;

        // 主题强调色联动对象（换主题时重刷）
        private SwitchControl sw1, sw2;
        private RadioControl rdPopup, rdSound;
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

        private const int HDR_H = 20;
        private const int CGAP = 2;
        private const int GAP = 6;
        private const int ROW_H = 28;
        private const int CELL_H = 24;
        private const int LABW = 78;    // 行标签宽
        private const int CTRL_X = 56;
        private const int LAB2W = 36;   // 关于区标签宽
        private const int COLGAP = 10;
        private const int SW_W = 36, SW_H = 20, SW_GAP = 6;
        private const int BROWSE_W = 34;

        public ConfigForm()
        {
            InitializeComponent();
            currentTheme = NoteTheme.Current();
            chromeBar = FormChrome.Apply(this, "设置", false, null, currentTheme.Chrome, currentTheme.ChromeText);
            this.Padding = new Padding(Dpi(5));
            BuildLayout();
            Deactivate += (s, e) => CancelShortcutCapture();
            Disposed += (s, e) => Program.AppContext?.SetShortcutCapture(false);
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
            nameFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            labelFont = new Font("Microsoft YaHei UI", 8.5f);
            smallFont = new Font("Microsoft YaHei UI", 8f);

            bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.White,
                Padding = new Padding(Dpi(12), Dpi(10), Dpi(12), Dpi(4))
            };

            BuildGeneral();
            BuildHotkey();
            BuildStorage();
            BuildAbout();
            var accountLink = new LinkLabel { Text = "账号与云同步", AutoSize = true, Font = smallFont,
                LinkColor = TextC, ActiveLinkColor = Accent, VisitedLinkColor = TextC,
                LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent,
                Name = "settings-account", AccessibleName = "打开账号与云同步" };
            chromeBar.Controls.Add(accountLink);
            accountLink.Click += (s,e) => Program.AppContext?.ShowAccount();
            Action placeAccount = () => {
                var close = chromeBar.Controls.OfType<ToolIconButton>().First(b => b.IconId == "close");
                accountLink.Location = new Point(Math.Max(Dpi(70),close.Left-Dpi(12)-accountLink.Width), (chromeBar.Height-accountLink.Height)/2);
            };
            chromeBar.Resize += (s,e) => placeAccount(); placeAccount();

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

            footBar = new Panel { Dock = DockStyle.Bottom, Height = Dpi(40), BackColor = Color.White };
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

            this.ClientSize = new Size(Dpi(CompactLayout.SettingsWidth), Dpi(CompactLayout.SettingsHeight));
            this.MinimumSize = new Size(Dpi(CompactLayout.SettingsWidth), Dpi(420));
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
                Location = new Point(0, Dpi(2))
            };
            sec.Controls.Add(name);
            bodyPanel.Controls.Add(sec);
            sec.BringToFront(); // Dock=Top：BringToFront 保证按添加顺序自上而下
            return sec;
        }

        private void BuildGeneral()
        {
            var sec = NewSection("常规", HDR_H + CGAP + 4 + CELL_H + ROW_H + GAP);

            // checkBox1/checkBox3 保留为数据载体，隐藏；开关双向绑定
            checkBox1.Visible = false;
            checkBox3.Visible = false;
            sec.Controls.Add(checkBox1);
            sec.Controls.Add(checkBox3);

            sw1 = MakeSwitch(sec, "开机启动");
            sw2 = MakeSwitch(sec, "无痕模式");
            sw1.CheckedChanged += (s, e) => checkBox1.Checked = sw1.Checked;
            sw2.CheckedChanged += (s, e) => checkBox3.Checked = sw2.Checked;

            // 待办提醒：必选单选，默认「仅弹窗」
            var labRemind = MakeRowLabel(sec, "待办提醒");
            rdPopup = new RadioControl { Text = "仅弹窗", Checked = true, Font = labelFont };
            rdSound = new RadioControl { Text = "弹窗+铃声", Font = labelFont };
            sec.Controls.Add(rdPopup);
            sec.Controls.Add(rdSound);
            rdPopup.BringToFront();
            rdSound.BringToFront();
            rdPopup.CheckedChanged += (s, e) => { if (rdPopup.Checked) rdSound.Checked = false; };
            rdSound.CheckedChanged += (s, e) => { if (rdSound.Checked) rdPopup.Checked = false; };

            sec.Resize += (s, e) =>
            {
                int cw = sec.ClientSize.Width;
                int colW = (cw - Dpi(COLGAP)) / 2;
                int y = Dpi(HDR_H + CGAP + 4) + (Dpi(CELL_H) - sw1.Height) / 2;
                LayoutCell(sw1, 0, y);
                LayoutCell(sw2, colW + Dpi(COLGAP), y);

                int y2 = Dpi(HDR_H + CGAP + 4 + CELL_H);
                labRemind.Location = new Point(0, y2 + (Dpi(ROW_H) - labRemind.Height) / 2);
                int rx = Dpi(CTRL_X);
                rdPopup.Location = new Point(rx, y2 + (Dpi(ROW_H) - rdPopup.Height) / 2);
                rdSound.Location = new Point(rdPopup.Right + Dpi(COLGAP), y2 + (Dpi(ROW_H) - rdSound.Height) / 2);
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
            var sec = NewSection("快捷键", HDR_H + CGAP + ROW_H * 4 + GAP);

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
                Size = new Size(Dpi(120), Dpi(24)),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            keycapBtn.FlatAppearance.BorderColor = FieldLine;
            keycapBtn.FlatAppearance.BorderSize = 1;
            keycapBtn.Click += (s, e) => BeginCapture();
            sec.Controls.Add(keycapBtn);

            var hint = new Label
            {
                Text = "点击键位修改 · Ctrl 固定",
                AutoSize = false,
                Font = smallFont,
                ForeColor = MutedC,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            hintLabel = hint;
            sec.Controls.Add(hint);
            string[] names = { "唤回贴图", "上一张", "下一张" };
            string[] keys = { "recallKey", "previousImageKey", "nextImageKey" };
            var historyLabels = new Label[3];
            for (int i = 0; i < 3; i++)
            {
                string key = keys[i];
                historyKeyValues[key] = InitConfig.GetConfigValue(key);
                var label = new Label { Text = names[i], AutoSize = true, Font = labelFont, ForeColor = MutedC };
                var button = new Button { FlatStyle = FlatStyle.Flat, BackColor = FieldBg, ForeColor = TextC, Font = labelFont,
                    Size = new Size(Dpi(120), Dpi(24)), Cursor = Cursors.Hand };
                button.FlatAppearance.BorderColor = FieldLine;
                historyKeyButtons[key] = button;
                historyLabels[i] = label;
                button.Click += (s, e) => { Program.AppContext?.SetShortcutCapture(true); capturing = false; historyCapturing = key; UpdateHistoryKeycaps(); Focus(); };
                sec.Controls.Add(label); sec.Controls.Add(button);
            }
            UpdateHistoryKeycaps();

            sec.Resize += (s, e) =>
            {
                int cw = sec.ClientSize.Width;
                int rowY = Dpi(HDR_H + CGAP);
                lab.Location = new Point(0, rowY + (Dpi(ROW_H) - lab.Height) / 2);
                int kx = Dpi(CTRL_X);
                keycapBtn.Location = new Point(kx, rowY + (Dpi(ROW_H) - keycapBtn.Height) / 2);
                keycapBtn.Width = Math.Max(Dpi(100), cw - kx);
                // Share the section title row; hints need no separate column.
                int hx = Dpi(64);
                hint.Bounds = new Rectangle(hx, Dpi(2), Math.Max(20, cw - hx), nameFont.Height);
                for (int i = 0; i < 3; i++)
                {
                    int y = rowY + Dpi(ROW_H * (i + 1));
                    historyLabels[i].Location = new Point(0, y + (Dpi(ROW_H) - historyLabels[i].Height) / 2);
                    historyKeyButtons[keys[i]].Bounds = new Rectangle(kx, y + (Dpi(ROW_H) - Dpi(24)) / 2, keycapBtn.Width, Dpi(24));
                }
            };
        }

        private void BuildStorage()
        {
            var sec = NewSection("存储", HDR_H + CGAP + ROW_H * 2 + GAP);

            var lab1 = MakeRowLabel(sec, "笔记");
            var lab2 = MakeRowLabel(sec, "图片");
            var field3 = MakePathField(sec, textBox3);
            var field4 = MakePathField(sec, textBox4);
            browse1 = MakeBrowse(sec, textBox3);
            browse2 = MakeBrowse(sec, textBox4);
            browse1.Parent = field3;
            browse2.Parent = field4;
            browse1.FlatAppearance.BorderSize = browse2.FlatAppearance.BorderSize = 0;

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
            var box = new Panel { BackColor = FieldBg, Height = Dpi(24) };
            box.Paint += (s, e) =>
            {
                using (var pen = new Pen(FieldLine))
                    e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
            };
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = FieldBg;
            tb.ForeColor = TextC;
            tb.Font = smallFont;
            tb.Dock = DockStyle.Fill;
            box.Padding = new Padding(Dpi(7), Dpi(3), Dpi(BROWSE_W + 4), Dpi(3));
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
                Text = "…",
                AccessibleName = "选择文件夹",
                FlatStyle = FlatStyle.Flat,
                Font = smallFont,
                Size = new Size(Dpi(BROWSE_W), Dpi(24)),
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
            path.Width = Math.Max(Dpi(100), cw - px);
            browse.Bounds = new Rectangle(path.Width - Dpi(BROWSE_W) - 1, 1, Dpi(BROWSE_W), path.Height - 2);
            browse.BringToFront();
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
            button6.Font = labelFont;
            button6.Size = new Size(Dpi(86), Dpi(28));
            StylePrimary(button7);
            button7.Font = labelFont;
            button7.Size = new Size(Dpi(86), Dpi(28));
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
            if (rdPopup != null) rdPopup.Accent = accent;
            if (rdSound != null) rdSound.Accent = accent;
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
        private string historyCapturing;
        private readonly Dictionary<string, string> historyKeyValues = new Dictionary<string, string>();
        private readonly Dictionary<string, Button> historyKeyButtons = new Dictionary<string, Button>();

        private void UpdateHistoryKeycaps()
        {
            foreach (var pair in historyKeyButtons)
                pair.Value.Text = historyCapturing == pair.Key ? "Ctrl + …" :
                    (historyKeyValues[pair.Key] == "None" ? "已禁用" : "Ctrl + " + historyKeyValues[pair.Key].Replace("Left", "←").Replace("Right", "→"));
            if (hintLabel != null && !capturing) hintLabel.Text = historyCapturing == null ? "点击键位修改 · Ctrl 固定" : "按一个键 · 退格禁用 · Esc 取消";
        }

        private void UpdateKeycap()
        {
            if (keycapBtn == null) return;
            string key = string.IsNullOrWhiteSpace(textBox1.Text) ? "—" : textBox1.Text.Trim();
            keycapBtn.Text = capturing ? "Ctrl  +  …" : key == "None" ? "已禁用" : "Ctrl  +  " + key;
            keycapBtn.ForeColor = capturing ? MutedC : TextC;
            if (hintLabel != null)
                hintLabel.Text = capturing ? "按一个键 · 退格禁用 · Esc 取消" : "点击键位修改 · Ctrl 固定";
        }

        private void BeginCapture()
        {
            Program.AppContext?.SetShortcutCapture(true);
            historyCapturing = null;
            UpdateHistoryKeycaps();
            capturing = true;
            UpdateKeycap();
            this.Focus();
        }

        private void ConfigForm_KeyDown(object sender, KeyEventArgs e)
        {
            try { CaptureShortcutKey(e); }
            finally
            {
                if (!capturing && historyCapturing == null) Program.AppContext?.SetShortcutCapture(false);
            }
        }

        // Navigation keys are dialog keys: capture before buttons use them to move focus.
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (capturing || historyCapturing != null)
            {
                ConfigForm_KeyDown(this, new KeyEventArgs(keyData));
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private void CancelShortcutCapture()
        {
            if (!capturing && historyCapturing == null) return;
            capturing = false;
            historyCapturing = null;
            UpdateKeycap();
            UpdateHistoryKeycaps();
            Program.AppContext?.SetShortcutCapture(false);
        }

        private void CaptureShortcutKey(KeyEventArgs e)
        {
            if (historyCapturing != null)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (IsModifierKey(e.KeyCode)) return;
                if (e.KeyCode != Keys.Escape)
                {
                    string value = e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete ? "None" : e.KeyCode.ToString();
                    string conflict = ShortcutConflict(value, historyCapturing);
                    if (conflict != null) { hintLabel.Text = "与“" + conflict + "”重复，请换一个键"; return; }
                    historyKeyValues[historyCapturing] = value;
                }
                historyCapturing = null; UpdateHistoryKeycaps(); return;
            }
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
                textBox1.Text = "None";
            }
            else
            {
                string conflict = ShortcutConflict(e.KeyCode.ToString(), "shortcutKey");
                if (conflict != null) { hintLabel.Text = "与“" + conflict + "”重复，请换一个键"; return; }
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

        private string ShortcutConflict(string value, string except)
        {
            if (!Enum.TryParse(value, true, out Keys candidate) || candidate == Keys.None) return null;
            if (except != "shortcutKey" && Enum.TryParse(textBox1.Text, true, out Keys capture) && capture == candidate) return "快速截屏";
            foreach (var pair in historyKeyValues)
                if (pair.Key != except && Enum.TryParse(pair.Value, true, out Keys other) && other == candidate)
                    return pair.Key == "recallKey" ? "唤回贴图" : pair.Key == "previousImageKey" ? "上一张" : "下一张";
            return null;
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
            SetCheck(checkBox1, InitConfig.GetConfigValue("checkBox1"));
            SetCheck(checkBox2, InitConfig.GetConfigValue("checkBox2"));
            SetCheck(checkBox3, InitConfig.GetConfigValue("checkBox3"));
            textBox1.Text = InitConfig.GetConfigValue("shortcutKey") ?? "";
            textBox3.Text = InitConfig.GetConfigValue("notesPath") ?? "";
            textBox4.Text = InitConfig.GetConfigValue("imagesPath") ?? "";
            SetRemindMode(InitConfig.GetConfigValue("remindMode"));
        }

        // 键缺失或值不规范时保持控件当前状态，不静默改掉用户配置
        private static void SetCheck(CheckBox cb, string value)
        {
            bool b;
            if (bool.TryParse(value, out b)) cb.Checked = b;
        }

        // 待办提醒方式：0=仅弹窗，1=弹窗+铃声；两钮互斥且必选一个
        private void SetRemindMode(string value)
        {
            bool sound = value == "1";
            rdSound.Checked = sound;
            rdPopup.Checked = !sound;
        }

        private void SaveData()
        {
            if (ShortcutConflict(textBox1.Text, "shortcutKey") != null || historyKeyValues.Any(p => ShortcutConflict(p.Value, p.Key) != null))
                throw new InvalidOperationException("快捷键重复，请为四项操作选择不同的键位。");
            SaveOpenData();
            if (string.IsNullOrWhiteSpace(textBox3.Text) || string.IsNullOrWhiteSpace(textBox4.Text))
                throw new IOException("请选择有效的笔记和截图目录。");
            Directory.CreateDirectory(textBox4.Text);
            Storage.CopyNotes(InitConfig.GetConfigValue("notesPath"), textBox3.Text);
            InitConfig.SetConfigValues(new Dictionary<string, string>
            {
                { "checkBox1", checkBox1.Checked.ToString() },
                { "checkBox2", checkBox2.Checked.ToString() },
                { "checkBox3", checkBox3.Checked.ToString() },
                { "shortcutKey", textBox1.Text },
                { "recallKey", historyKeyValues["recallKey"] },
                { "previousImageKey", historyKeyValues["previousImageKey"] },
                { "nextImageKey", historyKeyValues["nextImageKey"] },
                { "pasteShortcutVersion", "3" },
                { "notesPath", textBox3.Text },
                { "imagesPath", textBox4.Text },
                { "remindMode", rdSound.Checked ? "1" : "0" },
            });
        }

        private bool exiting;

        private void button7_Click(object sender, EventArgs e)
        {
            if (exiting) return;
            exiting = true;
            try
            {
                ApplyWithRestart(SaveData, MyApplicationContext.RestartApplication);
                ExitAfterApplying();
            }
            catch (Exception ex)
            {
                exiting = false;
                MessageBox.Show(this, ex.Message, "配置未应用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            if (exiting) return;
            exiting = true;
            try
            {
                ApplyWithRestart(ResetData, MyApplicationContext.RestartApplication);
                ExitAfterApplying();
            }
            catch (Exception ex)
            {
                exiting = false;
                MessageBox.Show(this, ex.Message, "配置未应用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ResetData()
        {
            SaveOpenData();
            var defaults = InitConfig.DefaultValues();
            Storage.CopyNotes(InitConfig.GetConfigValue("notesPath"), defaults["notesPath"]);
            Directory.CreateDirectory(defaults["imagesPath"]);
            InitConfig.SetConfigValues(defaults);
        }

        private static void SaveOpenData()
        {
            foreach (var opened in Application.OpenForms.OfType<note>().ToArray())
                if (!opened.SaveCurrentNoteSafe()) throw new IOException("请先解决便签保存失败的问题，再应用配置。");
            if (!TodoStore.TryFlush()) throw new IOException("待办尚未保存成功，请检查数据目录后重试。");
        }

        internal static void ApplyWithRestart(Action save, Action restart)
        {
            var previous = InitConfig.Snapshot();
            try
            {
                save();
                restart();
            }
            catch (Exception failure)
            {
                // Existing note windows keep their original directory until the
                // process exits. A failed launch must keep new windows there too.
                try { InitConfig.Restore(previous); }
                catch (Exception rollbackFailure)
                {
                    throw new IOException("新实例启动失败，原配置也未能恢复，请检查配置目录是否可写。",
                        new AggregateException(failure, rollbackFailure));
                }
                throw;
            }
        }

        // 重启的握手空窗没有窗口可显示，这里只保证旧窗口关闭前，状态真被人看见
        private void ExitAfterApplying()
        {
            Cursor = Cursors.WaitCursor;
            button6.Enabled = false;
            // 与「保存配置」同字号同样式，只差四个字；重复点由 exiting 拦，不靠禁用变灰
            button7.Text = "正在应用";
            if (chromeBar.Tag is Label title) title.Text = "正在应用";
            Refresh();


            var t = new System.Windows.Forms.Timer { Interval = 500 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                Program.AppContext.ExitApplication();
            };
            t.Start();
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
