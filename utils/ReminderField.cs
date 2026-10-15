using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace KimNotes.utils
{
    // One consistent surface for date and time: editable segments plus a calendar / quick-time menu.
    internal sealed class ReminderField : UserControl
    {
        private readonly bool dateMode;
        private readonly MaskedTextBox input;
        private readonly ToolIconButton picker;
        private readonly NoteTheme theme;
        private readonly Font inputFont;
        private ToolStripDropDown popup;
        private readonly System.Collections.Generic.List<ToolStripDropDown> menus = new System.Collections.Generic.List<ToolStripDropDown>();
        private bool releasing;
        private DateTime value;
        private bool syncing, invalid;
        public event EventHandler ValueChanged;
        private float S => DeviceDpi / 96f;

        public ReminderField(bool dateMode, NoteTheme theme)
        {
            this.dateMode = dateMode;
            this.theme = theme;
            value = DateTime.Now;
            BackColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            inputFont = new Font("Microsoft YaHei UI", 9.5f);
            input = new MaskedTextBox(dateMode ? "0000-00-00" : "00\\:00")
            {
                Culture = CultureInfo.InvariantCulture,
                BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = Color.FromArgb(43, 47, 54), Font = inputFont,
                PromptChar = '_', HidePromptOnLeave = true,
                AccessibleName = dateMode ? "提醒日期，格式为年-月-日" : "提醒时间，24小时制",
                TabStop = true
            };
            picker = new ToolIconButton { IconId = dateMode ? "calendar" : "clock", Theme = theme, BackColor = input.BackColor,
                AccessibleName = dateMode ? "打开日历" : "选择时间" };
            picker.Click += (s, e) => OpenPicker();
            input.TextChanged += (s, e) =>
            {
                if (!syncing && input.MaskCompleted && TryRead(out var parsed)) Accept(parsed);
            };
            input.GotFocus += (s, e) => Invalidate();
            input.LostFocus += (s, e) => { Commit(); Invalidate(); };
            input.KeyDown += InputKeyDown;
            Controls.Add(input);
            Controls.Add(picker);
            Resize += (s, e) => LayoutInput();
            EnabledChanged += (s, e) => Invalidate();
            Height = 40;
            SyncText();
        }

        public DateTime Value
        {
            get => value;
            set
            {
                if (this.value == value) return;
                this.value = value;
                SyncText();
            }
        }

        public void SyncText()
        {
            syncing = true;
            try { input.Text = value.ToString(dateMode ? "yyyy-MM-dd" : "HH:mm", CultureInfo.InvariantCulture); invalid = false; }
            finally { syncing = false; }
            Invalidate();
        }

        private bool TryRead(out DateTime parsed)
        {
            if (!input.MaskCompleted) { parsed = default(DateTime); return false; }
            if (!DateTime.TryParseExact(input.Text, dateMode ? "yyyy-MM-dd" : "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return false;
            if (dateMode) return parsed >= DateTimePicker.MinimumDateTime && parsed <= DateTimePicker.MaximumDateTime;
            parsed = value.Date.Add(parsed.TimeOfDay);
            return true;
        }

        public bool Commit()
        {
            if (!Enabled) return true;
            if (!TryRead(out var parsed)) { invalid = true; Invalidate(); return false; }
            Accept(parsed);
            return true;
        }

        private void Accept(DateTime parsed)
        {
            bool changed = dateMode ? parsed.Date != value.Date : parsed.TimeOfDay != value.TimeOfDay;
            value = parsed;
            invalid = false;
            Invalidate();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
            {
                e.SuppressKeyPress = true;
                OpenPicker();
                return;
            }
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
            int direction = e.KeyCode == Keys.Up ? 1 : -1;
            int start = input.SelectionStart;
            DateTime next = value;
            try
            {
                next = dateMode ? (start < 4 ? value.AddYears(direction) : start < 7 ? value.AddMonths(direction) : value.AddDays(direction))
                    : (start < 3 ? value.AddHours(direction) : value.AddMinutes(direction));
                if (dateMode && (next < DateTimePicker.MinimumDateTime || next > DateTimePicker.MaximumDateTime)) return;
                Accept(next);
                SyncText();
                input.Select(dateMode ? (start < 4 ? 0 : start < 7 ? 5 : 8) : (start < 3 ? 0 : 3), dateMode && start < 4 ? 4 : 2);
            }
            catch (ArgumentOutOfRangeException) { }
            finally { e.SuppressKeyPress = true; }
        }

        private void LayoutInput()
        {
            int buttonSize = (int)(28 * S), inset = (int)(11 * S);
            picker.Bounds = new Rectangle(Width - buttonSize - (int)(5 * S), (Height - buttonSize) / 2, buttonSize, buttonSize);
            input.SetBounds(inset, Math.Max(0, (Height - input.PreferredHeight) / 2), Math.Max(30, picker.Left - inset - (int)(3 * S)), input.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var background = Enabled ? Color.FromArgb(248, 250, 252) : Color.FromArgb(246, 247, 249);
            input.BackColor = picker.BackColor = background;
            using (var path = TodoListForm.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(5, (int)(8 * S))))
            {
                using (var brush = new SolidBrush(background)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(invalid ? Color.FromArgb(204, 83, 72) : ContainsFocus ? theme.IconHover : Color.FromArgb(228, 233, 239))) e.Graphics.DrawPath(pen, path);
            }
        }

        internal void OpenPicker()
        {
            if (!Enabled) return;
            if (popup != null) { popup.Close(); return; }
            var menu = new ToolStripDropDown { Padding = new Padding(7), BackColor = Color.White, AutoClose = true };
            menus.Add(menu);
            popup = menu;
            if (dateMode)
            {
                var calendar = new MonthCalendar { MaxSelectionCount = 1, ShowTodayCircle = false, Margin = Padding.Empty };
                calendar.SetDate(value.Date);
                calendar.DateSelected += (s, e) => { Accept(e.Start.Date); SyncText(); menu.Close(); };
                AddPickerContent(menu, calendar);
            }
            else
            {
                var times = new ListBox { BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
                    ItemHeight = (int)(30 * S), IntegralHeight = false, Size = new Size(Math.Max((int)(140 * S), Width - 14), (int)(210 * S)), Font = inputFont };
                for (int i = 0; i < 96; i++) times.Items.Add(new DateTime(2000, 1, 1).AddMinutes(i * 15).ToString("HH:mm", CultureInfo.InvariantCulture));
                times.SelectedIndex = value.Hour * 4 + value.Minute / 15;
                times.TopIndex = Math.Max(0, times.SelectedIndex - 2);
                times.DrawItem += (s, e) =>
                {
                    if (e.Index < 0) return;
                    bool selected = (e.State & DrawItemState.Selected) != 0;
                    using (var brush = new SolidBrush(selected ? Color.FromArgb(234, 241, 250) : Color.White)) e.Graphics.FillRectangle(brush, e.Bounds);
                    var bounds = Rectangle.Inflate(e.Bounds, -10, 0);
                    TextRenderer.DrawText(e.Graphics, times.Items[e.Index].ToString(), inputFont, bounds, selected ? theme.IconHover : Color.FromArgb(43, 47, 54), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                };
                Action choose = () =>
                {
                    if (times.SelectedIndex < 0) return;
                    Accept(value.Date.AddMinutes(times.SelectedIndex * 15)); SyncText(); menu.Close();
                };
                times.Click += (s, e) => choose();
                times.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; choose(); } };
                AddPickerContent(menu, times);
            }
            menu.Closed += (s, e) =>
            {
                if (popup == menu) popup = null;
                // Closed runs before WinForms finishes reparenting the dropdown handle.
                // Dispose on the next UI turn, or with the owning field during shutdown.
                if (!releasing && IsHandleCreated)
                    BeginInvoke((Action)(() => { menus.Remove(menu); menu.Dispose(); }));
            };
            menu.Show(this, new Point(0, Height + 4));
        }

        private static void AddPickerContent(ToolStripDropDown menu, Control content)
        {
            // MonthCalendar learns its native dimensions only after handle creation.
            if (content is MonthCalendar calendar) content.Size = calendar.SingleMonthSize;
            menu.Font = content.Font;
            // A ListBox's preferred size describes all 96 rows and only a narrow
            // text column. ToolStripControlHost.AutoSize would replace our viewport
            // with those dimensions, producing a very tall, clipped dropdown.
            var viewport = content.Size;
            var host = new ToolStripControlHost(content)
            {
                AutoSize = false,
                Font = content.Font,
                Size = viewport,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            menu.AutoSize = false;
            menu.Items.Add(host);
            menu.ClientSize = new Size(viewport.Width + menu.Padding.Horizontal, viewport.Height + menu.Padding.Vertical);
            if (content is MonthCalendar)
                content.SizeChanged += (s, e) =>
                {
                    // Native calendar metrics can change when the dropdown inherits
                    // the current Windows font or DPI while creating its handle.
                    host.Size = content.Size;
                    menu.ClientSize = new Size(content.Width + menu.Padding.Horizontal, content.Height + menu.Padding.Vertical);
                };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                releasing = true;
                foreach (var menu in menus.ToArray()) menu.Dispose();
                menus.Clear();
                popup = null;
            }
            base.Dispose(disposing);
            if (disposing) inputFont.Dispose();
        }
    }
}
