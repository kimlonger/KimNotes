using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes.utils
{
    public partial class TransparentInputBox : Control
    {
        #region 字段和常量
        private string _text = "";
        private bool _isDragging;
        private Point _dragOffset;
        private int _caretIndex;
        private Timer _caretTimer;
        private bool _caretVisible;
        private bool _imeComposing;

        // 缩放相关
        public float _scale = 1.0f;
        private const float MinScale = 0.5f;
        private const float MaxScale = 3.0f;
        private const float ScaleStep = 0.1f;

        // IME消息常量
        private const int WM_IME_STARTCOMPOSITION = 0x010D;
        private const int WM_IME_ENDCOMPOSITION = 0x010E;
        private const int WM_IME_COMPOSITION = 0x010F;
        #endregion

        #region 构造函数
        public TransparentInputBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor |
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer, true);
            this.PreviewKeyDown += (s, e) => {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                    e.IsInputKey = true;
            };
            Font = new Font("宋体", 14);
            ForeColor = Color.DarkRed;
            Cursor = Cursors.IBeam;
            ImeMode = ImeMode.On;

            _caretTimer = new Timer { Interval = 500 };
            _caretTimer.Tick += (s, e) => {
                _caretVisible = !_caretVisible;
                Invalidate();
            };
            _caretTimer.Start();

            MouseWheel += OnMouseWheel;
            // 修改初始尺寸计算方式
            UpdateSize();
        }
        #endregion

        #region 缩放功能实现
        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            if (ModifierKeys != Keys.Control) return;

            var delta = e.Delta > 0 ? ScaleStep : -ScaleStep;
            _scale = Math.Max(MinScale, Math.Min(MaxScale, _scale + delta));

            using (var g = CreateGraphics())
            {
                var size = g.MeasureString(_text, Font).ToSize();
                Size = new Size((int)(size.Width * _scale) + 10, (int)(size.Height * _scale) + 6);
            }

            UpdateSize();
            Invalidate();
        }

        // 增强的尺寸更新方法
        private void UpdateSize()
        {
            using (var g = CreateGraphics())
            {
                var baseSize = g.MeasureString("默认文本", Font);
                int minWidth = (int)(baseSize.Width * 1.2f);
                int minHeight = (int)(baseSize.Height * 1.5f);

                if (string.IsNullOrEmpty(_text))
                {
                    Size = new Size(minWidth, minHeight);
                }
                else
                {
                    var textSize = g.MeasureString(_text, Font);
                    Size = new Size(
                        Math.Max((int)(textSize.Width * _scale) + 10, minWidth),
                        Math.Max((int)(textSize.Height * _scale) + 6, minHeight)
                    );
                }
            }
        }
        // 新增坐标转换方法
        public Point GetOriginalPosition()
        {
            return new Point(
                (int)(Location.X / _scale),
                (int)(Location.Y / _scale)
            );
        }
        #endregion

        #region 输入法处理
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_IME_STARTCOMPOSITION:
                    _imeComposing = true;
                    break;
                case WM_IME_ENDCOMPOSITION:
                    _imeComposing = false;
                    break;
                case WM_IME_COMPOSITION:
                    if ((m.LParam.ToInt32() & 0x0001) != 0)
                    {
                        _imeComposing = false;
                    }
                    break;
            }
            base.WndProc(ref m);
        }
        #endregion

        #region 绘制逻辑
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.ScaleTransform(_scale, _scale);

            using (var brush = new SolidBrush(ForeColor))
            using (var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                e.Graphics.DrawString(_text, Font, brush,
                    new RectangleF(2 / _scale, 0, Width / _scale, Height / _scale),
                    format);
            }

            // 绘制边框
            using (var pen = new Pen(Color.Gray, 2.0f / _scale))
            {
                e.Graphics.ResetTransform();
                e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1));
            }

            // 绘制光标
            if (Focused && _caretVisible)
            {
                var caretX = GetCaretPosition() * _scale;
                e.Graphics.DrawLine(Pens.Red,
                    caretX + 2, 2,
                    caretX + 2, Height - 4);
            }
        }

        private int GetCaretPosition()
        {
            if (string.IsNullOrEmpty(_text)) return 2;

            using (var g = CreateGraphics())
            {
                return (int)g.MeasureString(_text.Substring(0, _caretIndex), Font).Width;
            }
        }
        #endregion

        #region 输入处理
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (_imeComposing || char.IsControl(e.KeyChar)) return;

            _text = _text.Insert(_caretIndex, e.KeyChar.ToString());
            _caretIndex++;
            UpdateSize();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Back:
                    if (_caretIndex > 0)
                    {
                        _text = _text.Remove(--_caretIndex, 1);
                        UpdateSize();
                        Invalidate();
                    }
                    break;
                case Keys.Left when _caretIndex > 0:
                    _caretIndex--;
                    Invalidate();
                    break;
                case Keys.Right when _caretIndex < _text.Length:
                    _caretIndex++;
                    Invalidate();
                    break;
            }
        }
        #endregion

        #region 鼠标交互
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _dragOffset = e.Location;
                Cursor = Cursors.SizeAll;
            }
            else if (e.Button == MouseButtons.Right)
            {
                OnConfirmRequested();
            }

        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isDragging)
            {
                var newPos = Parent.PointToClient(PointToScreen(e.Location));
                Location = new Point(
                    newPos.X - _dragOffset.X,
                    newPos.Y - _dragOffset.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) => _isDragging = false;
        #endregion

        #region 公共方法
        public event Action ConfirmRequested;
        private void OnConfirmRequested() => ConfirmRequested?.Invoke();
        public string GetText() => _text;

        public Font GetScaledFont() => new Font(Font.FontFamily, Font.Size * _scale, Font.Style);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _caretTimer?.Stop();
                _caretTimer?.Dispose();
                _caretTimer = null;
            }
            base.Dispose(disposing);
        }
        #endregion
    }

}
