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

namespace KimNotes
{
    public static class ScreenshotHelper
    {
        private static string imagePath = "";
        private static bool trace;
        private const int PasteBorder = 1;
        private static readonly Color PasteBorderColor = Color.FromArgb(90, 90, 90);

        private sealed class ScreenshotState
        {
            public Size OriginalSize { get; }
            public float Scale { get; set; } = 1.0f;
            public float MinScale { get; set; } = 0.1f;
            public float MaxScale { get; set; } = 3.0f;
            public bool IsColorPicking { get; set; }
            public bool IsAnnotating { get; set; }
            public ToolTip PickerToolTip { get; } = new ToolTip();

            public ScreenshotState(Size originalSize)
            {
                OriginalSize = originalSize;
            }
        }

        private sealed class ScaledPictureBox : PictureBox
        {
            private float _scale = 1.0f;

            public float ScaleFactor => _scale;

            public void SetScale(float scale)
            {
                _scale = Math.Max(0.1f, scale);
                UpdateScaledSize();
                Invalidate();
            }

            public void UpdateScaledSize()
            {
                if (Image == null) return;
                Size = new Size(
                    Math.Max(1, (int)Math.Round(Image.Width * _scale)),
                    Math.Max(1, (int)Math.Round(Image.Height * _scale)));
            }

            protected override void OnPaint(PaintEventArgs pe)
            {
                if (Image == null)
                {
                    base.OnPaint(pe);
                    return;
                }

                pe.Graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

                if (_scale >= 1.0f)
                {
                    pe.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    pe.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                }
                else
                {
                    pe.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    pe.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                }

                pe.Graphics.DrawImage(Image, new Rectangle(0, 0, Width, Height));

                if (PasteBorder > 0)
                {
                    int border = Math.Max(1, PasteBorder);
                    using (var brush = new SolidBrush(PasteBorderColor))
                    {
                        pe.Graphics.FillRectangle(brush, 0, 0, Width, border); // top
                        pe.Graphics.FillRectangle(brush, 0, 0, border, Height); // left
                        pe.Graphics.FillRectangle(brush, Width - border, 0, border, Height); // right
                        pe.Graphics.FillRectangle(brush, 0, Height - border, Width, border); // bottom
                    }
                }
            }
        }

        /// <summary>
        /// 启动交互式截图并返回截图窗体
        /// </summary>
        public static Form CaptureInteractive(string path, bool flag)
        {
            imagePath = path;
            trace = flag;
            using (var overlay = new ScreenOverlay())
            {
                if (overlay.ShowDialog() != DialogResult.OK) return null;

                var localArea = overlay.SelectedAreaLocal;
                if (localArea.Width <= 0 || localArea.Height <= 0) return null;

                var screenshot = CropFromSnapshot(overlay.ScreenSnapshot, localArea);
                var displayArea = new Rectangle(
                    overlay.CurrentScreen.Bounds.X + localArea.X,
                    overlay.CurrentScreen.Bounds.Y + localArea.Y,
                    localArea.Width,
                    localArea.Height
                );
                return CreateScreenshotForm(screenshot, displayArea);
            }
        }

        /// <summary>
        /// 从全屏快照中裁剪指定区域（考虑DPI缩放）
        /// </summary>
        private static Bitmap CropFromSnapshot(Bitmap fullscreenSnapshot, Rectangle localArea)
        {
            int x = localArea.X;
            int y = localArea.Y;
            int width = localArea.Width;
            int height = localArea.Height;

            // 确保裁剪区域在图像范围内
            Rectangle bounds = new Rectangle(0, 0, fullscreenSnapshot.Width, fullscreenSnapshot.Height);
            Rectangle crop = Rectangle.Intersect(new Rectangle(x, y, width, height), bounds);
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                crop = new Rectangle(Math.Max(0, Math.Min(x, fullscreenSnapshot.Width - 1)),
                    Math.Max(0, Math.Min(y, fullscreenSnapshot.Height - 1)),
                    1, 1);
            }

            var cropped = new Bitmap(crop.Width, crop.Height);
            using (var gDest = Graphics.FromImage(cropped))
            {
                gDest.DrawImage(
                    fullscreenSnapshot,
                    new Rectangle(0, 0, crop.Width, crop.Height),
                    crop,
                    GraphicsUnit.Pixel
                );
            }
            return cropped;
        }

