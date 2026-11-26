using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KimNotes.Windows.Runtime;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace KimNotes.Windows.Capture
{
    internal sealed class PinnedImageWindow : Window
    {
        private const double TextBoxLinePadding = 2;
        private const double TextLeftInset = 6 + TextBoxLinePadding;
        private const double TextTopInset = 5;
        private const double TextCaretRoom = 2;
        private const string TextHelp = "Enter 完成，Shift+Enter 换行，Esc 取消，Ctrl+滚轮调整字号";
        private readonly Action<PinnedImageWindow, CaptureTool> use;
        private readonly Grid imagePanel;
        private readonly Image bitmapView;
        private readonly AnnotationSurface preview;
        private readonly Dictionary<CaptureTool, CaptureAnnotationStyle> annotationStyles =
            new Dictionary<CaptureTool, CaptureAnnotationStyle>();
        private FloatingCaptureToolbar toolbar;
        private CaptureAnnotationPalette palette;
        private bool toolbarRequested;
        private CaptureToolbar selectionToolbar;
        private bool toolbarClipActive;
        private readonly DispatcherTimer resizeTimer;
        private readonly ToolTip colorHint = new ToolTip();
        private CaptureAnnotationDocument document;
        private TextBox typing;
        private bool composingText;
        private CaptureTool? editing;
        private bool drawing;
        private Pen gesturePen;
        private bool fillGesture;
        private List<Point> brushPoints;
        private bool moving;
        private bool ready;
        private bool refreshingSize;
        private DrawingRect positionedBounds;
        private DpiScale positionedDpi;
        private HwndSource host;
        private DrawingPoint dragStart;
        private DrawingPoint dragAnchor;
        private DrawingPoint dragCursor;
        private Point origin;
        private Point textAnchor;
        private double zoom = 1;
        private double displayedZoom = 1;
        private int wheelDelta;

        internal PinnedImageWindow(BitmapSource image, bool incognito, bool previewOnly, Action<PinnedImageWindow, CaptureTool> use)
        {
            this.use = use;
            Incognito = incognito;
            PreviewOnly = previewOnly;
            Title = "贴图";
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Width = 30;
            Height = 30;
            Background = new SolidColorBrush(Color.FromRgb(234, 240, 247));
            UseLayoutRounding = true;
            var body = new Grid { ClipToBounds = true };
            Content = body;
            imagePanel = new Grid
            {
                ClipToBounds = true,
                Focusable = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };
            body.Children.Add(imagePanel);
            bitmapView = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(bitmapView, BitmapScalingMode.NearestNeighbor);
            imagePanel.Children.Add(bitmapView);
            preview = new AnnotationSurface { IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.NearestNeighbor);
            imagePanel.Children.Add(preview);
            imagePanel.Children.Add(new ImageFrame { IsHitTestVisible = false });
            colorHint.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
            colorHint.PlacementTarget = imagePanel;
            resizeTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            resizeTimer.Tick += (sender, args) => ApplyPendingResize();

            imagePanel.MouseLeftButtonDown += BeginGesture;
            imagePanel.MouseMove += MoveGesture;
            imagePanel.MouseLeftButtonUp += EndGesture;
            imagePanel.LostMouseCapture += (sender, args) =>
            {
                if (moving) FinishMove();
                if (drawing) ClearGesture();
            };
            imagePanel.ContextMenu = BuildMenu();
            imagePanel.PreviewMouseRightButtonDown += (sender, args) =>
            {
                if (!editing.HasValue && selectionToolbar == null) return;
                args.Handled = true;
                if (!editing.HasValue) { InvokeTool(CaptureTool.Cancel); return; }
                if (CommitText()) EndMode();
            };
            MouseWheel += OnZoom;
            PreviewKeyDown += OnViewerKey;
            Closed += (sender, args) =>
            {
                ready = false;
                moving = false;
                resizeTimer.Stop();
                toolbar?.CloseWithOwner();
                colorHint.IsOpen = false;
                if (host != null)
                {
                    host.DpiChanged -= KeepRenderScale;
                    if (!host.IsDisposed) host.RemoveHook(KeepPhysicalSize);
                }
            };
            Replace(image, null);
        }

        internal bool Incognito { get; private set; }
        internal bool PreviewOnly { get; }
        internal bool HistorySuppressed { get; private set; }
        internal bool IsCaptureEditor { get; set; }
        internal string HistoryId { get; set; }
        internal bool HistoryChanged { get; set; }
        internal bool BusyEditing => editing.HasValue || drawing || typing != null;
        internal event Action<PinnedImageWindow, string> ColorPicked;
        private CaptureToolbar ActiveToolbar => selectionToolbar ?? toolbar?.Tools;

        internal void Open(DrawingPoint anchor)
        {
            var display = CaptureScreen.DisplayNear(anchor, CaptureScreen.GetDisplays()).WorkArea;
            var initial = new DrawingRect(
                Math.Max(display.Left, Math.Min(anchor.X, display.Right - 30)),
                Math.Max(display.Top, Math.Min(anchor.Y, display.Bottom - 30)), 30, 30);
            PrepareHost(initial);
            ResizeFor(anchor, display);
            ShowAndFocus();
        }

        internal void OpenInSelection(DrawingRect bounds, CaptureToolbar sharedToolbar, DrawingRect toolbarBounds, IntPtr owner)
        {
            selectionToolbar = sharedToolbar ?? throw new ArgumentNullException(nameof(sharedToolbar));
            new WindowInteropHelper(this).Owner = owner;
            PrepareHost(new DrawingRect(bounds.Left, bounds.Top, 1, 1));

            var transform = host.CompositionTarget.TransformToDevice;
            positionedDpi = new DpiScale(transform.M11, transform.M22);
            positionedBounds = bounds;
            ApplyImageSize(bounds.Size, positionedDpi);
            Width = bounds.Width / positionedDpi.DpiScaleX;
            Height = bounds.Height / positionedDpi.DpiScaleY;
            CaptureScreen.Position(this, bounds);
            LeaveToolbarVisible(bounds, toolbarBounds);
            ShowAndFocus();
        }

        internal void DetachSelectionToolbar()
        {
            if (selectionToolbar == null && !toolbarClipActive) return;
            palette?.Hide();
            selectionToolbar = null;
            if (host == null || host.IsDisposed)
            {
                toolbarClipActive = false;
                return;
            }
            new WindowInteropHelper(this).Owner = IntPtr.Zero;
            if (toolbarClipActive)
            {
                if (SetWindowRgn(host.Handle, IntPtr.Zero, true) == 0)
                    throw new InvalidOperationException("无法恢复贴图的完整边框。");
                toolbarClipActive = false;
            }
        }

        private void PrepareHost(DrawingRect initial)
        {
            CaptureScreen.Position(this, initial);
            host = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            host.DpiChanged += KeepRenderScale;
            host.AddHook(KeepPhysicalSize);
            ready = true;
        }

        private void ShowAndFocus()
        {
            Show();
            Activate();
            imagePanel.Focus();
            FollowToolbar();
        }

        private void LeaveToolbarVisible(DrawingRect bounds, DrawingRect toolbarBounds)
        {
            var overlap = LocalToolbarOverlap(bounds, toolbarBounds);
            if (overlap.IsEmpty) return;

            var region = CreateRectRgn(0, 0, bounds.Width, bounds.Height);
            var toolbarRegion = CreateRectRgn(overlap.Left, overlap.Top, overlap.Right, overlap.Bottom);
            try
            {
                const int RegionDifference = 4;
                if (region == IntPtr.Zero || toolbarRegion == IntPtr.Zero ||
                    CombineRgn(region, region, toolbarRegion, RegionDifference) == 0 ||
                    SetWindowRgn(host.Handle, region, false) == 0)
                    throw new InvalidOperationException("无法为截图标注保留工具栏空间。");
                region = IntPtr.Zero; // Windows owns the installed region.
                toolbarClipActive = true;
            }
            finally
            {
                if (region != IntPtr.Zero) DeleteObject(region);
                if (toolbarRegion != IntPtr.Zero) DeleteObject(toolbarRegion);
            }
        }

        private static DrawingRect LocalToolbarOverlap(DrawingRect bounds, DrawingRect toolbarBounds)
        {
            var overlap = DrawingRect.Intersect(bounds, toolbarBounds);
            if (overlap.Width <= 0 || overlap.Height <= 0) return DrawingRect.Empty;
            overlap.Offset(-bounds.Left, -bounds.Top);
            return overlap;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int combineMode);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

        private static void KeepRenderScale(object sender, HwndDpiChangedEventArgs args)
        {
            // The bitmap and its HWND share the chosen physical size throughout a move.
            args.Handled = true;
        }

        private static IntPtr KeepPhysicalSize(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int GetDpiScaledSize = 0x02E4;
            if (message != GetDpiScaledSize) return IntPtr.Zero;
            // Windows initializes the pending size to the current physical size.
            handled = true;
            return new IntPtr(1);
        }

        internal void Replace(BitmapSource image, string id)
        {
            EndMode();
            IsCaptureEditor = false;
            document = new CaptureAnnotationDocument(image);
            bitmapView.Source = document.Source;
            preview.SetDocument(document);
            HistoryId = id;
            HistoryChanged = id == null;
            zoom = 1;
            wheelDelta = 0;
            RefreshAnnotations();
            if (ready) ResizeFor(CurrentAnchor());
        }

        internal void CommitPendingAnnotation() => CommitText();

        internal BitmapSource ImageForAction()
        {
            CommitText();
            return document.Render();
        }

        internal bool TryApplyTranslation(BitmapSource original, BitmapSource translated)
        {
            if (!IsVisible || drawing || typing != null || translated == null ||
                translated.PixelWidth != document.Source.PixelWidth || translated.PixelHeight != document.Source.PixelHeight ||
                !ReferenceEquals(document.Render(), original))
                return false;
            document.AddReplacement(translated);
            HistoryChanged = true;
            RefreshAnnotations();
            return true;
        }

        internal void SuppressHistory()
        {
            HistorySuppressed = true;
            EndMode();
        }

        internal void MoveToPrivateHistory()
        {
            if (Incognito || PreviewOnly) return;
            Incognito = true;
            HistoryId = null;
            HistoryChanged = true;
        }

        internal void InvokeTool(CaptureTool tool)
        {
            if (tool != CaptureTool.Cancel && !CommitText()) return;
            switch (tool)
            {
                case CaptureTool.Edit: SetToolbar(true); break;
                case CaptureTool.Rectangle:
                case CaptureTool.Arrow:
                case CaptureTool.Mosaic:
                case CaptureTool.Text:
                case CaptureTool.Pen:
                case CaptureTool.PickColor:
                    var hasOptions = CaptureAnnotationOptions.Supports(tool);
                    EndMode(hidePalette: !hasOptions);
                    SetToolbar(true);
                    editing = tool;
                    ActiveToolbar.Select(tool);
                    imagePanel.Cursor = tool == CaptureTool.Text ? Cursors.IBeam : Cursors.Cross;
                    if (selectionToolbar != null && ready)
                    {
                        Activate();
                        imagePanel.Focus();
                    }
                    if (hasOptions && ready && IsVisible)
                    {
                        if (palette == null) palette = new CaptureAnnotationPalette(this, ApplyAnnotationStyle);
                        palette.Present(ActiveToolbar, tool, AnnotationStyle(tool));
                    }
                    break;
                case CaptureTool.Undo:
                    if (!document.CanUndo) break;
                    document.Undo();
                    HistoryChanged = true;
                    RefreshAnnotations();
                    break;
                case CaptureTool.Cancel:
                    if (typing != null || editing.HasValue) EndMode();
                    else Close();
                    break;
                case CaptureTool.Pin:
                    EndMode();
                    SetToolbar(false);
                    use(this, tool);
                    break;
                default:
                    use(this, tool);
                    break;
            }
        }

        private ContextMenu BuildMenu()
        {
            var commands = new List<(string Label, Action Run)>();
            if (!PreviewOnly)
            {
                commands.Add(("工具栏", () => InvokeTool(CaptureTool.Edit)));
                commands.Add(("插入便签", () => InvokeTool(CaptureTool.Insert)));
            }
            commands.Add(("复制", () => InvokeTool(CaptureTool.Copy)));
            commands.Add(("另存图片", () => InvokeTool(CaptureTool.Save)));
            if (!PreviewOnly) commands.Add(("文字识别", () => InvokeTool(CaptureTool.Ocr)));
            commands.Add(("重置大小", () =>
            {
                zoom = 1;
                wheelDelta = 0;
                ResizeFor(CurrentAnchor());
            }));
            var history = PreviewOnly ? Array.Empty<(string Label, Action Run)>() : new (string Label, Action Run)[]
            {
                ("上一个贴图", () => InvokeTool(CaptureTool.Previous)),
                ("下一个贴图", () => InvokeTool(CaptureTool.Next))
            };
            var closing = new List<(string Label, Action Run)> { ("关闭贴图", Close) };
            if (!PreviewOnly) closing.Add(("销毁贴图", () => InvokeTool(CaptureTool.Destroy)));
            return DesktopTrayMenu.CreateGrouped(commands.ToArray(), history, closing.ToArray());
        }

        private void SetToolbar(bool visible)
        {
            if (selectionToolbar != null) return;
            toolbarRequested = visible;
            if (!visible)
            {
                if (toolbar?.IsVisible == true) toolbar.Hide();
                return;
            }
            if (toolbar == null) toolbar = new FloatingCaptureToolbar(InvokeTool, document.CanUndo);
            FollowToolbar();
        }

        private void FollowToolbar()
        {
            if (toolbarRequested && ready && IsVisible) toolbar.Follow(this, positionedBounds);
            palette?.Follow();
        }

        private CaptureAnnotationStyle AnnotationStyle(CaptureTool tool) =>
            annotationStyles.TryGetValue(tool, out var style) ? style : CaptureAnnotationStyle.Default;

        private void ApplyAnnotationStyle(CaptureAnnotationStyle style)
        {
            if (!editing.HasValue || !CaptureAnnotationOptions.Supports(editing.Value)) return;
            annotationStyles[editing.Value] = style;
            if (editing != CaptureTool.Text || typing == null) return;
            ApplyTextStyle(typing, style);
            ResizeTextEditor();
        }

        private static void ApplyTextStyle(TextBox field, CaptureAnnotationStyle style)
        {
            field.CaretBrush = field.Foreground = CaptureAnnotationDocument.CreateInk(style.Color);
            field.FontSize = style.FontSize;
        }

        private void RefreshAnnotations()
        {
            preview.InvalidateVisual();
            if (ActiveToolbar != null) ActiveToolbar.CanUndo = document.CanUndo;
        }

        private void BeginGesture(object sender, MouseButtonEventArgs args)
        {
            if (typing != null)
            {
                if (typing.IsMouseOver) return;
                if (!CommitText()) { args.Handled = true; return; }
            }
            if (!editing.HasValue)
            {
                if (selectionToolbar != null) return;
                if (args.ClickCount == 2)
                {
                    if (PreviewOnly || Incognito) Close();
                    else InvokeTool(CaptureTool.SaveDefault);
                }
                else BeginMove();
                args.Handled = true;
                return;
            }

            imagePanel.Focus();
            if (resizeTimer.IsEnabled) ApplyPendingResize();
            var local = args.GetPosition(imagePanel);
            var point = ToSource(local);
            if (editing == CaptureTool.PickColor)
            {
                var color = document.ColorAt(point);
                ColorPicked?.Invoke(this, color);
                EndMode();
            }
            else if (editing == CaptureTool.Text)
                BeginText(local);
            else
            {
                if (imagePanel.CaptureMouse()) BeginDrawing(editing.Value, point);
            }
            args.Handled = true;
        }

        private void BeginDrawing(CaptureTool tool, Point point)
        {
            origin = point;
            drawing = true;
            var style = AnnotationStyle(tool);
            gesturePen = CaptureAnnotationDocument.CreatePen(style.Color, style.StrokeWidth);
            fillGesture = style.Filled;
            if (tool == CaptureTool.Pen)
            {
                brushPoints = new List<Point> { point };
                preview.SetStroke(brushPoints, gesturePen);
            }
            else preview.Set(tool, origin, point, gesturePen, fillGesture);
        }

        private void MoveGesture(object sender, MouseEventArgs args)
        {
            if (moving)
            {
                MoveWithCursor();
                if (args.LeftButton != MouseButtonState.Pressed) FinishMove();
                args.Handled = true;
                return;
            }
            if (!editing.HasValue) return;
            var point = ToSource(args.GetPosition(imagePanel));
            if (drawing && args.LeftButton == MouseButtonState.Pressed)
                UpdateDrawing(editing.Value, point);
            else if (editing == CaptureTool.PickColor)
            {
                string hex = document.ColorAt(point);
                var color = (Color)ColorConverter.ConvertFromString(hex);
                colorHint.Content = $"{hex}  RGB({color.R},{color.G},{color.B})";
                colorHint.IsOpen = true;
            }
        }

        private void UpdateDrawing(CaptureTool tool, Point point)
        {
            if (brushPoints == null) preview.Set(tool, origin, point, gesturePen, fillGesture);
            else if (AppendBrushPoint(point)) preview.SetStroke(brushPoints, gesturePen);
        }

        private bool AppendBrushPoint(Point point)
        {
            if ((point - brushPoints[brushPoints.Count - 1]).LengthSquared < 0.25) return false;
            brushPoints.Add(point);
            return true;
        }

        private void EndGesture(object sender, MouseButtonEventArgs args)
        {
            if (moving)
            {
                MoveWithCursor();
                FinishMove();
                args.Handled = true;
                return;
            }
            if (!drawing || !editing.HasValue) return;
            FinishDrawing(editing.Value, ToSource(args.GetPosition(imagePanel)));
            args.Handled = true;
        }

        private void FinishDrawing(CaptureTool tool, Point point)
        {
            var points = brushPoints;
            var pen = gesturePen;
            var filled = fillGesture;
            if (points != null) AppendBrushPoint(point);
            ClearGesture();
            if (imagePanel.IsMouseCaptured) imagePanel.ReleaseMouseCapture();
            if (points != null) document.AddStroke(points, pen);
            else if (!document.Add(tool, origin, point, pen, filled)) return;
            HistoryChanged = true;
            RefreshAnnotations();
        }

        private void ClearGesture()
        {
            drawing = false;
            brushPoints = null;
            gesturePen = null;
            fillGesture = false;
            preview.Clear();
        }

        private void BeginMove()
        {
            if (!ready) return;
            dragStart = dragCursor = CaptureScreen.Cursor;
            dragAnchor = CurrentAnchor();
            if (!imagePanel.CaptureMouse()) return;
            resizeTimer.Stop();
            moving = true;
        }

        private void MoveWithCursor()
        {
            dragCursor = CaptureScreen.Cursor;
            var position = DragTarget(dragAnchor, dragStart, dragCursor);
            if (position != positionedBounds.Location && CaptureScreen.Move(host.Handle, position))
            {
                positionedBounds.Location = position;
                FollowToolbar();
            }
        }

        private void FinishMove()
        {
            moving = false;
            if (imagePanel.IsMouseCaptured) imagePanel.ReleaseMouseCapture();
            if (!ready || !IsVisible) return;
            if (dragStart != dragCursor)
            {
                var displays = CaptureScreen.GetDisplays();
                var origin = CaptureScreen.DisplayNear(dragStart, displays);
                var destination = CaptureScreen.DisplayNear(dragCursor, displays);
                if (origin != destination)
                {
                    ResizeFor(CurrentAnchor(), destination.WorkArea, preserveDisplayedScale: true);
                    return;
                }
            }
            if (wheelDelta != 0) resizeTimer.Start();
        }

        private static DrawingPoint DragTarget(DrawingPoint anchor, DrawingPoint start, DrawingPoint cursor) =>
            new DrawingPoint(anchor.X + cursor.X - start.X, anchor.Y + cursor.Y - start.Y);

        private Point ToSource(Point window)
        {
            double x = window.X * document.Source.PixelWidth / Math.Max(1, imagePanel.ActualWidth);
            double y = window.Y * document.Source.PixelHeight / Math.Max(1, imagePanel.ActualHeight);
            return new Point(Math.Max(0, Math.Min(x, document.Source.PixelWidth)),
                Math.Max(0, Math.Min(y, document.Source.PixelHeight)));
        }

        private void BeginText(Point local)
        {
            if (imagePanel.ActualWidth <= 2 * TextLeftInset || imagePanel.ActualHeight <= 2 * TextTopInset) return;
            textAnchor = local;
            var style = AnnotationStyle(CaptureTool.Text);
            var field = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                FontFamily = CaptureAnnotationDocument.TextFont,
                Background = Brushes.Transparent,
                Padding = new Thickness(TextLeftInset - TextBoxLinePadding, TextTopInset,
                    TextLeftInset - TextBoxLinePadding, TextTopInset),
                Template = TextEditorTemplate,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = TextHelp
            };
            ApplyTextStyle(field, style);
            System.Windows.Automation.AutomationProperties.SetName(field, "截图文字标注");
            System.Windows.Automation.AutomationProperties.SetHelpText(field, TextHelp);
            TextOptions.SetTextFormattingMode(field, TextFormattingMode.Ideal);
            field.PreviewMouseWheel += (sender, args) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                AdjustTextSize(args.Delta);
                args.Handled = true;
            };
            field.PreviewKeyDown += (sender, args) => args.Handled = HandleTextKey(args.Key, Keyboard.Modifiers);
            TextCompositionManager.AddPreviewTextInputStartHandler(field, (sender, args) => ChangeTextComposition(field, true));
            TextCompositionManager.AddPreviewTextInputUpdateHandler(field, (sender, args) => ChangeTextComposition(field, true));
            TextCompositionManager.AddPreviewTextInputHandler(field, (sender, args) => ChangeTextComposition(field, false));
            field.LostKeyboardFocus += (sender, args) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!field.IsKeyboardFocusWithin) ChangeTextComposition(field, false);
            }));
            field.TextChanged += (sender, args) => ResizeTextEditor();
            typing = field;
            ResizeTextEditor();
            imagePanel.Children.Add(field);
            field.Focus();
        }

        private void AdjustTextSize(int wheelDelta)
        {
            if (typing == null || wheelDelta == 0) return;
            var current = AnnotationStyle(CaptureTool.Text);
            double size = Math.Max(8, Math.Min(72, current.FontSize + Math.Sign(wheelDelta) * 2));
            if (size == current.FontSize) return;
            var style = new CaptureAnnotationStyle(current.Color, current.StrokeWidth, size, current.Filled);
            ApplyAnnotationStyle(style);
            palette?.SetTextStyle(style);
        }

        private bool HandleTextKey(Key key, ModifierKeys modifiers)
        {
            if (typing == null || composingText || key == Key.ImeProcessed) return false;
            if (key == Key.Escape)
            {
                RemoveText();
                imagePanel.Focus();
                return true;
            }
            if (key != Key.Enter) return false;

            if (modifiers == ModifierKeys.Shift)
            {
                int start = typing.SelectionStart;
                typing.SelectedText = Environment.NewLine;
                typing.Select(start + Environment.NewLine.Length, 0);
                return true;
            }
            if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Control) return false;
            if (CommitText()) imagePanel.Focus();
            return true;
        }

        private void ChangeTextComposition(TextBox field, bool active)
        {
            if (typing == field) composingText = active;
        }

        private void ResizeTextEditor()
        {
            if (typing == null) return;
            var viewport = imagePanel.RenderSize;
            double available = Math.Max(1, viewport.Width - 2 * TextLeftInset);
            var text = CaptureAnnotationDocument.MeasureText(typing.Text, typing.FontSize, available);
            double width = Math.Max(22, Math.Ceiling(text.Width) + 2 * TextLeftInset + TextCaretRoom);
            double height = Math.Ceiling(text.Height) + 2 * TextTopInset;
            var bounds = TextEditorBounds(textAnchor, viewport, new Size(width, height));
            typing.Width = bounds.Width;
            typing.Height = bounds.Height;
            typing.Margin = new Thickness(bounds.Left, bounds.Top, 0, 0);
        }

        private static Rect TextEditorBounds(Point anchor, Size viewport, Size desired)
        {
            double width = Math.Min(viewport.Width, desired.Width);
            double height = Math.Min(viewport.Height, desired.Height);
            double left = Math.Max(0, Math.Min(anchor.X - TextLeftInset, viewport.Width - width));
            double top = Math.Max(0, Math.Min(anchor.Y - TextTopInset, viewport.Height - height));
            return new Rect(left, top, width, height);
        }

        private bool CommitText()
        {
            if (typing == null) return true;
            if (composingText) return false;
            var text = typing.Text;
            double fontSize = typing.FontSize;
            var foreground = typing.Foreground;
            var at = ToSource(new Point(typing.Margin.Left + TextLeftInset, typing.Margin.Top + TextTopInset));
            var xScale = document.Source.PixelWidth / Math.Max(1, imagePanel.ActualWidth);
            double width = Math.Max(1, (typing.Width - 2 * TextLeftInset) * xScale);
            RemoveText();
            if (string.IsNullOrWhiteSpace(text)) return true;
            document.AddText(at, text, fontSize * xScale, width, foreground);
            HistoryChanged = true;
            RefreshAnnotations();
            return true;
        }

        private static readonly ControlTemplate TextEditorTemplate = CreateTextEditorTemplate();

        private static ControlTemplate CreateTextEditorTemplate()
        {
            var root = new FrameworkElementFactory(typeof(Grid));
            var input = new FrameworkElementFactory(typeof(Border));
            input.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            input.AppendChild(new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"));
            root.AppendChild(input);
            root.AppendChild(new FrameworkElementFactory(typeof(CaptureTextOutline)));
            return new ControlTemplate(typeof(TextBox)) { VisualTree = root };
        }

        private void RemoveText()
        {
            if (typing == null) return;
            var field = typing;
            typing = null;
            composingText = false;
            imagePanel.Children.Remove(field);
        }

        private void EndMode(bool hidePalette = true)
        {
            RemoveText();
            editing = null;
            ActiveToolbar?.Select(null);
            if (hidePalette) palette?.Hide();
            ClearGesture();
            if (imagePanel.IsMouseCaptured) imagePanel.ReleaseMouseCapture();
            imagePanel.Cursor = Cursors.Arrow;
            colorHint.IsOpen = false;
        }

        private void OnZoom(object sender, MouseWheelEventArgs args)
        {
            if (selectionToolbar != null || BusyEditing || args.Delta == 0) return;
            wheelDelta += args.Delta;
            if (!moving) resizeTimer.Start();
            args.Handled = true;
        }

        private static double WheelZoom(double scale, int delta) =>
            Math.Max(0.15, Math.Min(4, scale * Math.Pow(1.1, delta / 120d)));

        private void ApplyPendingResize()
        {
            resizeTimer.Stop();
            if (ready && IsVisible && !moving) ResizeFor(CurrentAnchor());
        }

        private void OnViewerKey(object sender, KeyEventArgs args)
        {
            if (typing != null) return;
            if (IsCaptureEditor && !drawing && args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
                !(args.OriginalSource is Button))
            {
                args.Handled = true;
                InvokeTool(CaptureTool.Confirm);
                return;
            }
            if (args.Key == Key.Escape || (selectionToolbar == null && !BusyEditing && !PreviewOnly && (args.Key == Key.Left || args.Key == Key.Right)) ||
                ((args.Key == Key.C || args.Key == Key.Z) && Keyboard.Modifiers == ModifierKeys.Control))
            {
                args.Handled = true;
                if (args.Key == Key.Escape) InvokeTool(CaptureTool.Cancel);
                else if (args.Key == Key.Left) InvokeTool(CaptureTool.Previous);
                else if (args.Key == Key.Right) InvokeTool(CaptureTool.Next);
                else InvokeTool(args.Key == Key.Z ? CaptureTool.Undo : CaptureTool.Copy);
            }
        }

        private DrawingPoint CurrentAnchor()
        {
            var origin = PointToScreen(new Point());
            return new DrawingPoint((int)Math.Round(origin.X), (int)Math.Round(origin.Y));
        }

        private void ResizeFor(DrawingPoint anchor, DrawingRect? workArea = null, bool preserveDisplayedScale = false)
        {
            if (!ready || refreshingSize) return;
            refreshingSize = true;
            try
            {
                var display = workArea ?? CaptureScreen.DisplayNear(anchor, CaptureScreen.GetDisplays()).WorkArea;
                var transform = host.CompositionTarget.TransformToDevice;
                var dpi = new DpiScale(transform.M11, transform.M22);
                var source = new DrawingSize(document.Source.PixelWidth, document.Source.PixelHeight);
                var fit = FitZoom(display, source);
                if (preserveDisplayedScale) zoom = Math.Min(displayedZoom, fit);
                if (wheelDelta != 0)
                {
                    zoom = FittedWheelZoom(zoom, wheelDelta, fit);
                    wheelDelta = 0;
                }
                displayedZoom = Math.Min(zoom, fit);
                var bounds = ScaledBounds(display, anchor, source, zoom);
                ApplyImageSize(bounds.Size, dpi);

                // Keep the cached WPF startup size in sync while the HWND is still hidden.
                if (!IsVisible)
                {
                    Width = bounds.Width / dpi.DpiScaleX;
                    Height = bounds.Height / dpi.DpiScaleY;
                }
                else if (bounds == positionedBounds && anchor == bounds.Location &&
                    Math.Abs(ActualWidth * dpi.DpiScaleX - bounds.Width) < 1 &&
                    Math.Abs(ActualHeight * dpi.DpiScaleY - bounds.Height) < 1 &&
                    dpi.DpiScaleX == positionedDpi.DpiScaleX && dpi.DpiScaleY == positionedDpi.DpiScaleY)
                {
                    FollowToolbar();
                    return;
                }

                // Apply both axes and position in one native operation. WM_SIZE updates WPF's size.
                CaptureScreen.Position(this, bounds);
                positionedBounds = bounds;
                positionedDpi = dpi;
                FollowToolbar();
            }
            finally { refreshingSize = false; }
        }

        private void ApplyImageSize(DrawingSize physical, DpiScale dpi)
        {
            imagePanel.Width = physical.Width / dpi.DpiScaleX;
            imagePanel.Height = physical.Height / dpi.DpiScaleY;
        }

        private static DrawingRect ScaledBounds(DrawingRect display, DrawingPoint anchor, DrawingSize source, double zoom)
        {
            double scale = Math.Min(zoom, FitZoom(display, source));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            int left = Math.Max(display.Left, Math.Min(anchor.X, display.Right - width));
            int top = Math.Max(display.Top, Math.Min(anchor.Y, display.Bottom - height));
            return new DrawingRect(left, top, width, height);
        }

        private static double FitZoom(DrawingRect display, DrawingSize source) =>
            Math.Min(display.Width / (double)source.Width, display.Height / (double)source.Height);

        // Start from the visible size so extra scrolling at the monitor's limit cannot build up.
        private static double FittedWheelZoom(double scale, int delta, double fit) =>
            Math.Min(fit, WheelZoom(Math.Min(scale, fit), delta));

        private sealed class ImageFrame : FrameworkElement
        {
            private static readonly Brush Outside = FrameBrush(Color.FromArgb(154, 38, 48, 62));
            private static readonly Brush Inside = FrameBrush(Color.FromArgb(110, 255, 255, 255));

            protected override void OnRender(DrawingContext drawing)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                double x = Math.Min(1 / dpi.DpiScaleX, ActualWidth / 2);
                double y = Math.Min(1 / dpi.DpiScaleY, ActualHeight / 2);
                if (x <= 0 || y <= 0) return;
                var frame = new Rect(RenderSize);
                DrawFrame(drawing, Outside, frame, x, y);
                if (frame.Width < 4 * x || frame.Height < 4 * y) return;
                frame.Inflate(-x, -y);
                DrawFrame(drawing, Inside, frame, x, y);
            }

            private static void DrawFrame(DrawingContext drawing, Brush brush, Rect frame, double x, double y)
            {
                drawing.DrawRectangle(brush, null, new Rect(frame.Left, frame.Top, frame.Width, y));
                drawing.DrawRectangle(brush, null, new Rect(frame.Left, frame.Bottom - y, frame.Width, y));
                drawing.DrawRectangle(brush, null, new Rect(frame.Left, frame.Top + y, x, frame.Height - 2 * y));
                drawing.DrawRectangle(brush, null, new Rect(frame.Right - x, frame.Top + y, x, frame.Height - 2 * y));
            }

            private static Brush FrameBrush(Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }

        private sealed class AnnotationSurface : FrameworkElement
        {
            private CaptureAnnotationDocument document;
            private CaptureTool action;
            private Point start;
            private Point end;
            private Pen pen;
            private bool filled;
            private IReadOnlyList<Point> stroke;
            private bool drawingGesture;

            internal void SetDocument(CaptureAnnotationDocument value)
            {
                document = value;
                stroke = null;
                drawingGesture = false;
                InvalidateVisual();
            }

            internal void Set(CaptureTool tool, Point origin, Point current, Pen outline, bool fill)
            {
                if (drawingGesture && stroke == null && action == tool && start == origin && end == current &&
                    pen == outline && filled == fill) return;
                action = tool;
                start = origin;
                end = current;
                pen = outline;
                filled = fill;
                stroke = null;
                drawingGesture = true;
                InvalidateVisual();
            }

            internal void SetStroke(IReadOnlyList<Point> points, Pen outline)
            {
                stroke = points;
                pen = outline;
                drawingGesture = true;
                InvalidateVisual();
            }

            internal void Clear()
            {
                if (!drawingGesture) return;
                drawingGesture = false;
                stroke = null;
                pen = null;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawing)
            {
                if (document == null || (!drawingGesture && !document.CanUndo)) return;
                var bounds = document.Bounds;
                drawing.PushTransform(new ScaleTransform(ActualWidth / bounds.Width, ActualHeight / bounds.Height));
                document.DrawMarks(drawing);
                if (stroke != null) CaptureAnnotationDocument.DrawStroke(drawing, stroke, pen);
                else if (drawingGesture) CaptureAnnotationDocument.DrawGesture(drawing, action, start, end, pen, filled);
                drawing.Pop();
            }
        }
    }
}
