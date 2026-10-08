using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KimNotes
{
    /// <summary>
    /// 自绘开关（仿 iOS 胶囊）：开=主题强调色，关=浅灰；点击切换 Checked。
    /// 供设置页替代原生 CheckBox 视觉。
    /// </summary>
    public class SwitchControl : Control
    {
        private bool _checked;
        private Color _accent = Color.FromArgb(74, 127, 193);
        private static readonly Color OffColor = Color.FromArgb(213, 219, 227);

        public event EventHandler CheckedChanged;

        public SwitchControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(40, 22);
            Cursor = Cursors.Hand;
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

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left) Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color track = _checked ? _accent : OffColor;
            if (!Enabled) track = Blend(track, Color.White, 0.55f);

            int h = Height;
            int r = h; // 胶囊两端半圆
            using (var path = Capsule(ClientRectangle))
            using (var b = new SolidBrush(track))
                g.FillPath(b, path);

            int knobD = h - 4;
            int pad = 2;
            int kx = _checked ? Width - pad - knobD : pad;
            var knobRect = new Rectangle(kx, pad, knobD, knobD);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, knobRect);
        }

        private static GraphicsPath Capsule(Rectangle rc)
        {
            var path = new GraphicsPath();
            int d = rc.Height;
            path.AddArc(rc.X, rc.Y, d, d, 90, 180);
            path.AddArc(rc.Right - d, rc.Y, d, d, 270, 180);
            path.CloseFigure();
            return path;
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
