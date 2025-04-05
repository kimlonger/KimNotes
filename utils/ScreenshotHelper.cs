using System;
using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.IO;
using KimNotes.utils;
using System.ComponentModel;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Timer = System.Windows.Forms.Timer;

namespace KimNotes
{
    // 同时需要修改扩展方法类为：
    static class ControlExtensions
    {
        public static void SetToolTip(this Control control, string text)
        {
            new ToolTip().SetToolTip(control, text);
        }
    }

    public class TransparentInputBox : Control
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

        private Size _baseSize = new Size(50, 30);
        #endregion

        #region 构造函数
        public TransparentInputBox()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor |
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer, true);

            Font = new Font("宋体", 12);
            BackColor = Color.Transparent;
            ForeColor = Color.Red;
            Cursor = Cursors.IBeam;
            ImeMode = ImeMode.On;

            _caretTimer = new Timer { Interval = 500 };
            _caretTimer.Tick += (s, e) => {
                _caretVisible = !_caretVisible;
                Invalidate();
            };
            _caretTimer.Start();
            this.PreviewKeyDown += (s, e) => {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                    e.IsInputKey = true;
            };

            MouseWheel += OnMouseWheel;
            // 修改初始尺寸计算方式
            UpdateSize();
            MinimumSize = new Size(50, 30); // 设置最小尺寸
        }
        #endregion

        #region 缩放功能实现
        // 修改鼠标滚轮事件处理
        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            if (ModifierKeys != Keys.Control) return;

            var delta = e.Delta > 0 ? ScaleStep : -ScaleStep;
            _scale = Math.Max(MinScale, Math.Min(MaxScale, _scale + delta));

            // 根据基础尺寸和缩放比例更新显示尺寸
            Size = new Size(
                (int)(_baseSize.Width * _scale),
                (int)(_baseSize.Height * _scale)
            );

            Invalidate();
        }

        // 增强的尺寸更新方法
        private void UpdateSize()
        {
            using (var g = CreateGraphics())
            {
                if (string.IsNullOrEmpty(_text))
                {
                    _baseSize = new Size(50, 30); // 保持最小尺寸
                }
                else
                {
                    var textSize = g.MeasureString(_text, Font);
                    _baseSize = new Size(
                        (int)textSize.Width + 10,
                        (int)textSize.Height + 6
                    );
                }

                // 应用缩放后的尺寸
                Size = new Size(
                    (int)(_baseSize.Width * _scale),
                    (int)(_baseSize.Height * _scale)
                );
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
            // 保存原始变换
            var originalTransform = e.Graphics.Transform;

            // 应用缩放绘制文本
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

            // 恢复原始变换绘制其他元素
            e.Graphics.Transform = originalTransform;

            // 绘制边框
            using (var pen = new Pen(Color.Gray, 1.5f))
            {
                e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1));
            }

            // 计算光标位置（考虑左边距和缩放）
            if (Focused && _caretVisible)
            {
                int baseCaretX = GetCaretPosition();
                int scaledCaretX = (int)(baseCaretX * _scale) + 2; // 左边距2像素
                e.Graphics.DrawLine(Pens.Red,
                    scaledCaretX, 2,
                    scaledCaretX, Height - 4);
            }
        }

        private int GetCaretPosition()
        {
            if (string.IsNullOrEmpty(_text)) return 2;

            using (var g = CreateGraphics())
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
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
                case Keys.Enter:
                    OnConfirmRequested();
                    break;
            }
            base.OnKeyDown(e); // 确保调用基类方法
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

            UpdateCaretIndex(e.Location);
        }

        private void UpdateCaretIndex(Point point)
        {
            var scaledX = point.X / _scale;
            using (var g = CreateGraphics())
            {
                float currentWidth = 0;
                for (int i = 0; i <= _text.Length; i++)
                {
                    if (i == _text.Length)
                    {
                        _caretIndex = i;
                        break;
                    }

                    var charWidth = g.MeasureString(_text[i].ToString(), Font).Width;
                    if (currentWidth + charWidth / 2 > scaledX)
                    {
                        _caretIndex = i;
                        break;
                    }
                    currentWidth += charWidth;
                }
                Invalidate();
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
            else
            {
                UpdateCaretIndex(e.Location);
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
            _caretTimer?.Stop();
            base.Dispose(disposing);
        }
        #endregion
    }

    public static class ScreenshotHelper
    {
        /// <summary>
        /// 启动交互式截图并返回截图窗体
        /// </summary>
        public static Form CaptureInteractive()
        {
            using (var overlay = new ScreenOverlay())
            {
                if (overlay.ShowDialog() != DialogResult.OK) return null;

                var area = overlay.SelectedArea;
                var screenshot = CaptureArea(area);
                return CreateScreenshotForm(screenshot, area);
            }
        }



        /// <summary>
        /// 截取指定屏幕区域
        /// </summary>
        private static Bitmap CaptureArea(Rectangle area)
        {
            var screenshot = new Bitmap(area.Width, area.Height);
            using (var g = Graphics.FromImage(screenshot))
            {
                g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            }
            return screenshot;
        }

        /// <summary>
        /// 创建显示截图的悬浮窗
        /// </summary>
        private static Form CreateScreenshotForm(Bitmap screenshot, Rectangle area)
        {
            var form = BuildBaseForm(area);
            var contextMenu = BuildContextMenu(form); // 修改为只传窗体
            var pb = BuildPictureBox(screenshot, contextMenu);

            var borderPanel = BuildNestedPanels(pb);
            form.Controls.Add(borderPanel);

            BindEvents(form, pb);
            return form;
        }

        /// <summary>
        /// 构建基础窗体结构
        /// </summary>
        private static Form BuildBaseForm(Rectangle area)
        {
            return new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = area.Location,
                ClientSize = new Size(area.Width + 6, area.Height + 6),
                Padding = new Padding(1)
            };
        }

        /// <summary>
        /// 构建右键菜单（动态获取当前图像）
        /// </summary>
        private static ContextMenuStrip BuildContextMenu(Form hostForm)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("复制", null, (s, e) =>
                Clipboard.SetImage(GetCurrentImage(hostForm))));
            menu.Items.Add(new ToolStripMenuItem("标注", null, (s, e) =>
                AnnotateImages(GetCurrentImage(hostForm), hostForm)));
            menu.Items.Add(new ToolStripMenuItem("OCR", null, (s, e) =>
                ScanOCR(GetCurrentImage(hostForm))));
            menu.Items.Add(new ToolStripMenuItem("另存", null, (s, e) =>
                SaveWithDialog(GetCurrentImage(hostForm))));
            menu.Items.Add(new ToolStripMenuItem("销毁", null, (s, e) => hostForm.Close()));
            // 新增标注模式判断
            menu.Opening += (s, e) =>
            {
                var pb = GetPictureBox(hostForm);
                e.Cancel = pb?.ContextMenuStrip == null; // 当标注模式时禁用菜单
            };
            return menu;
        }

        private static Bitmap GetCurrentImage(Form hostForm)
        {
            var pb = GetPictureBox(hostForm);
            return pb?.Image as Bitmap;
        }

        private static PictureBox GetPictureBox(Form form)
        {
            return form.Controls[0]?.Controls[0]?.Controls[0] as PictureBox;
        }

        private static void AnnotateImages(Bitmap screenshot, Form hostForm)
        {
            Color buttonColor = Color.FromArgb(240, 240, 240);
            var originalImage = (Bitmap)screenshot.Clone();
            var annotations = new Stack<Action<Graphics>>();
            var currentMode = AnnotationMode.None;
            Point? rectStart = null;
            Rectangle currentRect = Rectangle.Empty;
            TransparentInputBox activeInputBox = null;

            // 优化工具栏布局
            var toolPanel = new Panel
            {
                Height = 32,  // 增加高度以适应按钮
                Dock = DockStyle.Bottom,
                BackColor = buttonColor,
                Padding = new Padding(3)
            };

            // 修正流式布局容器设置
            var flowPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                Anchor = AnchorStyles.None // 取消固定定位
            };

            // 按钮创建方法（修正布局参数）
            Func<string, int, Button> CreateToolButton = (text, width) =>
            {
                var btn = new Button
                {
                    Text = text,
                    Size = new Size(width, 26),  // 增加按钮高度
                    Margin = new Padding(2),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = buttonColor,
                    Font = new Font("Segoe UI Symbol", 10f), // 增大字体
                    Cursor = Cursors.Hand
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
                return btn;
            };

            // 创建按钮（调整顺序和大小）
            var buttons = new[]
            {
            CreateToolButton("⬜", 32),  // 矩形
            CreateToolButton("T", 32),   // 文本
            CreateToolButton("↩", 32),  // 撤销
            CreateToolButton("✓", 32)   // 确认
            };

            // 设置工具提示
            buttons[0].SetToolTip("矩形标注");
            buttons[1].SetToolTip("文字标注");
            buttons[2].SetToolTip("撤销操作");
            buttons[3].SetToolTip("确认保存");

            // 添加按钮到布局容器
            flowPanel.Controls.AddRange(buttons);
            toolPanel.Controls.Add(flowPanel);

            var pb = GetPictureBox(hostForm);
            var originalMenu = pb.ContextMenuStrip;
            pb.ContextMenuStrip = null;

            // 窗体布局调整（修正尺寸计算）
            hostForm.SuspendLayout();
            toolPanel.Width = (int)(pb.Width * 0.5f);
            flowPanel.Left = toolPanel.Width - flowPanel.PreferredSize.Width - 5; // 动态右对齐
            flowPanel.Location = new Point(
                (toolPanel.Width - flowPanel.PreferredSize.Width) / 2,
                (toolPanel.Height - flowPanel.PreferredSize.Height) / 2
            );
            hostForm.Height += toolPanel.Height;
            hostForm.Controls.Add(toolPanel);
            hostForm.ResumeLayout();

            // 标注层设置（保持原始代码逻辑）
            var annotationLayer = new PictureBox
            {
                Size = pb.Size,
                BackColor = Color.Transparent,
                Dock = DockStyle.Fill
            };
            ((Panel)pb.Parent).Controls.Add(annotationLayer);
            annotationLayer.BringToFront();

            Pen redPen = new Pen(Color.Red, 2);
            Font textFont = new Font("宋体", 12);

            buttons[0].Click += (s, e) => currentMode = AnnotationMode.Rectangle;
            buttons[1].Click += (s, e) =>
            {
                currentMode = AnnotationMode.Text;
                // 新增：禁用原始右键菜单
                pb.ContextMenuStrip = null;
            };

            buttons[2].Click += (s, e) =>
            {
                if (annotations.Count > 0)
                {
                    annotations.Pop();
                    annotationLayer.Invalidate();
                }
            };

            buttons[3].Click += (s, e) =>
            {
                buttons[3].BackColor = Color.LightGreen;
                Application.DoEvents();
                Thread.Sleep(150);

                using (var g = Graphics.FromImage(originalImage))
                {
                    foreach (var action in annotations)
                        action(g);
                }
                pb.Image = originalImage;

                annotationLayer.Dispose();
                toolPanel.Dispose();
                pb.ContextMenuStrip = originalMenu;
                hostForm.Height -= toolPanel.Height;
                // 恢复右键菜单
                pb.ContextMenuStrip = originalMenu;
            };

            annotationLayer.Paint += (s, e) =>
            {
                e.Graphics.DrawImage(pb.Image, annotationLayer.ClientRectangle);
                foreach (var action in annotations)
                    action(e.Graphics);

                if (currentRect != Rectangle.Empty)
                    e.Graphics.DrawRectangle(redPen, currentRect);
            };

            annotationLayer.MouseDown += (s, e) =>
            {
                // 新增：当存在激活文本框时，右键确认
                if (activeInputBox != null && e.Button == MouseButtons.Right)
                {
                    SaveAnnotation(activeInputBox);
                    activeInputBox = null;
                    return; // 阻止后续处理
                }
                if (currentMode == AnnotationMode.Rectangle)
                {
                    rectStart = e.Location;
                }
                else if (currentMode == AnnotationMode.Text && activeInputBox == null)
                {
                    // 创建文本框时禁用右键菜单
                    activeInputBox = new TransparentInputBox
                    {
                        Location = new Point(
                        e.X - 15, // 向右偏移避免光标遮挡
                        e.Y - 10  // 向上偏移保持视觉居中
                    ),
                        _scale = 1.5f // 默认放大1.5倍
                    };
                    activeInputBox.ConfirmRequested += () =>
                    {
                        SaveAnnotation(activeInputBox);
                        activeInputBox = null;
                    };
                    activeInputBox.LostFocus += (_, __) =>
                    {
                        if (activeInputBox != null)
                        {
                            SaveAnnotation(activeInputBox);
                            activeInputBox = null;
                        }
                    };

                    activeInputBox.KeyDown += (_, ke) =>
                    {
                        if (ke.KeyCode == Keys.Enter)
                        {
                            SaveAnnotation(activeInputBox);
                            activeInputBox = null;
                        }
                    };

                    activeInputBox.MouseDown += (_, me) =>
                    {
                        if (me.Button == MouseButtons.Right)
                        {
                            SaveAnnotation(activeInputBox);
                            activeInputBox = null;
                        }
                    };

                    annotationLayer.Controls.Add(activeInputBox);
                    activeInputBox.Focus();
                }
            };

            annotationLayer.MouseMove += (s, e) =>
            {
                if (rectStart.HasValue && currentMode == AnnotationMode.Rectangle)
                {
                    currentRect = new Rectangle(
                        Math.Min(rectStart.Value.X, e.X),
                        Math.Min(rectStart.Value.Y, e.Y),
                        Math.Abs(e.X - rectStart.Value.X),
                        Math.Abs(e.Y - rectStart.Value.Y)
                    );
                    annotationLayer.Invalidate();
                }
            };

            annotationLayer.MouseUp += (s, e) =>
            {
                if (rectStart.HasValue && currentMode == AnnotationMode.Rectangle)
                {
                    if (currentRect.Width > 2 && currentRect.Height > 2)
                    {
                        var finalRect = currentRect;
                        annotations.Push(g => g.DrawRectangle(redPen, finalRect));
                    }
                    rectStart = null;
                    currentRect = Rectangle.Empty;
                    annotationLayer.Invalidate();
                }
            };
            void SaveAnnotation(TransparentInputBox inputBox)
            {
                var text = inputBox.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    // 获取原始坐标（考虑缩放）
                    var pos = inputBox.GetOriginalPosition();
                    var font = inputBox.GetScaledFont();
                    var scale = inputBox._scale;

                    annotations.Push(g =>
                    {
                        // 使用原始尺寸和缩放比例进行绘制
                        var originPos = new Point(
                            (int)(inputBox.Left / inputBox._scale),
                            (int)(inputBox.Top / inputBox._scale)
                        );

                        g.ScaleTransform(inputBox._scale, inputBox._scale);
                        g.TranslateTransform(originPos.X, originPos.Y);
                        g.DrawString(text, inputBox.Font, Brushes.Red, Point.Empty);
                        g.ResetTransform();
                    });
                    annotationLayer.Invalidate();
                }
                annotationLayer.Controls.Remove(inputBox);
                inputBox.Dispose();
            }

        }
      


        enum AnnotationMode
        {
            None,
            Rectangle,
            Text
        }

        /// <summary>
        /// 构建 PictureBox 控件
        /// </summary>
        private static PictureBox BuildPictureBox(Bitmap screenshot, ContextMenuStrip menu)
        {
            return new PictureBox
            {
                Image = screenshot,
                SizeMode = PictureBoxSizeMode.StretchImage,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ContextMenuStrip = menu
            };
        }

        /// <summary>
        /// 构建嵌套边框面板
        /// </summary>
        private static Panel BuildNestedPanels(Control content)
        {
            var borderPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(173, 216, 230),
                Padding = new Padding(1)
            };

            var innerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            innerPanel.Controls.Add(content);
            borderPanel.Controls.Add(innerPanel);

            return borderPanel;
        }

        /// <summary>
        /// 绑定窗体事件（使用动态图像获取）
        /// </summary>
        private static void BindEvents(Form form, PictureBox pb)
        {
            pb.DoubleClick += (s, e) =>
            {
                var img = GetCurrentImage(form);
                if (img != null) SaveAndClose(form, img);
            };

            pb.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && e.Clicks == 1)
                {
                    StartFormDrag(form);
                }
            };

            pb.MouseWheel += (s, e) => ZoomForm(form, e.Delta > 0 ? 1.1f : 0.9f);
            form.FormClosed += (s, e) => (pb.Image as Bitmap)?.Dispose();
        }

        private static void StartFormDrag(Form form)
        {
            Win32ApiHelper.ReleaseCapture();
            Win32ApiHelper.SendMessage(
                form.Handle,
                Win32ApiHelper.WM_NCLBUTTONDOWN,
                Win32ApiHelper.HT_CAPTION,
                0
            );
        }

        private static void SaveAndClose(Form form, Bitmap screenshot)
        {
            try
            {
                SaveToDefaultPath(screenshot);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"自动保存失败: {ex.Message}");
            }
            form.Close();
        }

        private static void SaveToDefaultPath(Bitmap screenshot)
        {
            const string saveDir = @"D:\kimNotes\image";
            Directory.CreateDirectory(saveDir);
            string fileName = $"{DateTime.Now:yyyyMMddHHmmssfff}.png";
            screenshot.Save(Path.Combine(saveDir, fileName), ImageFormat.Png);
        }

        private static void SaveWithDialog(Bitmap screenshot)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG 图片|*.png|JPEG 图片|*.jpg";
                dialog.Title = "保存截图";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var format = dialog.FileName.EndsWith(".jpg") ?
                            ImageFormat.Jpeg : ImageFormat.Png;
                        screenshot.Save(dialog.FileName, format);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存失败: {ex.Message}");
                    }
                }
            }
        }

        private static void ScanOCR(Bitmap screenshot)
        {
            Form waitForm = new Form
            {
                Text = "文字识别",
                Size = new Size(200, 100),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false
            };

            Label waitLabel = new Label
            {
                Text = "识别中，请耐心等待...",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };

            waitForm.Controls.Add(waitLabel);

            BackgroundWorker worker = new BackgroundWorker();

            worker.DoWork += (sender, e) =>
            {
                string base64String;
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    screenshot.Save(memoryStream, ImageFormat.Png);
                    byte[] imageBytes = memoryStream.ToArray();
                    base64String = Convert.ToBase64String(imageBytes);
                }

                e.Result = RemoteCallUtils.generalBasic(base64String);
            };

            worker.RunWorkerCompleted += (sender, e) =>
            {
                if (e.Error != null)
                {
                    MessageBox.Show($"识别失败: {e.Error.Message}");
                }
                else
                {
                    try
                    {
                        var json = JObject.Parse((string)e.Result);
                        var words = json["words_result"]
                            .Select(item => item["words"].ToString())
                            .Where(word => !string.IsNullOrWhiteSpace(word))
                            .ToArray();

                        string combinedText = string.Join(Environment.NewLine, words);
                        Clipboard.SetText(combinedText);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"处理结果时出错: {ex.Message}");
                    }
                }

                waitForm.Close();
            };

            waitForm.Show();
            worker.RunWorkerAsync();
        }

        private static void ZoomForm(Form form, float scaleFactor)
        {
            form.SuspendLayout();
            form.Width = (int)(form.Width * scaleFactor);
            form.Height = (int)(form.Height * scaleFactor);
            form.ResumeLayout();
        }
    }

    /// <summary>
    /// 截图选区覆盖层
    /// </summary>
    internal class ScreenOverlay : Form
    {
        private Point selectionStart;
        private Rectangle selectionRect;
        private readonly Bitmap screenSnapshot;

        public Rectangle SelectedArea { get; private set; }

        public ScreenOverlay()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Cursor = Cursors.Cross;

            // 截取全屏作为背景
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            screenSnapshot = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(screenSnapshot))
            {
                g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
            }

            this.Paint += OverlayPaint;
            this.MouseDown += OverlayMouseDown;
            this.MouseMove += OverlayMouseMove;
            this.MouseUp += OverlayMouseUp;
            this.KeyPress += OverlayKeyPress;
        }

        // 添加边缘调整大小功能
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTCLIENT = 1;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            if (m.Msg == WM_NCHITTEST)
            {
                int borderWidth = 10; // 边缘检测宽度
                Point pos = new Point(m.LParam.ToInt32());
                pos = this.PointToClient(pos);

                if (pos.X <= borderWidth && pos.Y <= borderWidth)
                    m.Result = (IntPtr)HTTOPLEFT;
                else if (pos.X >= this.ClientSize.Width - borderWidth && pos.Y <= borderWidth)
                    m.Result = (IntPtr)HTTOPRIGHT;
                else if (pos.X <= borderWidth && pos.Y >= this.ClientSize.Height - borderWidth)
                    m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (pos.X >= this.ClientSize.Width - borderWidth && pos.Y >= this.ClientSize.Height - borderWidth)
                    m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (pos.X <= borderWidth)
                    m.Result = (IntPtr)HTLEFT;
                else if (pos.X >= this.ClientSize.Width - borderWidth)
                    m.Result = (IntPtr)HTRIGHT;
                else if (pos.Y <= borderWidth)
                    m.Result = (IntPtr)HTTOP;
                else if (pos.Y >= this.ClientSize.Height - borderWidth)
                    m.Result = (IntPtr)HTBOTTOM;
                else
                    m.Result = (IntPtr)HTCLIENT;
                return;
            }
            base.WndProc(ref m);
        }

        private void OverlayMouseDown(object sender, MouseEventArgs e)
        {
            selectionStart = e.Location;
            selectionRect = Rectangle.Empty;
        }

        private void OverlayMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            int x = Math.Min(selectionStart.X, e.X);
            int y = Math.Min(selectionStart.Y, e.Y);
            int width = Math.Abs(e.X - selectionStart.X);
            int height = Math.Abs(e.Y - selectionStart.Y);

            selectionRect = new Rectangle(x, y, width, height);
            this.Invalidate();
        }

        // 修改 OverlayMouseUp 方法
        private void OverlayMouseUp(object sender, MouseEventArgs e)
        {
            if (selectionRect.Width <= 0 || selectionRect.Height <= 0) return;

            // 转换为屏幕绝对坐标
            Point screenTopLeft = this.PointToScreen(selectionRect.Location);
            SelectedArea = new Rectangle(
                screenTopLeft.X,
                screenTopLeft.Y,
                selectionRect.Width,
                selectionRect.Height
            );
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void OverlayKeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Escape) this.Close();
        }

        private void OverlayPaint(object sender, PaintEventArgs e)
        {
            using (TextureBrush brush = new TextureBrush(screenSnapshot))
            {
                e.Graphics.FillRectangle(brush, this.ClientRectangle);
            }

            // 绘制选区
            if (!selectionRect.IsEmpty)
            {
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    e.Graphics.DrawRectangle(pen, selectionRect);
                }

                // 高亮选区外部区域
                using (Region region = new Region(this.ClientRectangle))
                {
                    region.Exclude(selectionRect);
                    e.Graphics.FillRegion(new SolidBrush(Color.FromArgb(128, Color.Black)), region);
                }
            }
        }

        // 在ScreenOverlay类中添加：
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // 显示坐标信息
            if (!selectionRect.IsEmpty)
            {
                string info = $"{selectionRect.Width} x {selectionRect.Height}";
                e.Graphics.DrawString(info, this.Font, Brushes.White,
                    selectionRect.X + 5, selectionRect.Y + 5);
            }
        }

        // 添加网格线辅助定位
        private void DrawGrid(Graphics g)
        {
            using (Pen gridPen = new Pen(Color.FromArgb(50, Color.White)))
            {
                for (int x = 0; x < this.Width; x += 50)
                    g.DrawLine(gridPen, x, 0, x, this.Height);
                for (int y = 0; y < this.Height; y += 50)
                    g.DrawLine(gridPen, 0, y, this.Width, y);
            }
        }
    }
}