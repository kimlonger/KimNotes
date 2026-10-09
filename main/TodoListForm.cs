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
    public class TodoListForm : Form
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
        private TodoCard pendingClick;
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
            ClientSize = new Size(Dpi(400), Dpi(580));
            MinimumSize = new Size(Dpi(360), Dpi(420));

            ApplyPalette(NoteTheme.Current());

            fCard = new Font("Microsoft YaHei UI", 10.5f);
            fChip = new Font("Microsoft YaHei UI", 8f);
            fHint = new Font("Microsoft YaHei UI", 7.5f);
            fHdr = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            fCnt = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Bold);
            fEmpty = new Font("Microsoft YaHei UI", 9f);
            fBtn = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

            pinBtn = new ToolIconButton { IconId = "pin", Size = new Size(34, 34), Selected = true };
            pinBtn.Click += (sd, e) => SetTopMost(!TopMost);
            Text = "待办清单";
            Icon = FormChrome.AppIcon;
            chromeBar = FormChrome.Apply(this, "待办清单", false, null, th.Chrome, th.ChromeText, pinBtn);

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(0, wa.Right - Width - Dpi(24)), Math.Max(0, (wa.Height - Height) / 2));

            BuildBody();

            searchDebounce.Tick += (sd, e) => { searchDebounce.Stop(); keyword = searchBox.Text.Trim(); Render(); };
            clickTimer.Interval = SystemInformation.DoubleClickTime;
            clickTimer.Tick += (sd, e) =>
            {
                clickTimer.Stop();
                var c = pendingClick;
                pendingClick = null;
                if (c != null) ToggleDone(c.Item);
            };
            flashTimer.Tick += (sd, e) => { flashTimer.Stop(); flashId = -1; Invalidate(true); };

            TodoStore.Changed += OnStoreChanged;
            FormClosed += (sd, e) => TodoStore.Changed -= OnStoreChanged;
            Render();
        }

        private void OnStoreChanged()
        {
            if (IsDisposed) return;
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
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED，降低闪烁
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
            cardBg = Blend(t.Body, Color.White, 0.5f);
            cardLine = Blend(t.Divider, Color.White, 0.35f);
            mutedFg = Blend(t.Icon, Color.White, 0.35f);
            hair = Blend(t.Divider, Color.White, 0.5f);
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
                chromeBar.BackColor = t.Chrome;
                var lbl = chromeBar.Tag as Label;
                if (lbl != null) lbl.ForeColor = t.ChromeText;
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

            body = new Panel { BackColor = Color.White, AutoScroll = true };
            content = new Panel { BackColor = Color.White, Location = new Point(0, 0) };
            body.Controls.Add(content);
            Controls.Add(body);
            body.MouseWheel += (sd, e) => ForwardWheel(e.Delta);
            content.MouseWheel += (sd, e) => ForwardWheel(e.Delta);
            searchBox.Input.MouseWheel += (sd, e) => ForwardWheel(e.Delta);
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
            int left = ring + Dpi(16);
            int availW = ClientSize.Width - ring * 2 - Dpi(32);

            topPanel.Bounds = new Rectangle(left, chromeBar.Bottom + Dpi(15), availW, Dpi(38));
            int addW = Math.Min(addBtn.Width, availW / 3);
            addBtn.Bounds = new Rectangle(availW - addW, 0, addW, Dpi(38));
            searchBox.Bounds = new Rectangle(0, 0, Math.Max(Dpi(80), addBtn.Left - Dpi(9)), Dpi(38));

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
                int gap = ctl is GroupHeader ? Dpi(9) : Dpi(10);
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
                BringForward(item.Id);
            }
        }

        private void AskDue(TodoItem item)
        {
            using (var dlg = new TodoEditDialog(item.Due))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                TodoStore.SetDue(item, dlg.Due);
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
                Height = o.fHdr.Height + o.Dpi(2);
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
                    using (var p = new Pen(owner.hair)) g.DrawPath(p, path);
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
                    using (var p = new Pen(owner.hair) { DashStyle = DashStyle.Dash }) g.DrawPath(p, path);
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
                Color bg = pressed ? Blend(owner.accent, Color.White, 0.66f)
                          : hover ? Blend(owner.accent, Color.White, 0.78f)
                          : owner.accentSoft;
                using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), owner.Dpi(9)))
                {
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                    using (var p = new Pen(owner.accentLine)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, owner.accentDeep,
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
                    BackColor = owner.cardBg,
                    ForeColor = item.Done ? owner.mutedFg : owner.th.Text,
                    Font = new Font(owner.fCard, item.Done ? FontStyle.Strikeout : FontStyle.Regular),
                    Text = item.Text ?? "",
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
                    Text = item.Done ? "单击恢复 ↺" : "单击完成 · 双击编辑",
                    Visible = item.Done,
                    TabStop = false,
                    Cursor = Cursors.Hand
                };
                Controls.Add(hint);

                BuildChips();

                MouseEnter += (sd, e) => SetHover(true);
                MouseLeave += (sd, e) => SetHoverLeave();
                Click += (sd, e) => owner.CardClicked(this);
                MouseDoubleClick += (sd, e) => owner.CardDoubleClicked(this);
                MouseWheel += (sd, e) => owner.ForwardWheel(e.Delta);
                foreach (Control c in new Control[] { txt, hint }) HookChild(c);
                foreach (var c in chips) HookChip(c);
            }

            private void HookChild(Control c)
            {
                c.MouseEnter += (sd, e) => SetHover(true);
                c.MouseLeave += (sd, e) => SetHoverLeave();
                c.Click += (sd, e) => owner.CardClicked(this);
                c.MouseDoubleClick += (sd, e) => owner.CardDoubleClicked(this);
                c.MouseWheel += (sd, e) => owner.ForwardWheel(e.Delta);
            }

            private void HookChip(Chip c)
            {
                HookChild(c);
                c.ActionClick += (sd, e) =>
                {
                    if (c == dueChip) owner.AskDue(Item);
                };
            }

            private void SetHover(bool on)
            {
                if (hover == on) return;
                hover = on;
                hint.Visible = on || Item.Done;
                if (dueChip != null) dueChip.Visible = on;
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
                    AddChip("✓ 完成 " + fin.ToString("yyyy-MM-dd"), false, ChipKind.Plain);
                    AddChip(fin.ToString("HH:mm"), false, ChipKind.Plain);
                }
                else if (Item.Due.HasValue)
                {
                    var due = Item.Due.Value;
                    var kind = Item.IsOverdue ? ChipKind.Warn : ChipKind.Accent;
                    AddChip((Item.IsOverdue ? "已过期 · " : "🔔 ") + due.ToString("yyyy-MM-dd"), false, kind);
                    AddChip(due.ToString("HH:mm"), false, kind);
                }
                else
                {
                    AddChip("📅 记于 " + Item.Created.ToString("yyyy-MM-dd"), false, ChipKind.Plain);
                    dueChip = AddChip("＋提醒", true, ChipKind.Ghost);
                }
            }

            private Chip AddChip(string text, bool clickable, ChipKind kind)
            {
                var c = new Chip(owner, text, kind) { Clickable = clickable, Visible = kind != ChipKind.Ghost };
                chips.Add(c);
                Controls.Add(c);
                return c;
            }

            /// <summary>按宽度重排文字与标签，并算出卡片高度（标签换行要累加行高）。</summary>
            public void FitWidth(int w)
            {
                int padTop = owner.Dpi(13);
                int left = owner.Dpi(19);                    // 左侧色条 4 + 内边距 15
                int right = owner.Dpi(15);
                int textW = Math.Max(owner.Dpi(40), w - left - right);

                txt.Width = textW;
                txt.Height = Math.Max(owner.Dpi(22), MeasureText(txt.Text, txt.Font, textW));
                txt.Location = new Point(left, padTop);

                int metaTop = txt.Bottom + owner.Dpi(10);
                int hintW = TextRenderer.MeasureText(hint.Text, hint.Font).Width;
                int rowW = Math.Max(owner.Dpi(60), textW - hintW - owner.Dpi(8));
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

                Height = metaTop + metaH + owner.Dpi(12);
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
                    using (var b = new SolidBrush(bar)) g.FillRectangle(b, 0, 0, owner.Dpi(4), Height);
                    g.ResetClip();
                    using (var p = new Pen(line)) g.DrawPath(p, path);
                    if (hover && !done)
                        using (var p = new Pen(owner.accentLine)) g.DrawPath(p, path);
                }
                if (Item.Id == owner.flashId)
                    using (var p = new Pen(owner.accent, 2f)) g.DrawPath(p, Rounded(new Rectangle(1, 1, Width - 3, Height - 3), owner.Dpi(11)));

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
                if (kind == ChipKind.Ghost)
                {
                    Cursor = Cursors.Hand;
                    MouseEnter += (s, e) => { hover = true; Invalidate(); };
                    MouseLeave += (s, e) => { hover = false; Invalidate(); };
                    Click += (s, e) => ActionClick?.Invoke(this, EventArgs.Empty);
                }
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
                        using (var p = new Pen(line) { DashStyle = DashStyle.Dash }) g.DrawPath(p, path);
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
            pendingClick = c;
            clickTimer.Stop();
            clickTimer.Start();
        }

        private void CardDoubleClicked(TodoCard c)
        {
            clickTimer.Stop();
            pendingClick = null;
            EditItem(c.Item);
        }

        /// <summary>双击编辑：同时改内容与提醒时间（复用录入弹窗的「内容 + 时间」能力）。</summary>
        private void EditItem(TodoItem item)
        {
            if (item == null) return;
            using (var dlg = new TodoEditDialog(item.Text, item.Due))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!string.IsNullOrWhiteSpace(dlg.TodoText))
                    TodoStore.SetText(item, dlg.TodoText);
                TodoStore.SetDue(item, dlg.Due);   // 含 null（不提醒）的处理与提醒去重
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
