using System;
using System.Drawing;
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

        public SearchBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
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
                BackColor = BgFocus;
                _txt.BackColor = BgFocus;
                ClearPlaceholder();
            };
            _txt.LostFocus += (s, e) =>
            {
                BackColor = BgIdle;
                _txt.BackColor = BgIdle;
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

            Region = RoundRegion(Width, Height, 10);
        }

        private static Region RoundRegion(int w, int h, int r)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = r * 2;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(w - d, 0, d, d, 270, 90);
            path.AddArc(w - d, h - d, d, d, 0, 90);
            path.AddArc(0, h - d, d, d, 90, 90);
            path.CloseFigure();
            return new Region(path);
        }

        private void ApplyPlaceholder()
        {
            if (!string.IsNullOrEmpty(_txt.Text) && !_showingPlaceholder) return;
            if (_txt.Focused) return;
            _showingPlaceholder = true;
            _txt.Text = _placeholder;
            _txt.ForeColor = PlaceholderColor;
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
