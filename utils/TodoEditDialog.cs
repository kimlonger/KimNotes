using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes.utils
{
    /// <summary>
    /// 待办录入/改时间小窗：内容框 + 提醒时间快捷预设（今天 18:00 / 明天 9:00 / 不提醒 / 自定义…）。
    /// 与三窗同族：无边框卡片 + 主题色标题条（只有关闭）。
    /// </summary>
    internal class TodoEditDialog : AppWindow
    {
        private readonly bool timeOnly;
        private readonly bool editMode;
        private readonly NoteTheme th;
        private readonly Color accent, accentDeep, accentSoft, accentLine;
        private static readonly Color FieldBg = Color.FromArgb(248, 250, 252);
        private static readonly Color FieldLine = Color.FromArgb(228, 233, 239);
        private static readonly Color TextMain = Color.FromArgb(43, 47, 54);
        private static readonly Color Muted = Color.FromArgb(138, 146, 158);

        private Panel chromeBar;
        private TextBox txtContent;
        private Panel contentFrame;
        private Label lblContent, lblTime, lblTip;
        private Button[] presets;
        private DateTimePicker dtpDate, dtpTime;
        private ReminderField dateField, timeField;
        private Button repeatBox;
        private ContextMenuStrip repeatMenu;
        private RepeatRule repeatValue;
        private Label lblRepeat;
        public RepeatRule Repeat => Due.HasValue ? repeatValue : RepeatRule.None;
        public void SetRepeat(RepeatRule rule)
        {
            repeatValue = Enum.IsDefined(typeof(RepeatRule), rule) ? rule : RepeatRule.None;
            repeatBox.Text = RepeatText(repeatValue) + "  ▾";
        }
        private static string RepeatText(RepeatRule rule) => rule == RepeatRule.Weekdays ? "工作日（周一至周五）" :
            rule == RepeatRule.Weekly ? "每周（所选日期的星期）" : TodoStore.RepeatLabel(rule);
        private Label lblDate, lblClock;
        private bool layoutReady;
        private string tipText;
        private Button btnOk, btnCancel;
        private int picked;   // 0=今天18:00 1=明天9:00 2=不提醒 3=自定义
        private bool syncing;
        private Font FontPreset;
        private readonly System.Collections.Generic.List<Font> ownedFonts = new System.Collections.Generic.List<Font>();

        private Font OwnFont(Font font) { ownedFonts.Add(font); return font; }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                repeatMenu?.Dispose();
                foreach (var font in ownedFonts) font.Dispose();
                ownedFonts.Clear();
            }
        }

        public string TodoText => txtContent != null ? txtContent.Text.Trim() : "";

        public DateTime? Due
        {
            get
            {
                switch (picked)
                {
                    case 2: return null;                                        // 不提醒
                    default: return CustomDue();                                // 自定义
                }
            }
        }

        /// <summary>转待办 / 新增待办：内容 + 时间。</summary>
        public TodoEditDialog(string initialText, bool fromSelection) : this(initialText, fromSelection, false, null) { }

        /// <summary>只改提醒时间（卡片上的「＋提醒」）。</summary>
        public TodoEditDialog(DateTime? currentDue) : this(null, false, true, currentDue) { }

        /// <summary>编辑已有待办：内容 + 时间，预填当前值。</summary>
        public TodoEditDialog(string initialText, DateTime? currentDue) : this(initialText, false, false, currentDue, true) { }

        private TodoEditDialog(string initialText, bool fromSelection, bool timeOnly, DateTime? currentDue, bool editMode = false)
        {
            this.timeOnly = timeOnly;
            this.editMode = editMode;
            th = NoteTheme.Current();
            accent = th.IconHover;
            accentDeep = ControlPaint.Dark(accent, 0.28f);
            accentSoft = Blend(accent, Color.White, 0.86f);
            accentLine = Blend(accent, Color.White, 0.55f);
            FontPreset = OwnFont(new Font("Microsoft YaHei UI", 8.5f));

            BackColor = Color.White;
            Font = OwnFont(new Font("Microsoft YaHei UI", 8.5f));
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScroll = false;
            AutoScaleMode = AutoScaleMode.None;
            Padding = new Padding((int)(5 * S));
            ClientSize = new Size((int)(CompactLayout.EditorWidth * S), (int)((timeOnly ? CompactLayout.ReminderHeight : CompactLayout.EditorHeight) * S));

            Text = timeOnly ? "设置提醒" : (editMode ? "编辑待办" : (fromSelection ? "转待办" : "新增待办"));
            Icon = FormChrome.AppIcon;
            chromeBar = FormChrome.Apply(this, Text, false, null, Color.White, TextMain, null, false);

            BuildBody(initialText, fromSelection, currentDue);
            layoutReady = true;
            Resize += (s, e) => LayoutBody();
            ApplyPreset(currentDue.HasValue ? 3 : (editMode ? 2 : 0), currentDue);
            LayoutBody();
            if (txtContent != null)
            {
                txtContent.Focus();
                txtContent.Select(txtContent.TextLength, 0);
            }
        }

        private void BuildBody(string initialText, bool fromSelection, DateTime? currentDue)
        {
            if (!timeOnly)
            {
                lblContent = MakeFieldLabel("待办内容");
                contentFrame = new Panel { BackColor = Color.White };
                contentFrame.Paint += (s, e) =>
                {
                    using (var path = TodoListForm.Rounded(new Rectangle(0, 0, contentFrame.Width - 1, contentFrame.Height - 1), (int)(9 * S)))
                    using (var pen = new Pen(txtContent.ContainsFocus ? accentLine : FieldLine))
                    {
                        using (var brush = new SolidBrush(FieldBg)) e.Graphics.FillPath(brush, path);
                        e.Graphics.DrawPath(pen, path);
                    }
                };
                txtContent = new TextBox
                {
                    Multiline = true,
                    WordWrap = true,
                    AcceptsReturn = true,
                    ScrollBars = ScrollBars.None,
                    BorderStyle = BorderStyle.None,
                    Font = OwnFont(new Font("Microsoft YaHei UI", 9f)),
                    BackColor = FieldBg,
                    ForeColor = TextMain,
                    Text = (initialText ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
                    TabIndex = 0
                };
                txtContent.GotFocus += (s, e) => contentFrame.Invalidate();
                txtContent.LostFocus += (s, e) => contentFrame.Invalidate();
                Controls.Add(lblContent);
                contentFrame.Controls.Add(txtContent);
                Controls.Add(contentFrame);
            }

            lblTime = MakeFieldLabel("提醒时间");
            Controls.Add(lblTime);

            var names = new[] { "今天 18:00", "明天 9:00", "不提醒", "自定义…" };
            presets = new Button[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = new DialogButton
                {
                    Text = names[i],
                    FlatStyle = FlatStyle.Flat,
                    Margin = Padding.Empty,
                    Cursor = Cursors.Hand,
                    TabStop = true,
                    Font = FontPreset,
                    Size = ChipSize(names[i], FontPreset, (int)(34 * S))
                };
                b.FlatAppearance.BorderColor = FieldLine;
                b.Click += (s, e) => ApplyPreset(idx, null);
                presets[i] = b;
                Controls.Add(b);
            }

            dtpDate = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                Width = TextRenderer.MeasureText("2026-08-08", Font).Width + (int)(36 * S),
                Value = currentDue ?? DateTime.Now,
                TabStop = false
            };
            dtpTime = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm",
                ShowUpDown = true,
                Width = TextRenderer.MeasureText("08:08", Font).Width + (int)(30 * S),
                Value = currentDue ?? DateTime.Now,
                TabStop = false
            };
            dtpDate.Visible = dtpTime.Visible = false;
            dateField = new ReminderField(true, th);
            timeField = new ReminderField(false, th);
            lblDate = MakeFieldLabel("日期");
            lblClock = MakeFieldLabel("时间");
            Controls.AddRange(new Control[] { lblDate, lblClock, dateField, timeField });
            dateField.ValueChanged += (s, e) => dtpDate.Value = dateField.Value;
            timeField.ValueChanged += (s, e) => dtpTime.Value = timeField.Value;
            dtpDate.ValueChanged += (s, e) => { dateField.Value = dtpDate.Value; SelectCustomPreset(); };
            dtpTime.ValueChanged += (s, e) => { timeField.Value = dtpTime.Value; SelectCustomPreset(); };
            Controls.Add(dtpDate);
            Controls.Add(dtpTime);

            lblRepeat = MakeFieldLabel("重复");
            repeatBox = new DialogButton { FlatStyle = FlatStyle.Flat, Font = FontPreset, Cursor = Cursors.Hand,
                BackColor = FieldBg, ForeColor = TextMain, AccessibleName = "提醒重复方式" };
            repeatBox.FlatAppearance.BorderColor = FieldLine;
            repeatMenu = new ContextMenuStrip();
            foreach (RepeatRule rule in Enum.GetValues(typeof(RepeatRule)))
            {
                var choice = rule;
                repeatMenu.Items.Add(new ToolStripMenuItem(RepeatText(rule), null, (s, e) => SetRepeat(choice)) { Tag = rule });
            }
            repeatBox.Click += (s, e) =>
            {
                foreach (ToolStripMenuItem item in repeatMenu.Items) item.Checked = (RepeatRule)item.Tag == repeatValue;
                repeatMenu.Show(repeatBox, new Point(0, repeatBox.Height));
            };
            SetRepeat(RepeatRule.None);
            Controls.Add(lblRepeat); Controls.Add(repeatBox);

            lblTip = new Label
            {
                AutoSize = false,
                ForeColor = Muted,
                Font = OwnFont(new Font("Microsoft YaHei UI", 8f)),
                Text = timeOnly
                    ? "到点会弹一个置顶小窗提醒。"
                    : (fromSelection ? "已带入选中文字，原便签内容保留。" : "设置提醒后，到点会弹窗通知。"),
                TabStop = false
            };
            Controls.Add(lblTip);
            tipText = lblTip.Text;

            btnCancel = MakeButton("取消", false);
            btnCancel.DialogResult = DialogResult.Cancel;
            btnOk = MakeButton(timeOnly ? "保存" : (editMode ? "保存" : "加入待办"), true);
            btnOk.Click += (s, e) =>
            {
                if (picked != 2 && (!dateField.Commit() || !timeField.Commit()))
                {
                    lblTip.Text = "请填写有效日期和时间（年-月-日 / 时:分）。";
                    lblTip.ForeColor = Color.FromArgb(204, 83, 72);
                    DialogResult = DialogResult.None;
                    return;
                }
                if (!timeOnly && string.IsNullOrWhiteSpace(txtContent.Text))
                {
                    MessageBox.Show("请输入待办内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    DialogResult = DialogResult.None;
                    txtContent.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            Controls.Add(btnCancel);
            Controls.Add(btnOk);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private Label MakeFieldLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Muted,
            Font = OwnFont(new Font("Microsoft YaHei UI", 8f)),
            TabStop = false
        };

        private Button MakeButton(string text, bool primary)
        {
            var font = OwnFont(new Font("Microsoft YaHei UI", 8.5f, primary ? FontStyle.Bold : FontStyle.Regular));
            var b = new DialogButton
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Margin = Padding.Empty,
                Cursor = Cursors.Hand,
                Font = font,
                Size = ChipSize(text, font, (int)(32 * S), (int)(18 * S)),
                BackColor = primary ? accent : Color.White,
                ForeColor = primary ? Color.White : TextMain
            };
            b.FlatAppearance.BorderColor = primary ? accent : FieldLine;
            return b;
        }

        // 按钮尺寸按文字实测，避免 AutoSize 在布局前取不到宽度
        private Size ChipSize(string text, Font font, int height, int padH = 0)
        {
            if (padH == 0) padH = (int)(11 * S);
            int w = TextRenderer.MeasureText(text, font).Width + padH * 2;
            return new Size(w, height);
        }

        private void ApplyPreset(int idx, DateTime? currentDue)
        {
            picked = idx;
            // 日期/时间控件常驻并同步显示当前生效时间；程序回填值时抑制 ValueChanged 回跳
            syncing = true;
            try
            {
                var v = currentDue ?? PresetDue(idx) ?? CustomDue() ?? DateTime.Now.Date.AddHours(18);
                dtpDate.Value = v;
                dtpTime.Value = v;
                dateField.Value = timeField.Value = v;
                dateField.SyncText();
                timeField.SyncText();
            }
            finally { syncing = false; }
            dtpDate.Enabled = dtpTime.Enabled = idx != 2;   // 不提醒时置灰
            dateField.Enabled = timeField.Enabled = idx != 2;
            repeatBox.Enabled = idx != 2;
            if (lblTip != null) { lblTip.Text = tipText; lblTip.ForeColor = Muted; }
            UpdatePresetButtons();
            LayoutBody();
        }

        private void SelectCustomPreset()
        {
            if (syncing || picked == 3) return;
            // Changing one field must not rewrite the other field's unfinished input.
            picked = 3;
            UpdatePresetButtons();
        }

        private void UpdatePresetButtons()
        {
            for (int i = 0; i < presets.Length; i++)
            {
                bool on = i == picked;
                presets[i].BackColor = on ? accentSoft : FieldBg;
                presets[i].ForeColor = on ? accentDeep : TextMain;
                presets[i].FlatAppearance.BorderColor = on ? accent : FieldLine;
                presets[i].Font = FontPreset;
            }
        }

        private DateTime? PresetDue(int idx)
        {
            switch (idx)
            {
                case 0: return DateTime.Now.Date.AddHours(18);            // 今天 18:00
                case 1: return DateTime.Now.Date.AddDays(1).AddHours(9);   // 明天 9:00
                default: return null;
            }
        }

        private DateTime? CustomDue()
        {
            var d = dtpDate.Value.Date;
            var t = dtpTime.Value;
            return d.Add(new TimeSpan(t.Hour, t.Minute, 0));
        }

        private float S => UiDpi.Factor;

        // 无边框小窗保留柔和投影（卡片感）
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        private void LayoutBody()
        {
            if (!layoutReady) return;
            int pad = (int)(18 * S);
            int wide = ClientSize.Width - pad * 2;
            int x = pad;
            int y = (chromeBar != null ? chromeBar.Bottom : 0) + (int)(10 * S);

            if (!timeOnly)
            {
                lblContent.Location = new Point(x, y);
                y = lblContent.Bottom + (int)(4 * S);
                contentFrame.Bounds = new Rectangle(x, y, wide, (int)(72 * S));
                txtContent.Bounds = new Rectangle((int)(10 * S), (int)(8 * S), wide - (int)(20 * S), (int)(56 * S));
                y = contentFrame.Bottom + (int)(10 * S);
            }

            lblTime.Location = new Point(x, y);
            y = lblTime.Bottom + (int)(5 * S);

            int gap = (int)(6 * S), ph = (int)(30 * S);
            for (int i = 0; i < presets.Length; i++)
            {
                presets[i].Bounds = new Rectangle(x + (i % 2) * ((wide - gap) / 2 + gap), y + (i / 2) * (ph + gap), (wide - gap) / 2, ph);
            }
            y += ph * 2 + gap + (int)(10 * S);

            int fieldGap = (int)(12 * S), fieldWidth = (wide - fieldGap) / 2;
            lblDate.Location = new Point(x, y);
            lblClock.Location = new Point(x + fieldWidth + fieldGap, y);
            y += lblDate.Height + (int)(4 * S);
            dateField.Bounds = new Rectangle(x, y, fieldWidth, (int)(32 * S));
            timeField.Bounds = new Rectangle(x + fieldWidth + fieldGap, y, fieldWidth, (int)(32 * S));
            y = dateField.Bottom + (int)(6 * S);

            lblRepeat.Location = new Point(x, y + (int)(7 * S));
            repeatBox.Bounds = new Rectangle(x + (int)(38 * S), y, wide - (int)(38 * S), (int)(28 * S));
            y = repeatBox.Bottom + (int)(6 * S);

            lblTip.Location = new Point(x, y);
            lblTip.Size = new Size(wide, lblTip.Font.Height * 2);
            y = lblTip.Bottom + (int)(10 * S);

            int minimumHeight = y + btnOk.Height + pad;
            MinimumSize = new Size((int)(CompactLayout.EditorWidth * S), minimumHeight);
            y = Math.Max(y, ClientSize.Height - pad - btnOk.Height);
            btnCancel.Width = (int)(88 * S);
            btnOk.Width = (int)(104 * S);
            int groupWidth = btnCancel.Width + (int)(12 * S) + btnOk.Width;
            btnCancel.Location = new Point((ClientSize.Width - groupWidth) / 2, y);
            btnOk.Location = new Point(btnCancel.Right + (int)(12 * S), y);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x84) return;
            long lp = m.LParam.ToInt64();
            var point = new Point(unchecked((short)(lp & 0xffff)), unchecked((short)((lp >> 16) & 0xffff)));
            int hit = FormChrome.HitTest(this, PointToClient(point), Math.Max(Padding.Left, 4));
            if (hit != 0) m.Result = (IntPtr)hit;
        }

        private sealed class DialogButton : Button
        {
            private bool hover;
            public DialogButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                MouseEnter += (s, e) => { hover = true; Invalidate(); };
                MouseLeave += (s, e) => { hover = false; Invalidate(); };
            }
            protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? Color.White);
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = TodoListForm.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(5, Height / 5)))
                {
                    using (var brush = new SolidBrush(hover ? ControlPaint.Dark(BackColor, 0.025f) : BackColor)) e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(FlatAppearance.BorderColor)) e.Graphics.DrawPath(pen, path);
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ForeColor, BackColor);
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
