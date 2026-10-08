using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes.utils
{
    /// <summary>
    /// 待办录入/改时间小窗：内容框 + 提醒时间快捷预设（今天 18:00 / 明天 9:00 / 不提醒 / 自定义…）。
    /// 与三窗同族：无边框卡片 + 主题色标题条（只有关闭）。
    /// </summary>
    internal class TodoEditDialog : Form
    {
        private readonly bool timeOnly;
        private readonly NoteTheme th;
        private readonly Color accent, accentDeep, accentSoft, accentLine;
        private static readonly Color FieldBg = Color.FromArgb(245, 247, 250);
        private static readonly Color FieldLine = Color.FromArgb(223, 228, 234);
        private static readonly Color TextMain = Color.FromArgb(43, 47, 54);
        private static readonly Color Muted = Color.FromArgb(138, 146, 158);

        private Panel chromeBar;
        private TextBox txtContent;
        private Label lblContent, lblTime, lblTip;
        private Button[] presets;
        private DateTimePicker dtpDate, dtpTime;
        private Button btnOk, btnCancel;
        private int picked;   // 0=今天18:00 1=明天9:00 2=不提醒 3=自定义
        private bool syncing;
        private Font FontPreset, FontPresetOn;

        public string TodoText => txtContent != null ? txtContent.Text.Trim() : "";

        public DateTime? Due
        {
            get
            {
                switch (picked)
                {
                    case 0: return DateTime.Now.Date.AddHours(18);            // 今天 18:00
                    case 1: return DateTime.Now.Date.AddDays(1).AddHours(9);    // 明天 9:00
                    case 2: return null;                                        // 不提醒
                    default: return CustomDue();                                // 自定义
                }
            }
        }

        /// <summary>转待办 / 新增待办：内容 + 时间。</summary>
        public TodoEditDialog(string initialText, bool fromSelection) : this(initialText, fromSelection, false, null) { }

        /// <summary>只改提醒时间（卡片上的「＋提醒」）。</summary>
        public TodoEditDialog(DateTime? currentDue) : this(null, false, true, currentDue) { }

        private TodoEditDialog(string initialText, bool fromSelection, bool timeOnly, DateTime? currentDue)
        {
            this.timeOnly = timeOnly;
            th = NoteTheme.Current();
            accent = th.IconHover;
            accentDeep = ControlPaint.Dark(accent, 0.28f);
            accentSoft = Blend(accent, Color.White, 0.86f);
            accentLine = Blend(accent, Color.White, 0.55f);
            FontPreset = new Font("Microsoft YaHei UI", 9f);
            FontPresetOn = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;

            Text = timeOnly ? "设置提醒" : (fromSelection ? "转待办" : "新增待办");
            Icon = FormChrome.AppIcon;
            chromeBar = FormChrome.Apply(this, Text, false, null, th.Chrome, th.ChromeText);

            BuildBody(initialText, fromSelection, currentDue);
            ApplyPreset(currentDue.HasValue ? 3 : 0, currentDue);
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
                txtContent = new TextBox
                {
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = FieldBg,
                    ForeColor = TextMain,
                    Text = initialText ?? "",
                    TabIndex = 0
                };
                Controls.Add(lblContent);
                Controls.Add(txtContent);
            }

            lblTime = MakeFieldLabel("提醒时间");

            var names = new[] { "今天 18:00", "明天 9:00", "不提醒", "自定义…" };
            presets = new Button[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = new Button
                {
                    Text = names[i],
                    FlatStyle = FlatStyle.Flat,
                    Margin = Padding.Empty,
                    Cursor = Cursors.Hand,
                    TabStop = false,
                    Font = FontPreset,
                    Size = ChipSize(names[i], FontPreset, FontPreset.Height + (int)(10 * S))
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
            dtpDate.ValueChanged += (s, e) => { if (!syncing && picked != 3) ApplyPreset(3, null); };
            dtpTime.ValueChanged += (s, e) => { if (!syncing && picked != 3) ApplyPreset(3, null); };
            Controls.Add(dtpDate);
            Controls.Add(dtpTime);

            lblTip = new Label
            {
                AutoSize = false,
                ForeColor = Muted,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Text = timeOnly
                    ? "到点会弹一个置顶小窗提醒。"
                    : (fromSelection
                        ? "已填入便签里划选的文字，加入后原文仍留在便签里。"
                        : "没有划选内容，自己写一句就行。"),
                TabStop = false
            };
            Controls.Add(lblTip);

            btnCancel = MakeButton("取消", false);
            btnCancel.DialogResult = DialogResult.Cancel;
            btnOk = MakeButton(timeOnly ? "保存" : "加入待办", true);
            btnOk.Click += (s, e) =>
            {
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
            Font = new Font("Microsoft YaHei UI", 8.5f),
            TabStop = false
        };

        private Button MakeButton(string text, bool primary)
        {
            var font = new Font("Microsoft YaHei UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Margin = Padding.Empty,
                Cursor = Cursors.Hand,
                Font = font,
                Size = ChipSize(text, font, font.Height + (int)(12 * S), (int)(22 * S)),
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
                var v = currentDue ?? PresetDue(idx) ?? DateTime.Now.Date.AddHours(18);
                dtpDate.Value = v;
                dtpTime.Value = v;
            }
            finally { syncing = false; }
            dtpDate.Enabled = dtpTime.Enabled = idx != 2;   // 不提醒时置灰
            for (int i = 0; i < presets.Length; i++)
            {
                bool on = i == idx;
                presets[i].BackColor = on ? accentSoft : FieldBg;
                presets[i].ForeColor = on ? accentDeep : TextMain;
                presets[i].FlatAppearance.BorderColor = on ? accent : FieldLine;
                presets[i].Font = on ? FontPresetOn : FontPreset;
                presets[i].Width = ChipSize(presets[i].Text, presets[i].Font, presets[i].Height).Width;
            }
            LayoutBody();
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
            int pad = (int)(15 * S);
            int wide = (int)(372 * S);
            int x = pad;
            int y = (chromeBar != null ? chromeBar.Bottom : 0) + (int)(13 * S);

            if (!timeOnly)
            {
                lblContent.Location = new Point(x, y);
                y = lblContent.Bottom + (int)(6 * S);
                txtContent.Bounds = new Rectangle(x, y, wide, (int)(66 * S));
                y = txtContent.Bottom + (int)(14 * S);
            }

            lblTime.Location = new Point(x, y);
            y = lblTime.Bottom + (int)(7 * S);

            int px = x;
            int ph = 0;
            foreach (var b in presets)
            {
                if (px > x && px + b.Width > x + wide)
                {
                    px = x;
                    y += ph + (int)(7 * S);
                    ph = 0;
                }
                b.Location = new Point(px, y);
                px += b.Width + (int)(7 * S);
                ph = Math.Max(ph, b.Height);
            }
            y += ph + (int)(9 * S);

            dtpDate.Location = new Point(x, y);
            dtpTime.Location = new Point(dtpDate.Right + (int)(7 * S), y);
            y = Math.Max(dtpDate.Bottom, dtpTime.Bottom) + (int)(9 * S);

            lblTip.Location = new Point(x, y);
            lblTip.Size = new Size(wide, lblTip.Font.Height * 2);
            y = lblTip.Bottom + (int)(12 * S);

            int okW = btnOk.Width, cancelW = btnCancel.Width;
            btnOk.Location = new Point(x + wide - okW, y);
            btnCancel.Location = new Point(btnOk.Left - (int)(9 * S) - cancelW, y);
            y += Math.Max(btnOk.Height, btnCancel.Height) + pad;

            ClientSize = new Size(wide + pad * 2, y);
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
