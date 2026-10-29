using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    /// <summary>
    /// 待办清单：全局单例窗口，块状卡片（单击整块完成/恢复、双击改字、悬停补提醒）。
    /// 默认固定置顶，标题栏只有 📌 和 ✕；上方是搜索框 +「＋ 新增」，没有底部工具栏与筛选按钮。
    /// </summary>
    public class TodoListForm : AppWindow
    {
        private readonly float s;
        private NoteTheme th;
        private Color accent, accentDeep, accentSoft, accentLine;
        private Color cardBg, cardLine, mutedFg, hair, doneBar, doneChipBg, emptyBg, chipFg;
        private static readonly Color WarnBg = Color.FromArgb(251, 227, 224);
        private static readonly Color WarnLine = Color.FromArgb(238, 198, 192);
        private static readonly Color WarnFg = Color.FromArgb(192, 57, 43);
        private static readonly Color OverdueBar = Color.FromArgb(217, 83, 79);
        private static readonly Color FieldIdle = Color.FromArgb(244, 246, 249);
        private static readonly Color FieldLine = Color.FromArgb(227, 231, 236);

        private Panel chromeBar, topPanel, body, content;
        private ToolIconButton pinBtn;
        private SearchBox searchBox;
        private RoundBtn addBtn;
        private GroupHeader hdrActive, hdrDone;
        // 字号用单倍缩放磅值（系统按 DPI 自缩放），控件高度一律按字体实测高度推，避免字被裁
        private Font fCard, fChip, fHint, fHdr, fCnt, fEmpty, fBtn;
        private readonly List<Control> rows = new List<Control>();
        private readonly List<TodoCard> cards = new List<TodoCard>();
        private readonly Timer searchDebounce = new Timer { Interval = 120 };
        private readonly Timer clickTimer = new Timer();
        private readonly Timer flashTimer = new Timer { Interval = 900 };
        private readonly Timer statusTimer = new Timer { Interval = 30000 };
        private bool resourcesDisposed;
        private readonly Dictionary<int, TodoItem> pendingClicks = new Dictionary<int, TodoItem>();
        private int flashId = -1;
        private string keyword = "";

        public TodoListForm()
        {
            s = UiDpi.Factor;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9f);
            Padding = new Padding((int)(5 * s));
            TopMost = true;                       // 默认固定置顶
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(Dpi(CompactLayout.Width), Dpi(CompactLayout.Height));
            MinimumSize = new Size(Dpi(CompactLayout.Width), Dpi(360));

            ApplyPalette(NoteTheme.Current());

            fCard = new Font("Microsoft YaHei UI", 10f);
            fChip = new Font("Microsoft YaHei UI", 8.5f);
            fHint = new Font("Microsoft YaHei UI", 7.5f);
            fHdr = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            fCnt = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Bold);
            fEmpty = new Font("Microsoft YaHei UI", 9f);
            fBtn = new Font("Microsoft YaHei UI", 9f);

            pinBtn = new ToolIconButton { IconId = "pin", Size = new Size(34, 34), Selected = true };
            pinBtn.Click += (sd, e) => SetTopMost(!TopMost);
            Text = "待办清单";
            Icon = FormChrome.AppIcon;
            chromeBar = FormChrome.Apply(this, "待办清单", false, null, Color.White, th.Text, pinBtn, false);

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(wa.Left, wa.Right - Width - Dpi(24)), wa.Top + Math.Max(0, (wa.Height - Height) / 2));

            BuildBody();

            searchDebounce.Tick += (sd, e) => { searchDebounce.Stop(); keyword = searchBox.Text.Trim(); Render(); };
            clickTimer.Interval = SystemInformation.DoubleClickTime;
            clickTimer.Tick += (sd, e) =>
            {
                clickTimer.Stop();
                var pending = pendingClicks.Values.ToArray();
                pendingClicks.Clear();
                foreach (var item in pending) ToggleDone(item);
            };
            flashTimer.Tick += (sd, e) => { flashTimer.Stop(); flashId = -1; Invalidate(true); };

            TodoStore.Changed += OnStoreChanged;
            FormClosed += (sd, e) => TodoStore.Changed -= OnStoreChanged;
            statusTimer.Tick += (sd, e) => { if (!clickTimer.Enabled) Render(); };
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
                clickTimer.Dispose();
                pendingClicks.Clear();
                flashTimer.Dispose();
                statusTimer.Dispose();
                base.Dispose(disposing);
                foreach (var font in new[] { fCard, fChip, fHint, fHdr, fCnt, fEmpty, fBtn, Font }) font?.Dispose();
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
            doneBar = Blend(t.Icon, Color.White, 0.8f);
            doneChipBg = Blend(t.Body, Color.White, 0.8f);
            emptyBg = Blend(t.Body, Color.White, 0.85f);
            chipFg = t.Icon;
        }

        public void ApplyTheme(NoteTheme t)
        {
            ApplyPalette(t);
            if (chromeBar != null)
            {
                chromeBar.BackColor = Color.White;
                var lbl = chromeBar.Tag as Label;
                if (lbl != null) lbl.ForeColor = t.Text;
            }
            BackColor = Color.White;
            if (topPanel != null) topPanel.BackColor = Color.White;
            if (body != null) body.BackColor = Color.White;
            if (content != null) content.BackColor = Color.White;
            if (searchBox != null) searchBox.FocusLineColor = accentLine;
            if (addBtn != null) addBtn.Invalidate();
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

            addBtn = new RoundBtn(this) { Text = "＋ 新增", Height = Dpi(38) };
            addBtn.Width = TextRenderer.MeasureText(addBtn.Text, addBtn.Font).Width + Dpi(32);
            addBtn.Click += (sd, e) => AddNew();

            topPanel = new Panel { BackColor = Color.White };
            topPanel.Controls.Add(searchBox);
            topPanel.Controls.Add(addBtn);
            Controls.Add(topPanel);

            body = new WheelPanel(this) { BackColor = Color.White, AutoScroll = true };
            content = new Panel { BackColor = Color.White, Location = new Point(0, 0) };
            body.Controls.Add(content);
            Controls.Add(body);
            MouseWheel += GlobalMouseWheel;
            content.MouseWheel += GlobalMouseWheel;
            searchBox.Input.MouseWheel += GlobalMouseWheel;
            body.Resize += (sd, e) => Relayout();
            body.HandleCreated += (sd, e) => HideScrollBars();

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

            topPanel.Bounds = new Rectangle(left, chromeBar.Bottom + Dpi(12), availW, Dpi(34));
            int addW = Math.Min(addBtn.Width, availW / 3);
            addBtn.Bounds = new Rectangle(availW - addW, 0, addW, Dpi(34));
            searchBox.Bounds = new Rectangle(0, 0, Math.Max(Dpi(80), addBtn.Left - Dpi(8)), Dpi(34));

            int bodyTop = topPanel.Bottom + Dpi(12);
            body.Bounds = new Rectangle(left, bodyTop, availW,
                Math.Max(Dpi(60), ClientSize.Height - bodyTop - ring - Dpi(16)));
            Relayout();
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
            int y = card.Top - Dpi(12);
            if (y < 0) y = 0;
            body.AutoScrollPosition = new Point(0, y);
            HideScrollBars();
            card.Invalidate();
        }

        // ---- 渲染 ----
        private void Render()
        {
            if (content == null) return;
            int scroll = -body.AutoScrollPosition.Y;
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

            hdrActive.Title = "进行中";
            hdrActive.Count = CountSuffix(fa.Count, active.Count);
            hdrDone.Title = "已完成";
            hdrDone.Count = CountSuffix(fd.Count, done.Count);

            rows.Add(hdrActive);
            content.Controls.Add(hdrActive);
            AddCards(fa, q.Length > 0 ? "没有匹配「" + keyword + "」的待办" : "都清完了，没有待完成的事");

            rows.Add(hdrDone);
            content.Controls.Add(hdrDone);
            AddCards(fd, q.Length > 0 ? "没有匹配的已完成项" : "还没有完成记录");

            body.ResumeLayout();
            Relayout();
            body.AutoScrollPosition = new Point(0, scroll);
            HideScrollBars();
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
            int w = Math.Max(Dpi(80), body.ClientSize.Width - Dpi(2));
            content.Width = w;
            int y = Dpi(2);
            foreach (var ctl in rows)
            {
                if (ctl.IsDisposed) continue;
                if (ctl == hdrDone) y += Dpi(10);   // 已完成分组前多留一拍（合计 20）
                ctl.Width = w;
                if (ctl is TodoCard card) card.FitWidth(w);
                ctl.Location = new Point(0, y);
                int gap = ctl is GroupHeader ? Dpi(12) : Dpi(10);
                y += ctl.Height + gap;
            }
            content.Height = Math.Max(Dpi(40), y + Dpi(6));
            HideScrollBars();
        }

        // 系统滚动条与卡片风格不搭：藏掉，滚动仍走滚轮（AutoScroll 偏移不受影响）
        private void HideScrollBars()
        {
            if (body != null && body.IsHandleCreated)
                Win32ApiHelper.ShowScrollBar(body.Handle, Win32ApiHelper.SB_BOTH, false);
        }

        // ---- 操作 ----
        private void ToggleDone(TodoItem item)
        {
            TodoStore.SetDone(item, !item.Done);
        }

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

        // ---- 强调色圆角按钮（＋ 新增） ----
        private sealed class RoundBtn : Control
        {
            private readonly TodoListForm owner;
            private bool hover, pressed;

            public RoundBtn(TodoListForm o)
            {
                owner = o;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
                TabStop = false;
                Font = owner.fBtn;
                MouseEnter += (s, e) => { hover = true; Invalidate(); };
                MouseLeave += (s, e) => { hover = false; pressed = false; Invalidate(); };
                MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } };
                MouseUp += (s, e) => { pressed = false; Invalidate(); };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color bg = pressed ? owner.accentDeep : hover ? ControlPaint.Dark(owner.accent, 0.08f) : owner.accent;
                using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), owner.Dpi(9)))
                {
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                }
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        // ================= 卡片 =================
        private sealed class TodoCard : Panel
        {
            private readonly TodoListForm owner;
            private readonly TextBox txt;
            private readonly List<Chip> chips = new List<Chip>();
            private readonly Label hint;
            private Chip dueChip;
            private bool hover;

            public TodoItem Item { get; }

            public TodoCard(TodoListForm owner, TodoItem item)
            {
                this.owner = owner;
                Item = item;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
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

                hint = new Label
                {
                    AutoSize = false,
                    Height = owner.fHint.Height,
                    Font = owner.fHint,
                    ForeColor = owner.mutedFg,
                    TextAlign = ContentAlignment.MiddleRight,
                    Text = "双击编辑",
                    Visible = false,
                    TabStop = false,
                    Cursor = Cursors.Hand
                };
                Controls.Add(hint);

                BuildChips();

                MouseEnter += (sd, e) => SetHover(true);
                MouseLeave += (sd, e) => SetHoverLeave();
                Click += (sd, e) => owner.CardClicked(this);
                MouseDoubleClick += (sd, e) => owner.CardDoubleClicked(this);
                MouseWheel += owner.GlobalMouseWheel;
                foreach (Control c in new Control[] { txt, hint }) HookChild(c);
                foreach (var c in chips) HookChip(c);
            }

            private void HookChild(Control c)
            {
                c.MouseEnter += (sd, e) => SetHover(true);
                c.MouseLeave += (sd, e) => SetHoverLeave();
                c.Click += (sd, e) => owner.CardClicked(this);
                c.MouseDoubleClick += (sd, e) => owner.CardDoubleClicked(this);
                c.MouseWheel += owner.GlobalMouseWheel;
            }

            private void HookChip(Chip c)
            {
                if (!c.Clickable) { HookChild(c); return; }
                c.MouseEnter += (sd, e) => SetHover(true);
                c.MouseLeave += (sd, e) => SetHoverLeave();
                c.MouseWheel += owner.GlobalMouseWheel;
                c.ActionClick += (sd, e) =>
                {
                    owner.CancelPendingClick(Item);
                    if (c == dueChip) owner.AskDue(Item);
                };
            }

            private void SetHover(bool on)
            {
                if (hover == on) return;
                hover = on;
                hint.Visible = on;
                if (Width > 0) FitWidth(Width);
                Invalidate();
            }

            private void SetHoverLeave()
            {
                if (ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
                SetHover(false);
            }

            private void BuildChips()
            {
                var th = owner.th;
                bool done = Item.Done;

                if (done)
                {
                    var fin = Item.DoneAt ?? Item.Created;
                    AddChip("完成于 " + fin.ToString("MM-dd HH:mm"), false, ChipKind.Plain);
                }
                else if (Item.Due.HasValue)
                {
                    var due = Item.Due.Value;
                    var kind = Item.IsOverdue ? ChipKind.Warn : ChipKind.Accent;
                    string day = due.Date == DateTime.Today ? "今天" : due.Date == DateTime.Today.AddDays(1) ? "明天" : due.ToString("MM-dd");
                    dueChip = AddChip((Item.IsOverdue ? "已过期 · " : "") + day + " " + due.ToString("HH:mm"), true, kind);
                    if (Item.Repeat != RepeatRule.None) AddChip(TodoStore.RepeatLabel(Item.Repeat), false, ChipKind.Plain);
                }
                else
                {
                    dueChip = AddChip("＋ 设置提醒", true, ChipKind.Ghost);
                }
            }

            private Chip AddChip(string text, bool clickable, ChipKind kind)
            {
                var c = new Chip(owner, text, kind) { Clickable = clickable, Visible = true, Cursor = clickable ? Cursors.Hand : Cursors.Default };
                chips.Add(c);
                Controls.Add(c);
                return c;
            }

            /// <summary>按宽度重排文字与标签，并算出卡片高度（标签换行要累加行高）。</summary>
            public void FitWidth(int w)
            {
                int padTop = owner.Dpi(15);
                int left = owner.Dpi(43);
                int right = owner.Dpi(16);
                int textW = Math.Max(owner.Dpi(40), w - left - right);

                txt.Width = textW;
                txt.Height = Math.Max(owner.Dpi(22), MeasureText(txt.Text, txt.Font, textW));
                txt.Location = new Point(left, padTop);

                int metaTop = txt.Bottom + owner.Dpi(9);
                int hintW = TextRenderer.MeasureText(hint.Text, hint.Font).Width;
                int rowW = Math.Max(owner.Dpi(60), textW - (hint.Visible ? hintW + owner.Dpi(8) : 0));
                int chipH = owner.fChip.Height + owner.Dpi(5);
                int cx = left, cy = metaTop, rowBottom = metaTop;
                foreach (var c in chips)
                {
                    if (!c.Visible) continue;
                    c.Height = chipH;
                    c.Width = ChipWidth(c);
                    if (cx > left && cx + c.Width > left + rowW)
                    {
                        cx = left;
                        cy = rowBottom + owner.Dpi(6);
                    }
                    c.Location = new Point(cx, cy);
                    cx += c.Width + owner.Dpi(7);
                    rowBottom = cy + c.Height;
                }
                int metaH = rowBottom - metaTop;
                if (metaH == 0) metaH = chipH;
                hint.Width = hintW;
                hint.Location = new Point(w - right - hintW, metaTop + (metaH - hint.Height) / 2);

                Height = metaTop + metaH + owner.Dpi(15);
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
                Color bar = done ? owner.doneBar : (Item.IsOverdue ? OverdueBar : owner.accent);

                using (var path = Rounded(rect, owner.Dpi(11)))
                {
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                    g.SetClip(path);
                    g.ResetClip();
                    using (var p = new Pen(line)) g.DrawPath(p, path);
                    if (hover && !done)
                        using (var p = new Pen(owner.accentLine)) g.DrawPath(p, path);
                }
                var check = new Rectangle(owner.Dpi(15), owner.Dpi(17), owner.Dpi(16), owner.Dpi(16));
                if (done)
                    using (var b = new SolidBrush(owner.accentSoft)) g.FillEllipse(b, check);
                using (var p = new Pen(done ? owner.accent : (Item.IsOverdue ? OverdueBar : owner.accentLine), 1.4f * owner.s))
                    g.DrawEllipse(p, check);
                if (done)
                    using (var p = new Pen(owner.accent, 1.6f * owner.s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(p, new[] { new Point(check.Left + owner.Dpi(4), check.Top + owner.Dpi(8)),
                            new Point(check.Left + owner.Dpi(7), check.Top + owner.Dpi(11)), new Point(check.Left + owner.Dpi(12), check.Top + owner.Dpi(5)) });
                if (Item.Id == owner.flashId)
                    using (var p = new Pen(owner.accent, 2f))
                    using (var path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), owner.Dpi(11))) g.DrawPath(p, path);

                base.OnPaint(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && txt != null && txt.Font != null) txt.Font.Dispose();
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

        // 卡片事件统一走窗口，方便区分单击/双击
        private void CardClicked(TodoCard c)
        {
            if (c.Item == null) return;
            // Keep task objects rather than controls: a queued store refresh can
            // rebuild cards while these single clicks wait for the double click.
            pendingClicks[c.Item.Id] = c.Item;
            clickTimer.Stop();
            clickTimer.Start();
        }

        private void CardDoubleClicked(TodoCard c)
        {
            CancelPendingClick(c.Item);
            EditItem(c.Item);
        }

        private void CancelPendingClick(TodoItem item)
        {
            if (item != null) pendingClicks.Remove(item.Id);
            if (pendingClicks.Count == 0) clickTimer.Stop();
        }

        /// <summary>双击编辑：同时改内容与提醒时间（复用录入弹窗的「内容 + 时间」能力）。</summary>
        private void EditItem(TodoItem item)
        {
            if (item == null) return;
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
            // Claim it once so both our handlers and native AutoScroll stop here.
            if (e is HandledMouseEventArgs handled)
            {
                if (handled.Handled) return;
                handled.Handled = true;
            }
            if (e.Delta == 0) return;
            ForwardWheel(e.Delta);
        }

        private sealed class WheelPanel : Panel
        {
            private readonly TodoListForm owner;
            public WheelPanel(TodoListForm owner) { this.owner = owner; }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                // ScrollableControl scrolls before raising MouseWheel, even when
                // the event was already handled by a child. Route it before base.
                owner.GlobalMouseWheel(this, e);
            }
        }

        private void ForwardWheel(int delta)
        {
            var pos = body.AutoScrollPosition;
            body.AutoScrollPosition = new Point(pos.X, -pos.Y - (delta > 0 ? Dpi(42) : -Dpi(42)));
            // 改滚动位置会触发系统重算滚动条，滚动后补藏一次
            HideScrollBars();
        }
    }
}