        /// <summary>
        /// 创建显示截图的悬浮窗
        /// </summary>
        private static Form CreateScreenshotForm(Bitmap screenshot, Rectangle area)
        {
            var form = BuildBaseForm(area);
            // 确保窗体显示在所选区域的屏幕上
            var screen = Screen.FromRectangle(area);
            form.Location = new Point(
                area.X - screen.Bounds.X + screen.Bounds.Left,
                area.Y - screen.Bounds.Y + screen.Bounds.Top
            );
            var contextMenu = BuildContextMenu(form);
            var pb = BuildPictureBox(screenshot, contextMenu);
            form.Controls.Add(pb);
            form.ContextMenuStrip = contextMenu;

            var state = new ScreenshotState(screenshot.Size);
            form.Tag = state;
            ApplyScale(form, pb, state);
            ClampToScreen(form);

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
                AutoScaleMode = AutoScaleMode.None,
                FormBorderStyle = FormBorderStyle.None,
                TopMost = true,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = area.Location,
                ClientSize = new Size(area.Width, area.Height),
                Padding = Padding.Empty,
                KeyPreview = true
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

            var pickColorItem = new ToolStripMenuItem("拾色", null, (s, e) =>
                StartColorPick(hostForm));
            var resetZoomItem = new ToolStripMenuItem("重置 100%", null, (s, e) =>
                ResetZoom(hostForm));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(pickColorItem);
            menu.Items.Add(resetZoomItem);

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

        private static ScaledPictureBox GetPictureBox(Form form)
        {
            return form.Controls.OfType<ScaledPictureBox>().FirstOrDefault();
        }

        private static void AnnotateImages(Bitmap screenshot, Form hostForm)
        {

            var pb = GetPictureBox(hostForm);
            var state = hostForm.Tag as ScreenshotState;
            if (pb == null || state == null || state.IsAnnotating) return;

            if (Math.Abs(state.Scale - 1.0f) > 0.001f)
            {
                state.Scale = 1.0f;
                ApplyScale(hostForm, pb, state);
            }
            state.IsAnnotating = true;
            Color buttonColor = Color.FromArgb(240, 240, 240);
            // 若上一轮标注的结果就是当前图片，直接在其上继续；否则做一份副本
            Bitmap originalImage = ReferenceEquals(pb.Image, screenshot)
                ? screenshot
                : new Bitmap(screenshot);
            // 按添加顺序绘制标注（原 Stack 遍历是反序，后画的会垫底）
            var annotations = new List<Action<Graphics>>();
            var currentMode = AnnotationMode.None;
            Point? rectStart = null;
            Rectangle currentRect = Rectangle.Empty;
            TransparentInputBox activeInputBox = null;

            // 工具栏：固定尺寸，不随截图大小缩放
            var toolPanel = new Panel
            {
                Height = 32,
                BackColor = buttonColor,
                Padding = new Padding(3)
            };

            var flowPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                Anchor = AnchorStyles.None
            };

            // 按钮创建方法
            Func<string, int, Button> CreateToolButton = (text, width) =>
            {
                var btn = new Button
                {
                    Text = text,
                    Size = new Size(width, 26),
                    Margin = new Padding(2),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = buttonColor,
                    Font = new Font("Segoe UI Symbol", 10f),
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
            CreateToolButton("↙", 32),  // 箭头按钮
            CreateToolButton("▒", 32),   // 新增马赛克按钮
            CreateToolButton("T", 32),   // 文本
            CreateToolButton("↩", 32),  // 撤销
            CreateToolButton("✓", 32)   // 确认
            };

            // 设置工具提示（共用一个 ToolTip，避免每个控件泄漏一个）
            var toolTip = new ToolTip();
            toolTip.SetToolTip(buttons[0], "矩形标注");
            toolTip.SetToolTip(buttons[1], "箭头标注");
            toolTip.SetToolTip(buttons[2], "马赛克");
            toolTip.SetToolTip(buttons[3], "文字标注");
            toolTip.SetToolTip(buttons[4], "撤销操作");
            toolTip.SetToolTip(buttons[5], "确认保存");

            // 添加按钮到布局容器
            flowPanel.Controls.AddRange(buttons);
            toolPanel.Controls.Add(flowPanel);

            var originalMenu = pb.ContextMenuStrip;
            pb.ContextMenuStrip = null;

            // 工具栏定位：优先放截图下方，放不下放上方，再放不下悬浮在图片内侧底部
            int panelWidth = Math.Max(flowPanel.PreferredSize.Width + 10, 60);
            int panelHeight = toolPanel.Height;
            toolPanel.Size = new Size(panelWidth, panelHeight);
            flowPanel.Location = new Point(
                (panelWidth - flowPanel.PreferredSize.Width) / 2,
                (panelHeight - flowPanel.PreferredSize.Height) / 2
            );

            var screen = Screen.FromControl(hostForm);
            var wa = screen.WorkingArea;
            int centerX = hostForm.Left + (hostForm.Width - panelWidth) / 2;
            centerX = Math.Min(Math.Max(centerX, wa.Left), wa.Right - panelWidth);

            Point originalFormLocation = hostForm.Location;
            hostForm.SuspendLayout();
            if (hostForm.Bottom + panelHeight <= wa.Bottom)
            {
                // 下方空间足够：窗体向下扩展
                hostForm.Height += panelHeight;
                toolPanel.Location = new Point(centerX - hostForm.Left, hostForm.Height - panelHeight);
            }
            else if (hostForm.Top - panelHeight >= wa.Top)
            {
                // 放到截图上方：窗体向上扩展，图片下移让出工具栏位置
                hostForm.Location = new Point(hostForm.Left, hostForm.Top - panelHeight);
                hostForm.Height += panelHeight;
                toolPanel.Location = new Point(centerX - hostForm.Left, 0);
                pb.Location = new Point(pb.Location.X, pb.Location.Y + panelHeight);
            }
            else
            {
                // 都放不下：悬浮在图片内侧底部
                toolPanel.Location = new Point(
                    Math.Min(Math.Max((hostForm.Width - panelWidth) / 2, 0), Math.Max(0, hostForm.Width - panelWidth)),
                    Math.Max(0, hostForm.Height - panelHeight));
            }
            hostForm.Controls.Add(toolPanel);
            hostForm.ResumeLayout();
            ClampToScreen(hostForm);

            // 标注层设置（保持原始代码逻辑）
            var annotationLayer = new PictureBox
            {
                Size = pb.Size,
                Location = pb.Location,
                BackColor = Color.Transparent,
                Dock = DockStyle.None
            };
            hostForm.Controls.Add(annotationLayer);
            annotationLayer.BringToFront();
            toolPanel.BringToFront();

            Pen redPen = new Pen(Color.Red, 2);
            Font textFont = new Font("宋体", 12);

            buttons[0].Click += (s, e) => currentMode = AnnotationMode.Rectangle;
            buttons[1].Click += (s, e) => currentMode = AnnotationMode.Arrow;
            //马赛克按钮点击事件
            buttons[2].Click += (s, e) => currentMode = AnnotationMode.Mosaic;
            buttons[3].Click += (s, e) =>
            {
                currentMode = AnnotationMode.Text;
                // 新增：禁用原始右键菜单
                pb.ContextMenuStrip = null;
            };

            buttons[4].Click += (s, e) =>
            {
                if (annotations.Count > 0)
                {
                    annotations.RemoveAt(annotations.Count - 1);
                    annotationLayer.Invalidate();
                }
            };

            buttons[5].Click += (s, e) =>
            {
                // 先提交未确认的文本框
                if (activeInputBox != null)
                {
                    SaveAnnotation(activeInputBox);
                    activeInputBox = null;
                }

                using (var g = Graphics.FromImage(originalImage))
                {
                    foreach (var action in annotations)
                        action(g);
                }

                // 先移除并释放标注层，避免它在 pb.Image 更新后把图片画到自身上
                hostForm.Controls.Remove(annotationLayer);
                annotationLayer.Dispose();

                pb.Image = originalImage;
                pb.Invalidate();

                hostForm.Controls.Remove(toolPanel);
                toolPanel.Dispose();
                toolTip.Dispose();

                // 还原图片位置与窗体尺寸（ApplyScale 会把 pb 归位到左上角并按图片重设客户区）
                ApplyScale(hostForm, pb, state);
                hostForm.Location = originalFormLocation;

                // 恢复右键菜单
                pb.ContextMenuStrip = originalMenu;
                state.IsAnnotating = false;
                ClampToScreen(hostForm);
            };

            annotationLayer.Paint += (s, e) =>
            {
                var baseImage = pb.Image;
                if (baseImage == null) return;
                e.Graphics.DrawImage(baseImage, annotationLayer.ClientRectangle);
                foreach (var action in annotations)
                    action(e.Graphics);

                // 修改后的绘制逻辑
                if (currentMode == AnnotationMode.Rectangle && currentRect != Rectangle.Empty)
                {
                    e.Graphics.DrawRectangle(redPen, currentRect);
                }
                else if (currentMode == AnnotationMode.Arrow && currentRect != Rectangle.Empty)
                {
                    DrawArrow(e.Graphics, currentRect.Location,
                        new Point(currentRect.Right, currentRect.Bottom));
                }
                else if (currentMode == AnnotationMode.Mosaic && currentRect != Rectangle.Empty)
                {
                    // 绘制马赛克选择区域
                    e.Graphics.DrawRectangle(redPen, currentRect);

                    // 预览马赛克效果
                    Rectangle previewRect = new Rectangle(
                        Math.Min(currentRect.X, currentRect.Right),
                        Math.Min(currentRect.Y, currentRect.Bottom),
                        Math.Abs(currentRect.Width),
                        Math.Abs(currentRect.Height));

                    if (previewRect.Width > 5 && previewRect.Height > 5)
                    {
                        // 绘制半透明预览
                        using (Bitmap previewBitmap = new Bitmap(previewRect.Width, previewRect.Height))
                        {
                            using (Graphics previewG = Graphics.FromImage(previewBitmap))
                            {
                                // 复制原始区域
                                previewG.DrawImage(
                                    baseImage,
                                    new Rectangle(0, 0, previewRect.Width, previewRect.Height),
                                    previewRect,
                                    GraphicsUnit.Pixel);
                            }

                            // 应用马赛克预览效果
                            ApplyMosaicEffect(previewBitmap, 8);

                            // 半透明绘制预览
                            e.Graphics.DrawImage(previewBitmap, previewRect);
                        }
                    }
                }
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
                if (currentMode == AnnotationMode.Arrow)
                {
                    rectStart = e.Location;
                }
                if (currentMode == AnnotationMode.Mosaic)
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
                if (rectStart.HasValue)
                {
                    if (currentMode == AnnotationMode.Rectangle)
                    {
                        currentRect = new Rectangle(
                            Math.Min(rectStart.Value.X, e.X),
                            Math.Min(rectStart.Value.Y, e.Y),
                            Math.Abs(e.X - rectStart.Value.X),
                            Math.Abs(e.Y - rectStart.Value.Y)
                        );
                    }
                    else if (currentMode == AnnotationMode.Arrow)
                    {
                        currentRect = new Rectangle(
                            rectStart.Value.X,
                            rectStart.Value.Y,
                            e.X - rectStart.Value.X,
                            e.Y - rectStart.Value.Y
                        );
                    }
                    else if (currentMode == AnnotationMode.Mosaic)
                    {
                        currentRect = new Rectangle(
                            Math.Min(rectStart.Value.X, e.X),
                            Math.Min(rectStart.Value.Y, e.Y),
                            Math.Abs(e.X - rectStart.Value.X),
                            Math.Abs(e.Y - rectStart.Value.Y)
                        );
                    }
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
                        annotations.Add(g => g.DrawRectangle(redPen, finalRect));
                    }
                    rectStart = null;
                    currentRect = Rectangle.Empty;
                    annotationLayer.Invalidate();
                }
                if (rectStart.HasValue && currentMode == AnnotationMode.Arrow)
                {
                    if (Math.Abs(e.X - rectStart.Value.X) > 2 ||
                        Math.Abs(e.Y - rectStart.Value.Y) > 2)
                    {
                        var startPoint = rectStart.Value;
                        var endPoint = e.Location;
                        annotations.Add(g => DrawArrow(g, startPoint, endPoint));
                    }
                    rectStart = null;
                    currentRect = Rectangle.Empty;
                    annotationLayer.Invalidate();
                }
                if (rectStart.HasValue && currentMode == AnnotationMode.Mosaic)
                {
                    if (currentRect.Width > 5 && currentRect.Height > 5)
                    {
                        var finalRect = currentRect;
                        // 捕获当前区域的图像以应用马赛克
                        Rectangle captureRect = new Rectangle(
                            Math.Min(finalRect.X, finalRect.Right),
                            Math.Min(finalRect.Y, finalRect.Bottom),
                            Math.Abs(finalRect.Width),
                            Math.Abs(finalRect.Height));

                        // 为该区域创建一个独立的马赛克处理
                        annotations.Add(g => ApplyMosaicToArea(g, captureRect, pb.Image));
                    }
                    rectStart = null;
                    currentRect = Rectangle.Empty;
                    annotationLayer.Invalidate();
                }
            };
            void SaveAnnotation(TransparentInputBox inputBox)
            {
                // 防止重复保存/释放（输入框自身与标注层的右键事件会各触发一次）
                if (inputBox == null || inputBox.IsDisposed) return;

                var text = inputBox.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    // 获取原始坐标（考虑缩放）
                    var pos = inputBox.GetOriginalPosition();
                    var font = inputBox.GetScaledFont();
                    var scale = inputBox._scale;

                    annotations.Add(g =>
                    {
                        // 应用双重缩放补偿
                        g.ScaleTransform(scale, scale);
                        g.TranslateTransform(pos.X, pos.Y);
                        g.DrawString(text, font, Brushes.Red, Point.Empty);
                        g.ResetTransform();
                    });
                    annotationLayer.Invalidate();
                }
                if (!annotationLayer.IsDisposed)
                {
                    annotationLayer.Controls.Remove(inputBox);
                }
                inputBox.Dispose();
            }

        }

        private static void DrawArrow(Graphics g, Point start, Point end)
        {
            using (Pen redPen = new Pen(Color.Red, 2))
            {
                // 绘制主线
                g.DrawLine(redPen, start, end);

                // 计算箭头角度
                float angle = (float)Math.Atan2(end.Y - start.Y, end.X - start.X);

                // 绘制箭头头部
                float arrowSize = 10;
                PointF[] arrowPoints =
                {
            end,
            new PointF(
                end.X - arrowSize * (float)Math.Cos(angle - Math.PI/6),
                end.Y - arrowSize * (float)Math.Sin(angle - Math.PI/6)),
            new PointF(
                end.X - arrowSize * (float)Math.Cos(angle + Math.PI/6),
                end.Y - arrowSize * (float)Math.Sin(angle + Math.PI/6))
        };

                g.FillPolygon(Brushes.Red, arrowPoints);
            }
        }

        enum AnnotationMode
        {
            None,
            Rectangle,
            Text,
            Arrow, // 添加箭头模式
            Mosaic // 添加马赛克模式
        }

        /// <summary>
        /// 构建 PictureBox 控件
        /// </summary>
        private static ScaledPictureBox BuildPictureBox(Bitmap screenshot, ContextMenuStrip menu)
        {
            var pb = new ScaledPictureBox
            {
                Image = screenshot,
                SizeMode = PictureBoxSizeMode.Normal,
                Location = Point.Empty,
                Margin = Padding.Empty,
                ContextMenuStrip = menu
            };
            pb.SetScale(1.0f);
            return pb;
        }

        /// <summary>
        /// 绑定窗体事件（使用动态图像获取）
        /// </summary>
        private static void BindEvents(Form form, ScaledPictureBox pb)
        {
            var state = form.Tag as ScreenshotState;
            if (state == null) return;
            bool isSyncing = false;

            void SyncLayout()
            {
                if (isSyncing) return;
                isSyncing = true;
                ApplyScale(form, pb, state);
                ClampToScreen(form);
                isSyncing = false;
            }

            pb.DoubleClick += (s, e) =>
            {
                var img = GetCurrentImage(form);
                if (trace)
                {
                    form.Close();
                }
                else
                {
                    if (img != null) SaveAndClose(form, img);
                }
            };

            pb.MouseDown += (s, e) =>
            {
                if (state.IsColorPicking)
                {
                    if (e.Button == MouseButtons.Left && e.Clicks == 1)
                    {
                        PickColorAt(form, pb, state, e.Location);
                    }
                    else if (e.Button == MouseButtons.Right)
                    {
                        state.IsColorPicking = false;
                        pb.Cursor = Cursors.Default;
                    }
                    return;
                }

                if (e.Button == MouseButtons.Left && e.Clicks == 1)
                {
                    StartFormDrag(form);
                }
            };
            pb.MouseMove += (s, e) =>
            {
                if (state.IsColorPicking)
                {
                    PreviewColorAt(form, pb, state, e.Location);
                }
            };

            void HandleZoom(MouseEventArgs e)
            {
                if (state.IsAnnotating)
                {
                    return;
                }
                const float step = 0.1f;
                float minScale = state.MinScale;
                float maxScale = state.MaxScale;

                var newScale = state.Scale + (e.Delta > 0 ? step : -step);
                newScale = Math.Max(minScale, Math.Min(maxScale, newScale));
                if (Math.Abs(newScale - state.Scale) < 0.0001f) return;

                state.Scale = newScale;
                ApplyScale(form, pb, state);
                ClampToScreen(form);
            }

            pb.MouseWheel += (s, e) => HandleZoom(e);
            form.MouseWheel += (s, e) => HandleZoom(e);
            form.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    form.Close();
                    return;
                }
            };
            form.Shown += (s, e) => SyncLayout();
            form.ClientSizeChanged += (s, e) =>
            {
                if (isSyncing) return;
                // 标注模式下窗体尺寸包含工具栏，不能按图片大小回缩
                if (state.IsAnnotating) return;
                if (form.ClientSize != pb.Size)
                {
                    SyncLayout();
                }
            };
            form.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    StartFormDrag(form);
                }
            };

            form.FormClosed += (s, e) =>
            {
                (pb.Image as Bitmap)?.Dispose();
                state.PickerToolTip.Dispose();
            };
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
            string saveDir = imagePath;
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
                waitForm.Close();
                
                if (e.Error != null)
                {
                    MessageBox.Show($"识别失败: {e.Error.Message}");
                    return;
                }

                try
                {
                    var json = JObject.Parse((string)e.Result);
                    var words = json["words_result"]
                        .Select(item => item["words"].ToString())
                        .Where(word => !string.IsNullOrWhiteSpace(word))
                        .ToArray();

                    string combinedText = string.Join(Environment.NewLine, words);
                    
                    // 通过全局上下文创建 note 窗体，保证窗体计数与生命周期一致
                    var newNote = Program.AppContext.AddNewForm();

                    // 将识别结果插入到新窗体
                    newNote.SetText(combinedText);
                    
                    // 复制到剪贴板
                    if (!string.IsNullOrEmpty(combinedText))
                    {
                        Clipboard.SetText(combinedText);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"处理结果时出错: {ex.Message}");
                }
            };

            waitForm.Show();
            worker.RunWorkerAsync();
        }

        private static void ApplyScale(Form form, ScaledPictureBox pb, ScreenshotState state)
        {
            form.SuspendLayout();
            pb.SetScale(state.Scale);
            pb.Location = Point.Empty;
            form.ClientSize = pb.Size;
            form.ResumeLayout();
        }

        private static void ResetZoom(Form form)
        {
            var pb = GetPictureBox(form);
            var state = form.Tag as ScreenshotState;
            if (pb == null || state == null) return;

            state.Scale = 1.0f;
            ApplyScale(form, pb, state);
            ClampToScreen(form);
        }

        private static void StartColorPick(Form form)
        {
            var pb = GetPictureBox(form);
            var state = form.Tag as ScreenshotState;
            if (pb == null || state == null) return;

            state.IsColorPicking = true;
            pb.Cursor = Cursors.Cross;
        }

        private static void PickColorAt(Form form, ScaledPictureBox pb, ScreenshotState state, Point location)
        {
            if (!(pb.Image is Bitmap bmp)) return;

            int x = (int)Math.Floor(location.X / Math.Max(0.0001f, state.Scale));
            int y = (int)Math.Floor(location.Y / Math.Max(0.0001f, state.Scale));
            x = Math.Max(0, Math.Min(x, bmp.Width - 1));
            y = Math.Max(0, Math.Min(y, bmp.Height - 1));

            Color c = bmp.GetPixel(x, y);
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            string text = $"{hex}  RGB({c.R},{c.G},{c.B})";

            Clipboard.SetText(hex);
            state.PickerToolTip.Show(
                text,
                form,
                form.PointToClient(Cursor.Position),
                1200);

            state.IsColorPicking = false;
            pb.Cursor = Cursors.Default;
        }

        private static void PreviewColorAt(Form form, ScaledPictureBox pb, ScreenshotState state, Point location)
        {
            if (!(pb.Image is Bitmap bmp)) return;

            int x = (int)Math.Floor(location.X / Math.Max(0.0001f, state.Scale));
            int y = (int)Math.Floor(location.Y / Math.Max(0.0001f, state.Scale));
            x = Math.Max(0, Math.Min(x, bmp.Width - 1));
            y = Math.Max(0, Math.Min(y, bmp.Height - 1));

            Color c = bmp.GetPixel(x, y);
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            string text = $"{hex}  RGB({c.R},{c.G},{c.B})";

            state.PickerToolTip.Show(
                text,
                form,
                form.PointToClient(Cursor.Position),
                200);
        }

        private static void ClampToScreen(Form form)
        {
            var screen = Screen.FromRectangle(new Rectangle(form.Location, form.Size));

            int x = form.Left;
            int y = form.Top;

            if (form.Width >= screen.Bounds.Width)
            {
                x = screen.Bounds.Left;
            }
            else
            {
                x = Math.Min(Math.Max(form.Left, screen.Bounds.Left), screen.Bounds.Right - form.Width);
            }

            if (form.Height >= screen.Bounds.Height)
            {
                y = screen.Bounds.Top;
            }
            else
            {
                y = Math.Min(Math.Max(form.Top, screen.Bounds.Top), screen.Bounds.Bottom - form.Height);
            }

            form.Location = new Point(x, y);
        }

        // 添加马赛克效果方法
        private static void ApplyMosaicEffect(Bitmap bitmap, int blockSize)
        {
            if (blockSize <= 1) return;

            // 对每个马赛克块进行处理
            for (int y = 0; y < bitmap.Height; y += blockSize)
            {
                for (int x = 0; x < bitmap.Width; x += blockSize)
                {
                    // 计算实际块大小（处理图像边缘）
                    int currentBlockWidth = Math.Min(blockSize, bitmap.Width - x);
                    int currentBlockHeight = Math.Min(blockSize, bitmap.Height - y);

                    if (currentBlockWidth <= 0 || currentBlockHeight <= 0) continue;

                    // 计算块内平均颜色
                    Color avgColor = CalculateAverageColor(bitmap, x, y, currentBlockWidth, currentBlockHeight);

                    // 用平均颜色填充整个块
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        using (SolidBrush brush = new SolidBrush(avgColor))
                        {
                            g.FillRectangle(brush, x, y, currentBlockWidth, currentBlockHeight);
                        }
                    }
                }
            }
        }

        // 计算指定区域的平均颜色
        private static Color CalculateAverageColor(Bitmap bitmap, int startX, int startY, int width, int height)
        {
            int totalPixels = width * height;
            int r = 0, g = 0, b = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (startX + x < bitmap.Width && startY + y < bitmap.Height)
                    {
                        Color pixel = bitmap.GetPixel(startX + x, startY + y);
                        r += pixel.R;
                        g += pixel.G;
                        b += pixel.B;
                    }
                }
            }

            // 计算平均值
            r /= totalPixels;
            g /= totalPixels;
            b /= totalPixels;

            return Color.FromArgb(r, g, b);
        }

        // 将马赛克应用到区域
        private static void ApplyMosaicToArea(Graphics g, Rectangle area, Image originalImage)
        {
            // 截取该区域
            using (Bitmap areaBitmap = new Bitmap(area.Width, area.Height))
            {
                using (Graphics areaG = Graphics.FromImage(areaBitmap))
                {
                    areaG.DrawImage(
                        originalImage,
                        new Rectangle(0, 0, area.Width, area.Height),
                        area,
                        GraphicsUnit.Pixel);
                }

                // 应用马赛克效果 - 马赛克块大小为10像素
                ApplyMosaicEffect(areaBitmap, 10);

                // 将处理后的区域绘制回原始位置
                g.DrawImage(areaBitmap, area);
            }
        }
    }



}
