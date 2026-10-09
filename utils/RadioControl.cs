using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    /// <summary>
    /// 自绘单选圆点（含文字，点文字也能选中）。
    /// 原生 RadioButton 的圆点跟 Windows 系统强调色，不跟便签那 8 套主题，所以自己画。
    /// 点击只会置为选中，取消由同组另一个控件负责（单选必有一个选中）。
    /// </summary>
    public class RadioControl : Control
    {
        private bool _checked;
        private Color _accent = Color.FromArgb(74, 127, 193);
        private static readonly Color RingOff = Color.FromArgb(195, 204, 214);
        private static readonly Color TextC = Color.FromArgb(43, 47, 54);

        public event EventHandler CheckedChanged;

        public RadioControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            Font = new Font("Microsoft YaHei UI", 9f);
            Fit();
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public Color Accent
        {
            get { return _accent; }
            set { _accent = value; Invalidate(); }
        }

        public override string Text
        {
            get { return base.Text; }
            set { base.Text = value; Fit(); }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Fit();
        }

        private int S(int v) => (int)Math.Round(v * UiDpi.Factor);

        // 尺寸 = 圆点 + 间距 + 实测文字宽，任何缩放下都不切字
        private void Fit()
        {
            int dot = S(16);
            int gap = S(7);
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font).Width;
            Size = new Size(dot + gap + tw + S(2), Math.Max(dot, Font.Height) + S(4));
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left) Checked = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int dot = S(16);
            int y = (Height - dot) / 2;
            var ring = new Rectangle(S(1), y, dot - S(2), dot - S(2));
            Color line = _checked ? _accent : RingOff;
            if (!Enabled) line = Blend(line, Color.White, 0.55f);

            using (var pen = new Pen(line, Math.Max(1f, S(1))))
                g.DrawEllipse(pen, ring);

            if (_checked)
            {
                int inner = S(8);
                var rc = new Rectangle(ring.X + (ring.Width - inner) / 2,
                                       ring.Y + (ring.Height - inner) / 2, inner, inner);
                using (var b = new SolidBrush(Enabled ? _accent : Blend(_accent, Color.White, 0.55f)))
                    g.FillEllipse(b, rc);
            }

            int tx = dot + S(7);
            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Width - tx, Height),
                    Enabled ? TextC : Blend(TextC, Color.White, 0.55f),
                    TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }
}
