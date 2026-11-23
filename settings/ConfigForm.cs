using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes.settings
{
    public partial class ConfigForm : AppWindow
    {
        private Panel chromeBar;
        private Panel bodyPanel;
        private Panel footBar;
        private NoteTheme currentTheme;

        private CheckBox taskbarCheckBox;
        private RadioControl rdPopup, rdSound;
        private Button keycapBtn, browse1, browse2;
        private Label hintLabel;
        private LinkLabel aboutSite;

        // 视觉常量（96dpi 基准，运行时按 Dpi() 缩放）
        private static readonly Color TextC = Color.FromArgb(43, 47, 54);
        private static readonly Color MutedC = Color.FromArgb(138, 146, 158);
        private static readonly Color LineC = Color.FromArgb(236, 239, 243);
        private static readonly Color FieldBg = Color.FromArgb(245, 247, 250);
        private static readonly Color FieldLine = Color.FromArgb(223, 228, 234);
        private Font labelFont, smallFont;

        private const int GAP = 4;
        private const int ROW_H = 24;
        private const int HINT_H = 18;
        private const string ShortcutCaptureHint = "按一个键 · 退格禁用 · Esc 取消";
        private const int FIELD_H = 20;
        private const int KEY_WIDTH = 112;
        private const int CTRL_X = 56;
        private const int COLGAP = 10;
        private const int BROWSE_W = 28;

        public ConfigForm()
        {
            InitializeComponent();
            currentTheme = NoteTheme.Current();
            this.Padding = new Padding(Dpi(5));
            chromeBar = FormChrome.ApplyStandardHeader(this, "设置", currentTheme);
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
            UpdateKeycap();
            RestyleTheme(currentTheme);
        }

        // ===== 布局构建 =====
        private void BuildLayout()
        {
            labelFont = new Font("Microsoft YaHei UI", 8.5f);
            smallFont = new Font("Microsoft YaHei UI", 8f);

            bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.White,
                Padding = new Padding(Dpi(12), Dpi(6), Dpi(12), Dpi(4))
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

            Size defaultSize = CompactLayout.DefaultNoteSize(UiDpi.Factor, Screen.PrimaryScreen.WorkingArea.Size);
            this.ClientSize = defaultSize;
            this.MinimumSize = new Size(defaultSize.Width, Math.Min(Dpi(360), defaultSize.Height));
        }

        private Panel AddRows(int height)
        {
            var sec = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dpi(height),
                BackColor = Color.White
            };
            bodyPanel.Controls.Add(sec);
            sec.BringToFront();
            return sec;
        }

        private void AddAccountStatus(Panel section)
        {
            var avatar = new AccountAvatar { Name = "settings-avatar", Size = new Size(Dpi(44), Dpi(44)) };
            var status = new LinkLabel
            {
                Name = "settings-account", AutoSize = true, Font = labelFont,
                BackColor = Color.White, LinkColor = Accent, ActiveLinkColor = Accent,
                VisitedLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline,
                AccessibleDescription = "打开账号窗口", Cursor = Cursors.Hand
            };
            section.Controls.Add(avatar);
            section.Controls.Add(status);
            status.LinkClicked += (s, e) => Program.AppContext?.ShowAccount();
            avatar.Cursor = Cursors.Hand;
            avatar.Click += (s, e) => Program.AppContext?.ShowAccount();

            void PlaceAccountStatus()
            {
                int center = section.ClientSize.Width - Dpi(KEY_WIDTH) / 2;
                avatar.Location = new Point(center - avatar.Width / 2, Dpi(2));
                status.Location = new Point(center - status.Width / 2, avatar.Bottom + Dpi(3));
            }
            section.Resize += (s, e) => PlaceAccountStatus();

            var sync = CloudSync.Current;
            void RefreshAccountStatus()
            {
                if (IsDisposed || Disposing) return;
                if (IsHandleCreated && InvokeRequired)
                {
                    BeginInvoke((Action)RefreshAccountStatus);
                    return;
                }

                bool signedIn = sync?.Session != null && !sync.NeedsLogin;
                avatar.SetIdentity(sync?.Session, signedIn);
                string text = signedIn ? "已登录" : "未登录";
                if (status.Text == text) return;
                status.Text = text;
                status.AccessibleName = text + "，打开账号窗口";
                PlaceAccountStatus();
            }
            RefreshAccountStatus();
            if (sync != null)
            {
                sync.Changed += RefreshAccountStatus;
                Disposed += (s, e) => sync.Changed -= RefreshAccountStatus;
            }
        }


        private void BuildGeneral()
        {
            var sec = AddRows(ROW_H * 4 + GAP);
            AddAccountStatus(sec);
            taskbarCheckBox = new CheckBox { Text = "显示任务栏" };
            var options = new[] { checkBox1, checkBox3, taskbarCheckBox };
            for (int index = 0; index < options.Length; index++)
            {
                var option = options[index];
                option.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                option.AutoSize = true;
                option.Font = labelFont;
                option.ForeColor = TextC;
                option.AccessibleName = option.Text;
                option.TabIndex = index;
                option.UseVisualStyleBackColor = true;
                sec.Controls.Add(option);
            }

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

            Action layoutGeneral = () =>
            {
                for (int index = 0; index < options.Length; index++)
                {
                    var option = options[index];
                    option.Location = new Point(0, Dpi(index * ROW_H) + (Dpi(ROW_H) - option.Height) / 2);
                }

                int y2 = Dpi(options.Length * ROW_H);
                labRemind.Location = new Point(0, y2 + (Dpi(ROW_H) - labRemind.Height) / 2);
                int rx = Math.Max(Dpi(CTRL_X), labRemind.Right + Dpi(8));
                rdPopup.Location = new Point(rx, y2 + (Dpi(ROW_H) - rdPopup.Height) / 2);
                rdSound.Location = new Point(rdPopup.Right + Dpi(COLGAP), y2 + (Dpi(ROW_H) - rdSound.Height) / 2);
            };
            sec.Resize += (s, e) => layoutGeneral();
            rdPopup.SizeChanged += (s, e) => layoutGeneral();
            rdSound.SizeChanged += (s, e) => layoutGeneral();
            layoutGeneral();
        }

        private void BuildHotkey()
        {
            const int rowHeight = ROW_H * 4 + GAP;
            var sec = AddRows(rowHeight);

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
                Size = new Size(Dpi(KEY_WIDTH), Dpi(FIELD_H)),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            keycapBtn.FlatAppearance.BorderColor = FieldLine;
            keycapBtn.FlatAppearance.BorderSize = 1;
            keycapBtn.Click += (s, e) => BeginCapture();
            sec.Controls.Add(keycapBtn);

            var hint = new Label
            {
                AutoSize = false,
                Visible = false,
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
                    Size = new Size(Dpi(KEY_WIDTH), Dpi(FIELD_H)), Cursor = Cursors.Hand };
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
                int rowY = hint.Text.Length == 0 ? 0 : Dpi(HINT_H);
                lab.Location = new Point(0, rowY + (Dpi(ROW_H) - lab.Height) / 2);
                int labelsWidth = Math.Max(lab.Width, historyLabels.Max(label => label.Width));
                int kx = Math.Max(labelsWidth + Dpi(10), cw - Dpi(KEY_WIDTH));
                keycapBtn.Location = new Point(kx, rowY + (Dpi(ROW_H) - keycapBtn.Height) / 2);
                keycapBtn.Width = Math.Max(1, cw - kx);
                hint.SetBounds(0, 0, cw, rowY);
                for (int i = 0; i < 3; i++)
                {
                    int y = rowY + Dpi(ROW_H * (i + 1));
                    historyLabels[i].Location = new Point(0, y + (Dpi(ROW_H) - historyLabels[i].Height) / 2);
                    historyKeyButtons[keys[i]].SetBounds(kx, y + (Dpi(ROW_H) - keycapBtn.Height) / 2,
                        keycapBtn.Width, keycapBtn.Height);
                }
            };
            hint.TextChanged += (s, e) =>
            {
                bool show = hint.Text.Length > 0;
                hint.Visible = show;
                sec.Height = Dpi(rowHeight) + (show ? Dpi(HINT_H) : 0);
            };
        }

        private void BuildStorage()
        {
            var sec = AddRows(ROW_H * 2 + GAP);

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
                LayoutPathRow(lab1, field3, browse1, cw, 0);
                LayoutPathRow(lab2, field4, browse2, cw, Dpi(ROW_H));
            };
        }

        // 路径字段：外框提供上下留白与描边，内嵌无边框文本框避免文字贴边
        private Panel MakePathField(Panel parent, TextBox tb)
        {
            var box = new Panel { BackColor = FieldBg, Height = Dpi(FIELD_H) };
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
            box.Padding = new Padding(Dpi(7), Dpi(2), Dpi(BROWSE_W + 4), Dpi(2));
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
                Size = new Size(Dpi(BROWSE_W), Dpi(FIELD_H)),
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
            int px = Math.Max(Dpi(42), lab.Width + Dpi(8));
            path.Location = new Point(px, rowY + (Dpi(ROW_H) - path.Height) / 2);
            path.Width = Math.Max(Dpi(100), cw - px);
            browse.Bounds = new Rectangle(path.Width - Dpi(BROWSE_W) - 1, 1, Dpi(BROWSE_W), path.Height - 2);
            browse.BringToFront();
        }

        private void BuildAbout()
        {
            var sec = AddRows(ROW_H + GAP);
            var version = new Label { AutoSize = true, Font = smallFont, ForeColor = MutedC, Text = "版本 " + Application.ProductVersion };
            aboutSite = new LinkLabel { AutoSize = true, Font = smallFont, Text = "访问官网",
                LinkColor = Accent, ActiveLinkColor = Accent, VisitedLinkColor = Accent,
                LinkBehavior = LinkBehavior.HoverUnderline, Cursor = Cursors.Hand,
                AccessibleName = "访问小羊便签官网" };
            aboutSite.Links.Add(0, aboutSite.Text.Length, UpdateNotice.DownloadPage);
            aboutSite.LinkClicked += (s, e) => {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true }); }
                catch (Exception) { MessageBox.Show("无法打开浏览器，请访问 note.kimlulu.com。", "小羊便签", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            sec.Controls.Add(version);
            sec.Controls.Add(aboutSite);
            sec.Resize += (s, e) =>
            {
                int mid = Dpi(ROW_H) / 2;
                version.Location = new Point(0, mid - version.Height / 2);
                aboutSite.Location = new Point(sec.ClientSize.Width - aboutSite.Width, mid - aboutSite.Height / 2);
            };
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
                FormChrome.ApplyHeaderTheme(chromeBar, t);
            Color accent = Accent;
            if (rdPopup != null) rdPopup.Accent = accent;
            if (rdSound != null) rdSound.Accent = accent;
            if (button7 != null) StylePrimary(button7);
            if (browse1 != null) StyleBrowse(browse1);
            if (browse2 != null) StyleBrowse(browse2);
            Invalidate(true);
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
            if (hintLabel != null && !capturing)
                hintLabel.Text = historyCapturing == null ? string.Empty : ShortcutCaptureHint;
        }

        private void UpdateKeycap()
        {
            if (keycapBtn == null) return;
            string key = string.IsNullOrWhiteSpace(textBox1.Text) ? "—" : textBox1.Text.Trim();
            keycapBtn.Text = capturing ? "Ctrl  +  …" : key == "None" ? "已禁用" : "Ctrl  +  " + key;
            keycapBtn.ForeColor = capturing ? MutedC : TextC;
            if (hintLabel != null)
                hintLabel.Text = capturing || historyCapturing != null ? ShortcutCaptureHint : string.Empty;
        }

        private void BeginCapture()
        {
            Program.AppContext?.SetShortcutCapture(true);
            historyCapturing = null;
            capturing = true;
            UpdateHistoryKeycaps();
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
            int x = screenBounds.Left + Math.Max(0, screenBounds.Width * 3 / 4 - Width);
            int y = screenBounds.Top + Math.Max(0, (screenBounds.Height - Height) / 2);
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
            taskbarCheckBox.Checked = InitConfig.ShowTaskbarIcons;
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
                { "showInTaskbar", taskbarCheckBox.Checked.ToString() },
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
