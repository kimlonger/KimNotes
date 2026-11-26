using System;
using System.Drawing;
using System.Globalization;
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
        private readonly Font inputFont;

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
            Font = inputFont = new Font("Microsoft YaHei UI", 11);
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
            HandleZoom(e, ModifierKeys);
        }

        private void HandleZoom(MouseEventArgs e, Keys modifiers)
        {
            if (modifiers != Keys.Control) return;
            if (e is HandledMouseEventArgs handled)
            {
                if (handled.Handled) return;
                handled.Handled = true;
            }
            if (e.Delta == 0) return;

            var delta = e.Delta > 0 ? ScaleStep : -ScaleStep;
            _scale = Math.Max(MinScale, Math.Min(MaxScale, _scale + delta));
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
            if (e.Control && e.KeyCode == Keys.V)
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    _text = _text.Insert(_caretIndex, text);
                    _caretIndex += text.Length;
                    UpdateSize();
                    Invalidate();
                }
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            switch (e.KeyCode)
            {
                case Keys.Back:
                    if (_caretIndex > 0)
                    {
                        int previous = PreviousTextElement(_caretIndex);
                        _text = _text.Remove(previous, _caretIndex - previous);
                        _caretIndex = previous;
                        UpdateSize();
                        Invalidate();
                    }
                    break;
                case Keys.Delete when _caretIndex < _text.Length:
                    _text = _text.Remove(_caretIndex, NextTextElement(_caretIndex) - _caretIndex);
                    UpdateSize();
                    Invalidate();
                    break;
                case Keys.Home:
                    _caretIndex = 0;
                    Invalidate();
                    break;
                case Keys.End:
                    _caretIndex = _text.Length;
                    Invalidate();
                    break;
                case Keys.Left when _caretIndex > 0:
                    _caretIndex = PreviousTextElement(_caretIndex);
                    Invalidate();
                    break;
                case Keys.Right when _caretIndex < _text.Length:
                    _caretIndex = NextTextElement(_caretIndex);
                    Invalidate();
                    break;
            }
        }

        private int PreviousTextElement(int index)
        {
            int previous = 0;
            foreach (int boundary in StringInfo.ParseCombiningCharacters(_text))
            {
                if (boundary >= index) break;
                previous = boundary;
            }
            return previous;
        }

        private int NextTextElement(int index)
        {
            foreach (int boundary in StringInfo.ParseCombiningCharacters(_text))
                if (boundary > index) return boundary;
            return _text.Length;
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
                inputFont.Dispose();
            }
            base.Dispose(disposing);
        }
        #endregion
    }

}
