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
            // Get the screen's DPI settings for high-DPI screens
            var dpiX = 96; // Default DPI
            var dpiY = 96;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                dpiX = (int)g.DpiX;
                dpiY = (int)g.DpiY;
            }

            // Adjust the capture size based on the DPI
            var screenshot = new Bitmap(area.Width * dpiX / 96, area.Height * dpiY / 96);
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
            using (var g = Graphics.FromImage(originalImage))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;  // Anti-aliasing for smoother lines
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;  // High-quality scaling
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;  // High compositing quality
            }
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
                        // 应用双重缩放补偿
                        g.ScaleTransform(scale, scale);
                        g.TranslateTransform(pos.X, pos.Y);
                        g.DrawString(text, font, Brushes.Red, Point.Empty);
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
                SizeMode = PictureBoxSizeMode.AutoSize,  // AutoSize instead of StretchImage
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



}