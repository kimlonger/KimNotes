using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    /// <summary>
    /// 待办清单：全局单例窗口，通过卡片上的按钮操作待办。
    /// 默认固定置顶，使用统一标题栏；上方是搜索框和新增按钮。
    /// </summary>
    public class TodoListForm : AppWindow
    {
        private float s;
        private NoteTheme th;
        private Color accent, accentDeep, accentSoft, accentLine;
        private Color cardBg, cardLine, mutedFg, hair, doneChipBg, emptyBg, chipFg;
        private static readonly Color WarnBg = Color.FromArgb(251, 227, 224);
        private static readonly Color WarnLine = Color.FromArgb(238, 198, 192);
        private static readonly Color WarnFg = Color.FromArgb(192, 57, 43);
        private static readonly Color FieldIdle = Color.FromArgb(244, 246, 249);
        private static readonly Color FieldLine = Color.FromArgb(227, 231, 236);

        private Panel chromeBar, topPanel, body, content;
        private ToolIconButton pinBtn, addBtn;
        private SearchBox searchBox;
        private GroupHeader hdrActive, hdrDone;
        // 字号用单倍缩放磅值（系统按 DPI 自缩放），控件高度一律按字体实测高度推，避免字被裁
        private Font fCard, fChip, fHdr, fCnt, fEmpty;
        private readonly List<Control> rows = new List<Control>();
        private readonly List<TodoCard> cards = new List<TodoCard>();
        private readonly Timer searchDebounce = new Timer { Interval = 120 };
        private readonly ToolTip actionTips = new ToolTip();
        private readonly Timer flashTimer = new Timer { Interval = 900 };
        private readonly Timer statusTimer = new Timer { Interval = 30000 };
        private bool resourcesDisposed;
        private int flashId = -1;
        private int scrollOffset;
        private string keyword = "";

        public TodoListForm()
        {
            s = UiDpi.Factor;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9f);
            Padding = new Padding((int)(5 * s));
            TopMost = true;                       // 默认固定置顶
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size defaultSize = CompactLayout.DefaultNoteSize(s, wa.Size);
            ClientSize = defaultSize;
            MinimumSize = new Size(defaultSize.Width, Math.Min(Dpi(360), defaultSize.Height));

            ApplyPalette(NoteTheme.Current());

            fCard = new Font("Microsoft YaHei UI", 10f);
            fChip = new Font("Microsoft YaHei UI", 8.5f);
            fHdr = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            fCnt = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Bold);
            fEmpty = new Font("Microsoft YaHei UI", 9f);

            pinBtn = new ToolIconButton { IconId = "pin", Size = new Size(34, 34), Selected = true, Theme = th };
            pinBtn.Click += (sd, e) => SetTopMost(!TopMost);
            Text = "待办清单";
            Icon = FormChrome.AppIcon;
            chromeBar = FormChrome.ApplyStandardHeader(this, "待办清单", th, pinBtn);

            Location = new Point(Math.Max(wa.Left, wa.Right - Width - Dpi(24)), wa.Top + Math.Max(0, (wa.Height - Height) / 2));

            BuildBody();

            searchDebounce.Tick += (sd, e) => { searchDebounce.Stop(); keyword = searchBox.Text.Trim(); Render(); };
            flashTimer.Tick += (sd, e) => { flashTimer.Stop(); flashId = -1; Invalidate(true); };

            TodoStore.Changed += OnStoreChanged;
            FormClosed += (sd, e) => TodoStore.Changed -= OnStoreChanged;
            statusTimer.Tick += (sd, e) => Render();
            statusTimer.Start();
            Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                TodoStore.Changed -= OnStoreChanged;
                searchDebounce.Dispose();
                flashTimer.Dispose();
                statusTimer.Dispose();
                base.Dispose(disposing);
                actionTips.Dispose();
                foreach (var font in new[] { fCard, fChip, fHdr, fCnt, fEmpty, Font }) font?.Dispose();
                return;
            }
            base.Dispose(disposing);
        }

        private void OnStoreChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            // 卡片事件里改数据会触发刷新，延后一拍避免边遍历边销毁控件
            BeginInvoke((Action)(() => { if (!IsDisposed) Render(); }));
        }

        private int Dpi(int v) => (int)Math.Round(v * s);
        private float DpiF(float v) => v * s;

        private bool dpiLayoutQueued;
        private int dpiScrollBeforeChange;
        private float dpiScaleBeforeChange;

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            if (!IsHandleCreated || IsDisposed || dpiLayoutQueued) return;
            dpiLayoutQueued = true;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    dpiLayoutQueued = false;
                    if (IsDisposed || Disposing || body == null || content == null) return;
                    float oldScale = dpiScaleBeforeChange > 0 ? dpiScaleBeforeChange : (s > 0 ? s : 1f);
                    int oldScroll = dpiScrollBeforeChange;
                    dpiScaleBeforeChange = 0;
                    s = DeviceDpi / 96f;
                    Padding = new Padding(Dpi(5));
                    Size defaultSize = CompactLayout.DefaultNoteSize(s, Screen.FromControl(this).WorkingArea.Size);
                    MinimumSize = new Size(defaultSize.Width, Math.Min(Dpi(360), defaultSize.Height));
                    addBtn.IconScaleOverride = s;
                    hdrActive.Height = hdrDone.Height = fHdr.Height + Dpi(8);
                    // Existing cards already know how to reflow at a new width.
                    // Keep their controls instead of recreating every native text box.
                    foreach (var row in rows)
                    {
                        if (row is TodoCard card) card.PrepareDpiLayout();
                        else if (row is EmptyBox) row.Height = Dpi(64);
                    }
                    AdjustLayout();
                    SetScrollOffset((int)Math.Round(oldScroll * s / oldScale));
                    Invalidate(true);
                }));
            }
            catch (InvalidOperationException) { dpiLayoutQueued = false; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = FormChrome.WithShadow(base.CreateParams);
                return cp;
            }
        }

        // 无边框边缘拉伸
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0 && !IsPixelLayoutLocked && body != null && !IsDisposed) // WM_DPICHANGED
            {
                if (!dpiLayoutQueued)
                {
                    dpiScrollBeforeChange = scrollOffset;
                    dpiScaleBeforeChange = s;
                }
                // The old monitor's minimum is in old pixels. Let WinForms
                // apply the suggested bounds before restoring the new minimum.
                MinimumSize = Size.Empty;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x84)
            {
                int lp = m.LParam.ToInt32();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                int code = FormChrome.HitTest(this, PointToClient(new Point(x, y)), Math.Max(Padding.Left, 4));
                if (code != 0) m.Result = (IntPtr)code;
            }
        }

        // 顶部命中环涂成标题栏同色，无缝覆盖
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int band = Padding.Top + (chromeBar != null ? chromeBar.Height : 0);
            if (band > 0)
            {
                using (var b = new SolidBrush(chromeBar != null ? chromeBar.BackColor : BackColor))
                    e.Graphics.FillRectangle(b, 0, 0, ClientSize.Width, band);
            }
        }

        private void ApplyPalette(NoteTheme t)
        {
            th = t;
            accent = t.IconHover;
            accentDeep = ControlPaint.Dark(accent, 0.28f);
            accentSoft = Blend(accent, Color.White, 0.86f);
            accentLine = Blend(accent, Color.White, 0.55f);
            // 列表底白、卡片主题淡色（与「便签列表」同族），其余表面全部由主题派生
            cardBg = Color.FromArgb(250, 251, 253);
            cardLine = Color.FromArgb(232, 236, 241);
            mutedFg = Color.FromArgb(132, 141, 154);
            hair = Color.FromArgb(235, 238, 243);
            doneChipBg = Blend(t.Body, Color.White, 0.8f);
            emptyBg = Blend(t.Body, Color.White, 0.85f);
            chipFg = t.Icon;
        }

        public void ApplyTheme(NoteTheme t)
        {
            ApplyPalette(t);
            if (chromeBar != null)
            {
                FormChrome.ApplyHeaderTheme(chromeBar, t);
                Invalidate(new Rectangle(0, 0, ClientSize.Width, chromeBar.Bottom));
            }
            BackColor = Color.White;
            if (topPanel != null) topPanel.BackColor = Color.White;
            if (body != null) body.BackColor = Color.White;
            if (content != null) content.BackColor = Color.White;
            if (searchBox != null) searchBox.FocusLineColor = accentLine;
            if (addBtn != null)
            {
                addBtn.Theme = t;
                addBtn.Invalidate();
            }
            Render();
        }

        private void BuildBody()
        {
            BackColor = Color.White;

            searchBox = new SearchBox { Placeholder = "搜索待办", Height = Dpi(38) };
            searchBox.IdleColor = FieldIdle;
            searchBox.LineColor = FieldLine;
            searchBox.FocusLineColor = accentLine;
            searchBox.PlaceholderForeColor = Color.FromArgb(163, 171, 182);
            searchBox.TextChanged += (sd, e) => { searchDebounce.Stop(); searchDebounce.Start(); };

            addBtn = new ActionIconButton(AddNew)
            {
                IconId = "add",
                IconSize = 18,
                IconScaleOverride = s,
                AccessibleName = "新增待办",
                BackColor = Color.White,
                Theme = th,
                Accent = true
            };
            actionTips.SetToolTip(addBtn, addBtn.AccessibleName);
            addBtn.MouseWheel += GlobalMouseWheel;

            topPanel = new Panel { BackColor = Color.White };
            topPanel.Controls.Add(searchBox);
            topPanel.Controls.Add(addBtn);
            Controls.Add(topPanel);

            body = new WheelPanel(this) { BackColor = Color.White };
            content = new Panel { BackColor = Color.White, Location = new Point(0, 0) };
            body.Controls.Add(content);
            Controls.Add(body);
            MouseWheel += GlobalMouseWheel;
            content.MouseWheel += GlobalMouseWheel;
            searchBox.Input.MouseWheel += GlobalMouseWheel;

            hdrActive = new GroupHeader(this);
            hdrDone = new GroupHeader(this);

            AdjustLayout();
            Resize += (sd, e) => AdjustLayout();
        }

        private void AdjustLayout()
        {
            if (topPanel == null || body == null) return;
            int ring = Padding.Left;
            int left = ring + Dpi(12);
            int availW = ClientSize.Width - ring * 2 - Dpi(24);

            int rowHeight = Dpi(34);
            int addSize = Dpi(28);
            topPanel.SetBounds(left, chromeBar.Bottom + Dpi(12), availW, rowHeight);
            addBtn.SetBounds(availW - addSize, (rowHeight - addSize) / 2, addSize, addSize);
            searchBox.SetBounds(0, 0, addBtn.Left - Dpi(6), rowHeight);

            int bodyTop = topPanel.Bottom + Dpi(12);
            bool widthChanged = body.Width != availW;
            body.SetBounds(left, bodyTop, availW, Math.Max(Dpi(60), ClientSize.Height - bodyTop - ring - Dpi(16)));
            Relayout();
            if (widthChanged) body.Invalidate(true);
        }

        /// <summary>激活窗口；highlightId 指定的待办会滚到可见并闪一下。</summary>
        public void BringForward(int highlightId = -1)
        {
            if (IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            if (TopMost) { TopMost = false; TopMost = true; }
            if (highlightId < 0) return;
            flashId = highlightId;
            flashTimer.Stop();
            flashTimer.Start();
            // 新增/改动触发的重渲染是延后一拍的，这里等它画完再滚过去
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                Render();
                ScrollTo(highlightId);
            }));
        }

        private void SetTopMost(bool on)
        {
            TopMost = on;
            pinBtn.Selected = on;
        }

        private void ScrollTo(int id)
        {
            var card = cards.FirstOrDefault(c => c.Item.Id == id);
            if (card == null) return;
            SetScrollOffset(card.Top - Dpi(12));
            card.Invalidate();
        }

        // ---- 渲染 ----
        private void Render()
        {
            if (content == null) return;
            body.SuspendLayout();
            foreach (Control c in content.Controls.Cast<Control>().ToList())
            {
                content.Controls.Remove(c);
                if (c != hdrActive && c != hdrDone) c.Dispose();
            }
            rows.Clear();
            cards.Clear();

            string q = keyword.ToLowerInvariant();
            var active = TodoStore.Active();
            var done = TodoStore.Done();
            var fa = q.Length == 0 ? active : active.Where(t => (t.Text ?? "").ToLowerInvariant().Contains(q)).ToList();
            var fd = q.Length == 0 ? done : done.Where(t => (t.Text ?? "").ToLowerInvariant().Contains(q)).ToList();

            hdrActive.Title = "进行";
            hdrActive.Count = CountSuffix(fa.Count, active.Count);
            hdrDone.Title = "完成";
            hdrDone.Count = CountSuffix(fd.Count, done.Count);

            rows.Add(hdrActive);
            content.Controls.Add(hdrActive);
            AddCards(fa, q.Length > 0 ? "没有匹配「" + keyword + "」的待办" : "都清完了，没有待完成的事");

            rows.Add(hdrDone);
            content.Controls.Add(hdrDone);
            AddCards(fd, q.Length > 0 ? "没有匹配的完成项" : "还没有完成记录");

            body.ResumeLayout();
            Relayout();
        }

        private static string CountSuffix(int shown, int total)
            => shown == total ? total.ToString() : shown + " / " + total;

        private void AddCards(List<TodoItem> list, string emptyText)
        {
            if (list.Count == 0)
            {
                var empty = new EmptyBox(this, emptyText);
                rows.Add(empty);
                content.Controls.Add(empty);
                return;
            }
            foreach (var t in list)
            {
                var card = new TodoCard(this, t);
                cards.Add(card);
                rows.Add(card);
                content.Controls.Add(card);
            }
        }

        private void Relayout()
        {
            if (content == null || content.IsDisposed || body == null) return;
            int w = Math.Max(0, body.ClientSize.Width - Dpi(2));
            content.Width = w;
            int y = Dpi(2);
            foreach (var ctl in rows)
            {
                if (ctl.IsDisposed) continue;
                if (ctl == hdrDone) y += Dpi(10);   // 已完成分组前多留一拍（合计 20）
                if (ctl is TodoCard card) card.FitWidth(w, y);
                else ctl.SetBounds(0, y, w, ctl.Height);
                int gap = ctl is GroupHeader ? Dpi(12) : Dpi(10);
                y += ctl.Height + gap;
            }
            content.Height = Math.Max(Dpi(40), y + Dpi(6));
            SetScrollOffset(scrollOffset);
        }

        private void SetScrollOffset(int offset)
        {
            int maxOffset = Math.Max(0, content.Height - body.ClientSize.Height);
            scrollOffset = Math.Max(0, Math.Min(offset, maxOffset));
            if (content.Top != -scrollOffset) content.Top = -scrollOffset;
        }

        private void ShowFocusedAction(TodoCard card, Control button)
        {
            int top = card.Top + button.Top;
            int bottom = card.Top + button.Bottom;
            if (top < scrollOffset) SetScrollOffset(top - Dpi(6));
            else if (bottom > scrollOffset + body.ClientSize.Height)
                SetScrollOffset(bottom - body.ClientSize.Height + Dpi(6));
        }

        // ---- 操作 ----
        private void AddNew()
        {
            using (var dlg = new TodoEditDialog("", false))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var item = TodoStore.Add(dlg.TodoText, dlg.Due, null, null);
                TodoStore.SetSchedule(item, dlg.Due, dlg.Repeat);
                BringForward(item.Id);
            }
        }

        private void AskDue(TodoItem item)
        {
            using (var dlg = new TodoEditDialog(item.Due))
            {
                dlg.SetRepeat(item.Repeat);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                TodoStore.SetSchedule(item, dlg.Due, dlg.Repeat);
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

        internal static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var path = new GraphicsPath();
            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---- 分组标题：名称 + 计数胶囊 + 细线 ----
        private sealed class GroupHeader : Control
        {
            private readonly TodoListForm owner;
            public string Title = "";
            public string Count = "";

            public GroupHeader(TodoListForm o)
            {
                owner = o;
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
                TabStop = false;
                Height = o.fHdr.Height + o.Dpi(8);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var nf = owner.fHdr;
                TextRenderer.DrawText(g, Title, nf, new Rectangle(0, 0, Width, Height), owner.mutedFg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                int nw = TextRenderer.MeasureText(Title, nf).Width;
                var cf = owner.fCnt;
                int ch = cf.Height + owner.Dpi(4);
                var pr = new Rectangle(nw + owner.Dpi(8), (Height - ch) / 2,
                    TextRenderer.MeasureText(Count, cf).Width + owner.Dpi(14), ch);
                using (var path = Rounded(pr, ch / 2))
                {
                    using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                }
                TextRenderer.DrawText(g, Count, cf, pr, owner.chipFg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                int rx = pr.Right + owner.Dpi(8);
                if (rx < Width - 2)
                    using (var p = new Pen(owner.hair)) g.DrawLine(p, rx, Height / 2, Width - 1, Height / 2);
            }
        }

        // ---- 空状态：虚线占位块 ----
        private sealed class EmptyBox : Control
        {
            private readonly TodoListForm owner;

            public EmptyBox(TodoListForm o, string text)
            {
                owner = o;
                Text = text;
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
                TabStop = false;
                Font = o.fEmpty;
                Height = o.Dpi(64);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), owner.Dpi(10)))
                {
                    using (var b = new SolidBrush(owner.emptyBg)) g.FillPath(b, path);
                    using (var p = new Pen(owner.hair)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), owner.mutedFg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private sealed class TodoCard : Panel
        {
            private readonly TodoListForm owner;
            private readonly TextBox txt;
            private readonly List<Chip> chips = new List<Chip>();
            private readonly ToolIconButton completionButton, editButton, deleteButton;
            private readonly ToolIconButton[] actions;

            public TodoItem Item { get; }

            public TodoCard(TodoListForm owner, TodoItem item)
            {
                this.owner = owner;
                Item = item;
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
                BackColor = Color.White;
                Height = owner.Dpi(70);

                txt = new TextBox
                {
                    Multiline = true,
                    WordWrap = true,
                    ReadOnly = true,
                    BorderStyle = BorderStyle.None,
                    ScrollBars = ScrollBars.None,
                    BackColor = item.Done ? Color.White : owner.cardBg,
                    ForeColor = item.Done ? owner.mutedFg : owner.th.Text,
                    Font = new Font(owner.fCard, item.Done ? FontStyle.Strikeout : FontStyle.Regular),
                    Text = (item.Text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
                    TabStop = false,
                    Cursor = Cursors.Default,
                    ShortcutsEnabled = false
                };
                Controls.Add(txt);

                BuildChips();
                bool done = item.Done;
                completionButton = AddAction(done ? "todo" : "check-open", done ? "恢复待办" : "完成待办", () =>
                {
                    if (item.Done == done) TodoStore.SetDone(item, !done);
                });
                if (done) completionButton.GlyphColor = owner.accent;
                editButton = AddAction("edit", "编辑待办", () => owner.EditItem(item));
                if (done) deleteButton = AddAction("trash", "删除待办", () => TodoStore.DeleteCompleted(item));
                actions = done ? new[] { completionButton, editButton, deleteButton } : new[] { completionButton, editButton };

                MouseWheel += owner.GlobalMouseWheel;
                txt.MouseWheel += owner.GlobalMouseWheel;
            }

            private ToolIconButton AddAction(string icon, string label, Action action)
            {
                var button = new ActionIconButton(action)
                {
                    IconId = icon,
                    IconSize = 16,
                    IconScaleOverride = owner.s,
                    AccessibleName = label,
                    BackColor = txt.BackColor,
                    Theme = owner.th
                };
                button.Enter += (s, e) => owner.ShowFocusedAction(this, button);
                button.MouseWheel += owner.GlobalMouseWheel;
                Controls.Add(button);
                owner.actionTips.SetToolTip(button, label);
                return button;
            }

            private static string FormatTime(DateTime time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            private void BuildChips()
            {
                bool done = Item.Done;

                if (done)
                {
                    var fin = Item.DoneAt ?? Item.Created;
                    AddChip("完成 · " + FormatTime(fin), false, ChipKind.Plain);
                }
                else if (Item.Due.HasValue)
                {
                    var due = Item.Due.Value;
                    var kind = Item.IsOverdue ? ChipKind.Warn : ChipKind.Accent;
                    AddChip((Item.IsOverdue ? "过期 · " : "") + FormatTime(due), true, kind);
                    if (Item.Repeat != RepeatRule.None) AddChip(TodoStore.RepeatLabel(Item.Repeat), false, ChipKind.Plain);
                }
                else
                {
                    AddChip("＋ 设置提醒", true, ChipKind.Ghost);
                }
            }

            private void AddChip(string text, bool clickable, ChipKind kind)
            {
                var c = new Chip(owner, text, kind) { Clickable = clickable, Visible = true, Cursor = clickable ? Cursors.Hand : Cursors.Default };
                chips.Add(c);
                Controls.Add(c);
                c.MouseWheel += owner.GlobalMouseWheel;
                if (clickable) c.ActionClick += (s, e) => owner.AskDue(Item);
            }

            internal void PrepareDpiLayout()
            {
                var desired = new Font(owner.fCard, Item.Done ? FontStyle.Strikeout : FontStyle.Regular);
                if (txt.Font.Equals(desired)) desired.Dispose();
                else
                {
                    var previous = txt.Font;
                    txt.Font = desired;
                    previous.Dispose();
                }
                foreach (var button in actions) button.IconScaleOverride = owner.s;
                foreach (var chip in chips) chip.Font = owner.fChip;
            }

            /// <summary>按宽度重排文字与标签，并算出卡片高度（标签换行要累加行高）。</summary>
            public void FitWidth(int w, int y)
            {
                int padTop = owner.Dpi(11);
                int left = owner.Dpi(15);
                int right = owner.Dpi(12);
                int textW = Math.Max(owner.Dpi(40), w - left - right);

                int textH = Math.Max(owner.Dpi(22), MeasureText(txt.Text, txt.Font, textW));
                txt.SetBounds(left, padTop, textW, textH);

                int metaTop = txt.Bottom + owner.Dpi(4);
                int buttonSize = owner.Dpi(24);
                int buttonGap = owner.Dpi(2);
                // Keep the finish and edit buttons in the same columns in both groups.
                int actionLeft = w - right - 3 * buttonSize - 2 * buttonGap;
                int chipH = owner.fChip.Height + owner.Dpi(5);
                int rowHeight = Math.Max(buttonSize, chipH);
                int chipRight = actionLeft - owner.Dpi(8);
                int cx = left, rowTop = metaTop;
                foreach (var c in chips)
                {
                    int chipW = ChipWidth(c);
                    if (cx > left && cx + chipW > chipRight)
                    {
                        cx = left;
                        rowTop += rowHeight + owner.Dpi(4);
                    }
                    c.SetBounds(cx, rowTop + (rowHeight - chipH) / 2, chipW, chipH);
                    cx += chipW + owner.Dpi(6);
                }
                for (int i = 0; i < actions.Length; i++)
                    actions[i].SetBounds(actionLeft + (buttonSize + buttonGap) * i,
                        metaTop + (rowHeight - buttonSize) / 2, buttonSize, buttonSize);

                SetBounds(0, y, w, rowTop + rowHeight + owner.Dpi(9));
            }

            private int ChipWidth(Chip c)
                => TextRenderer.MeasureText(c.Text, c.Font).Width + owner.Dpi(18);

            private static int MeasureText(string text, Font f, int width)
            {
                int total = 0;
                foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                    var sz = TextRenderer.MeasureText(line.Length == 0 ? " " : line, f,
                        new Size(width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                    total += Math.Max(sz.Height, f.Height);
                }
                return total + 4;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                bool done = Item.Done;
                Color bg = done ? Color.White : owner.cardBg;
                Color line = done ? owner.hair : owner.cardLine;

                using (var path = Rounded(rect, owner.Dpi(11)))
                {
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                    using (var p = new Pen(line)) g.DrawPath(p, path);
                }
                if (Item.Id == owner.flashId)
                    using (var p = new Pen(owner.accent, 2f))
                    using (var path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), owner.Dpi(11))) g.DrawPath(p, path);

                base.OnPaint(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    foreach (var button in actions) owner.actionTips.SetToolTip(button, null);
                    txt.Font.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private enum ChipKind { Accent, Plain, Warn, Ghost }

        private sealed class Chip : Control
        {
            private readonly TodoListForm owner;
            private readonly ChipKind kind;
            private bool hover;

            public bool Clickable { get; set; }
            public event EventHandler ActionClick;

            public Chip(TodoListForm owner, string text, ChipKind kind)
            {
                this.owner = owner;
                this.kind = kind;
                Text = text;
                DoubleBuffered = true;
                Font = owner.fChip;
                TabStop = false;
                Height = owner.fChip.Height + owner.Dpi(5);
                MouseEnter += (s, e) => { if (Clickable) { hover = true; Invalidate(); } };
                MouseLeave += (s, e) => { hover = false; Invalidate(); };
                Click += (s, e) => { if (Clickable) ActionClick?.Invoke(this, EventArgs.Empty); };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color bg, line, fg;
                switch (kind)
                {
                    case ChipKind.Accent: bg = owner.accentSoft; line = owner.accentLine; fg = owner.accentDeep; break;
                    case ChipKind.Plain: bg = Color.White; line = owner.cardLine; fg = owner.chipFg; break;
                    case ChipKind.Warn: bg = WarnBg; line = WarnLine; fg = WarnFg; break;
                    default: bg = hover ? owner.accentSoft : Color.Transparent; line = owner.accentLine; fg = owner.accentDeep; break;
                }
                if (Parent is TodoCard card && card.Item.Done && kind != ChipKind.Ghost)
                {
                    bg = owner.doneChipBg; line = owner.hair; fg = owner.mutedFg;
                }

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = Rounded(rect, owner.Dpi(6)))
                {
                    if (kind == ChipKind.Ghost)
                    {
                        using (var p = new Pen(owner.hair)) g.DrawPath(p, path);
                    }
                    else
                    {
                        using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                        using (var p = new Pen(line)) g.DrawPath(p, path);
                    }
                }
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private void EditItem(TodoItem item)
        {
            if (item.DeletedAt.HasValue) return;
            using (var dlg = new TodoEditDialog(item.Text, item.Due))
            {
                dlg.SetRepeat(item.Repeat);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!string.IsNullOrWhiteSpace(dlg.TodoText))
                    TodoStore.SetText(item, dlg.TodoText);
                TodoStore.SetSchedule(item, dlg.Due, dlg.Repeat);
            }
        }

        private void GlobalMouseWheel(object sender, MouseEventArgs e)
        {
            // Child controls bubble the same wheel event through card/content/body.
            // Claim it once so the viewport moves only once per wheel event.
            if (e is HandledMouseEventArgs handled)
            {
                if (handled.Handled) return;
                handled.Handled = true;
            }
            if (e.Delta == 0) return;
            SetScrollOffset(scrollOffset + (e.Delta > 0 ? -Dpi(42) : Dpi(42)));
        }

        private sealed class WheelPanel : Panel
        {
            private readonly TodoListForm owner;
            public WheelPanel(TodoListForm owner)
            {
                this.owner = owner;
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
            }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                owner.GlobalMouseWheel(this, e);
            }
        }
    }
}
