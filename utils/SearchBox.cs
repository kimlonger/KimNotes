using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KimNotes
{
    /// <summary>
    /// 自包含搜索框：圆角灰底、聚焦变白、占位符、内嵌线稿放大镜。
    /// 对外只暴露 Text / TextChanged / Placeholder / FocusInput / Input（供滚轮等接线）。
    /// </summary>
    public class SearchBox : Control
    {
        private readonly TextBox _txt = new TextBox();
        private readonly ToolIconButton _icon = new ToolIconButton { IconId = "search" };
        private bool _showingPlaceholder;
        private string _placeholder = "搜索";

        private static readonly Color BgIdle = Color.FromArgb(241, 243, 246);
        private static readonly Color BgFocus = Color.White;
        private static readonly Color TextColor = Color.FromArgb(43, 47, 54);
        private static readonly Color PlaceholderColor = Color.FromArgb(138, 146, 158);

        private Color _idle = BgIdle;
        private Color _focus = BgFocus;
        private Color _line = Color.Empty;      // Empty = 不描边
        private Color _lineFocus = Color.Empty;
        private Color _phColor = PlaceholderColor;

        /// <summary>失焦底色 / 聚焦底色 / 失焦描边 / 聚焦描边 / 占位符字色，供各窗按主题统一。</summary>
        public Color IdleColor { get { return _idle; } set { _idle = value; SyncSurface(); } }
        public Color FocusColor { get { return _focus; } set { _focus = value; SyncSurface(); } }
        public Color LineColor { get { return _line; } set { _line = value; Invalidate(); } }
        public Color FocusLineColor { get { return _lineFocus; } set { _lineFocus = value; Invalidate(); } }
        public Color PlaceholderForeColor { get { return _phColor; } set { _phColor = value; ApplyPlaceholder(); } }

        internal void UseOutlineTheme(NoteTheme theme, Color surface)
        {
            _idle = _focus = surface;
            _line = theme.Divider;
            _lineFocus = theme.IconHover;
            _icon.Theme = theme;
            SyncSurface();
            _icon.Invalidate();
        }

        private void SyncSurface()
        {
            Color bg = _txt.Focused ? _focus : _idle;
            BackColor = bg;
            _txt.BackColor = bg;
            Invalidate();
        }

        public SearchBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = BgIdle;
            Height = 44;

            _txt.BorderStyle = BorderStyle.None;
            _txt.BackColor = BgIdle;
            _txt.ForeColor = PlaceholderColor;
            _txt.Font = new Font("Microsoft YaHei UI", 10f);
            _txt.Cursor = Cursors.IBeam;
            _txt.TabStop = true;

            _txt.GotFocus += (s, e) =>
            {
                SyncSurface();
                ClearPlaceholder();
            };
            _txt.LostFocus += (s, e) =>
            {
                SyncSurface();
                ApplyPlaceholder();
            };
            _txt.TextChanged += (s, e) => OnTextChanged(EventArgs.Empty);
            _icon.Click += (s, e) => _txt.Focus();

            Controls.Add(_txt);
            Controls.Add(_icon);
            Resize += (s, e) => LayoutInner();
            ApplyPlaceholder();
            LayoutInner();
        }

        public string Placeholder
        {
            get { return _placeholder; }
            set { _placeholder = value; ApplyPlaceholder(); }
        }

        /// <summary>真实文本（占位符显示时为空）。</summary>
        public override string Text
        {
            get { return _showingPlaceholder ? string.Empty : _txt.Text; }
            set { _txt.Text = value; _showingPlaceholder = false; _txt.ForeColor = TextColor; }
        }

        public TextBox Input => _txt;

        public void FocusInput() => _txt.Focus();

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var surround = new SolidBrush(Parent?.BackColor ?? BackColor))
                e.Graphics.FillRectangle(surround, e.ClipRectangle);
            if (Width < 5 || Height < 5) return;

            var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            int radius = Math.Min((int)Math.Round(9 * DeviceDpi / 96f), Math.Min(bounds.Width, bounds.Height) / 2);
            Color line = _txt.Focused ? _lineFocus : _line;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = ToolIconButton.RoundedPath(bounds, radius))
            using (var surface = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(surface, path);
                if (!line.IsEmpty)
                    using (var pen = new Pen(line)) e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            LayoutInner();
        }

        private void LayoutInner()
        {
            int iconSize = 28;
            _icon.Size = new Size(iconSize, iconSize);
            _icon.Location = new Point(Width - iconSize - 8, (Height - iconSize) / 2);

            _txt.Height = Math.Max(20, _txt.Font.Height + 4);
            _txt.Location = new Point(14, (Height - _txt.Height) / 2);
            _txt.Width = Math.Max(40, _icon.Left - 8 - _txt.Left);
        }

        private void ApplyPlaceholder()
        {
            if (!string.IsNullOrEmpty(_txt.Text) && !_showingPlaceholder) return;
            if (_txt.Focused) return;
            _showingPlaceholder = true;
            _txt.Text = _placeholder;
            _txt.ForeColor = _phColor;
        }

        private void ClearPlaceholder()
        {
            if (!_showingPlaceholder) return;
            _showingPlaceholder = false;
            _txt.Text = string.Empty;
            _txt.ForeColor = TextColor;
        }
    }
}
