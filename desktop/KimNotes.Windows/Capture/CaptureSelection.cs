using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;

namespace KimNotes.Windows.Capture
{
    // Coordinates for region selection stay in physical desktop pixels, including negative origins.
    internal sealed class CaptureSelection : IDisposable
    {
        [Flags]
        private enum DragEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

        private ScreenSnapshot snapshot;
        private readonly IReadOnlyList<CaptureDisplay> displays;
        private readonly List<SelectionOverlay> overlays = new List<SelectionOverlay>();
        private readonly Action<BitmapSource, DrawingRect, CaptureTool, Action> complete;
        private Action<CaptureTool> editorInvoke;
        private DrawingRect area;
        private DrawingRect initial;
        private DrawingPoint origin;
        private SelectionOverlay dragOverlay;
        private DragEdge edge;
        private bool submitting;
        private bool closed;

        internal CaptureSelection(
            IReadOnlyList<CaptureDisplay> displays,
            Action<BitmapSource, DrawingRect, CaptureTool, Action> complete)
        {
            this.displays = displays;
            this.complete = complete;
        }

        internal void Prepare()
        {
            // The grab worker can read the desktop while these HWNDs are still hidden.
            foreach (var display in displays)
                overlays.Add(new SelectionOverlay(this, display));
        }

        internal void Show(ScreenSnapshot snapshot)
        {
            if (closed) return;
            this.snapshot = snapshot;
            foreach (var overlay in overlays) overlay.SetFrame(snapshot.MonitorImage(overlay.Display.Bounds));
            bool staged = false;
            for (int i = 0; i < overlays.Count; i++)
            {
                if (overlays[i].Stage()) staged = true;
                else UseTransparentFallback(i);
            }
            // Loaded runs after WPF has submitted the initial layout and drawing pass for every monitor.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => _ = RevealWhenReadyAsync(staged)));
        }

        private async Task RevealWhenReadyAsync(bool staged)
        {
            if (closed) return;
            try
            {
                if (staged)
                    await Task.WhenAny(Task.Run(SelectionOverlay.WaitForDesktopFrame), Task.Delay(80));
                if (!closed && !submitting) Reveal();
            }
            catch (Exception error)
            {
                Program.LogError(error);
                Dispose();
                MessageBox.Show(error.Message, "无法截图", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Reveal()
        {
            var current = displays[0];
            try { current = CaptureScreen.DisplayNear(CaptureScreen.Cursor, displays); }
            catch (InvalidOperationException) { } // Input can be unavailable if the desktop just changed.
            for (int i = 0; i < overlays.Count; i++)
            {
                if (overlays[i].Reveal()) continue;
                UseTransparentFallback(i).Reveal();
            }
            overlays.First(view => ReferenceEquals(view.Display, current)).Activate();
        }

        private SelectionOverlay UseTransparentFallback(int index)
        {
            // Transparency keeps an unpainted HWND invisible when DWM cannot cloak it.
            var previous = overlays[index];
            var fallback = new SelectionOverlay(this, previous.Display, transparentFallback: true);
            overlays[index] = fallback;
            previous.Dispose();
            fallback.SetFrame(snapshot.MonitorImage(fallback.Display.Bounds));
            return fallback;
        }

        private void Down(SelectionOverlay overlay, MouseButtonEventArgs args)
        {
            if (submitting) return;
            var point = overlay.PointerPixel(args);
            if (args.ClickCount == 2 && area.Contains(point))
            {
                Submit(CaptureTool.Edit);
                return;
            }

            origin = point;
            initial = area;
            edge = Hit(point, overlay);
            if (edge == DragEdge.None) area = DrawingRect.Empty;
            if (overlay.Surface.CaptureMouse()) dragOverlay = overlay;
            if (!overlay.Surface.IsKeyboardFocused) overlay.Surface.Focus();
            Redraw();
        }

        private void Move(SelectionOverlay overlay, MouseEventArgs args)
        {
            if (submitting) return;
            var point = overlay.PointerPixel(args);
            if (dragOverlay != null)
                Update(point);
            else
            {
                var cursor = CursorFor(Hit(point, overlay));
                if (overlay.Surface.Cursor != cursor) overlay.Surface.Cursor = cursor;
            }
        }

        private void Up(SelectionOverlay overlay, MouseButtonEventArgs args)
        {
            if (submitting || !ReferenceEquals(dragOverlay, overlay)) return;
            Update(overlay.PointerPixel(args));
            dragOverlay = null;
            overlay.Surface.ReleaseMouseCapture();
            Redraw();
        }

        private void Update(DrawingPoint point)
        {
            var bounds = snapshot.Bounds;
            DrawingRect updated;
            if (edge == DragEdge.Move)
            {
                int left = Clamp(initial.Left + point.X - origin.X, bounds.Left, bounds.Right - initial.Width);
                int top = Clamp(initial.Top + point.Y - origin.Y, bounds.Top, bounds.Bottom - initial.Height);
                updated = new DrawingRect(left, top, initial.Width, initial.Height);
            }
            else if (edge == DragEdge.None)
                updated = point == origin ? DrawingRect.Empty : DrawingRect.Intersect(bounds, DrawingRect.FromLTRB(
                    Math.Min(origin.X, point.X), Math.Min(origin.Y, point.Y),
                    Math.Max(origin.X, point.X) + 1, Math.Max(origin.Y, point.Y) + 1));
            else
            {
                int left = initial.Left, top = initial.Top, right = initial.Right, bottom = initial.Bottom;
                if (edge.HasFlag(DragEdge.Left)) left = Clamp(point.X, bounds.Left, right - 1);
                if (edge.HasFlag(DragEdge.Top)) top = Clamp(point.Y, bounds.Top, bottom - 1);
                if (edge.HasFlag(DragEdge.Right)) right = Clamp(point.X + 1, left + 1, bounds.Right);
                if (edge.HasFlag(DragEdge.Bottom)) bottom = Clamp(point.Y + 1, top + 1, bounds.Bottom);
                updated = DrawingRect.FromLTRB(left, top, right, bottom);
            }
            if (area == updated) return;
            area = updated;
            Redraw();
        }

        private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(value, high));

        private DragEdge Hit(DrawingPoint point, SelectionOverlay overlay)
        {
            if (area.Width <= 0 || area.Height <= 0) return DragEdge.None;
            var dpi = VisualTreeHelper.GetDpi(overlay.Surface);
            int margin = (int)Math.Ceiling(6 * dpi.DpiScaleX);
            var outer = area;
            outer.Inflate(margin, margin);
            if (!outer.Contains(point)) return DragEdge.None;

            var horizontal = Math.Abs(point.X - area.Left) <= margin ? DragEdge.Left :
                Math.Abs(point.X - area.Right) <= margin ? DragEdge.Right : DragEdge.None;
            var vertical = Math.Abs(point.Y - area.Top) <= margin ? DragEdge.Top :
                Math.Abs(point.Y - area.Bottom) <= margin ? DragEdge.Bottom : DragEdge.None;
            return horizontal != DragEdge.None || vertical != DragEdge.None
                ? horizontal | vertical : area.Contains(point) ? DragEdge.Move : DragEdge.None;
        }

        private static Cursor CursorFor(DragEdge part)
        {
            switch (part)
            {
                case DragEdge.Left | DragEdge.Top:
                case DragEdge.Right | DragEdge.Bottom: return Cursors.SizeNWSE;
                case DragEdge.Right | DragEdge.Top:
                case DragEdge.Left | DragEdge.Bottom: return Cursors.SizeNESW;
                case DragEdge.Top:
                case DragEdge.Bottom: return Cursors.SizeNS;
                case DragEdge.Left:
                case DragEdge.Right: return Cursors.SizeWE;
                case DragEdge.Move: return Cursors.SizeAll;
                default: return Cursors.Cross;
            }
        }

        private void Redraw()
        {
            var target = area.Width == 0 || area.Height == 0 || dragOverlay != null ? null :
                CaptureScreen.DisplayNear(new DrawingPoint(area.Right - 1, area.Bottom - 1), displays);
            foreach (var overlay in overlays)
                overlay.ShowArea(area, ReferenceEquals(overlay.Display, target));
        }

        private void Submit(CaptureTool choice)
        {
            if (closed) return;
            if (editorInvoke != null)
            {
                editorInvoke(choice);
                return;
            }
            if (choice == CaptureTool.Cancel)
            {
                Dispose();
                return;
            }
            if (submitting) return;
            if (area.Width <= 0 || area.Height <= 0) return;
            submitting = true;
            var selectedArea = area;
            var mouseCapture = dragOverlay;
            dragOverlay = null;
            if (mouseCapture?.Surface.IsMouseCaptured == true) mouseCapture.Surface.ReleaseMouseCapture();
            // Crossing monitors or saving a large area can copy tens of megabytes.
            var crop = Task.Run(() => snapshot.Copy(selectedArea));
            _ = CompleteAsync(crop, selectedArea, choice);
        }

        internal bool AttachEditor(Action<CaptureTool> invoke, out CaptureToolbar toolbar, out DrawingRect toolbarBounds, out IntPtr owner)
        {
            toolbar = null;
            toolbarBounds = DrawingRect.Empty;
            owner = IntPtr.Zero;
            if (closed || !submitting || editorInvoke != null) return false;
            foreach (var overlay in overlays)
            {
                if (!overlay.TryGetToolbar(out toolbar, out toolbarBounds, out owner)) continue;
                editorInvoke = invoke;
                return true;
            }
            return false;
        }

        private async Task CompleteAsync(Task<BitmapSource> crop, DrawingRect selectedArea, CaptureTool choice)
        {
            BitmapSource image;
            try
            {
                image = await crop;
            }
            catch (Exception error)
            {
                if (closed) return;
                Dispose();
                MessageBox.Show(error.Message, "截图未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (closed) return;
            // Leave the composed monitor frames visible until the next surface has opened.
            complete(image, selectedArea, choice, Stop);
        }

        public void Dispose()
        {
            if (closed) return;
            // The editor must detach its native owner before these monitor HWNDs are destroyed.
            closed = true;
            editorInvoke = null;
            try { complete(null, DrawingRect.Empty, CaptureTool.Cancel, CloseOverlays); }
            finally { CloseOverlays(); }
        }

        private void Stop()
        {
            if (closed) return;
            closed = true;
            editorInvoke = null;
            CloseOverlays();
        }

        private void CloseOverlays()
        {
            dragOverlay = null;
            foreach (var overlay in overlays)
                if (overlay.Surface.IsMouseCaptured) overlay.Surface.ReleaseMouseCapture();
            foreach (var overlay in overlays) overlay.Dispose();
            overlays.Clear();
        }

        private static HwndSourceParameters BuildOverlayParameters(DrawingRect bounds)
        {
            const int Popup = unchecked((int)0x80000000);
            const int TopmostToolWindow = 0x00000008 | 0x00000080;
            // HWND creation accepts physical screen coordinates and picks the DPI for this monitor.
            var parameters = new HwndSourceParameters("选取截图区域", bounds.Width, bounds.Height)
            {
                WindowStyle = Popup,
                ExtendedWindowStyle = TopmostToolWindow
            };
            parameters.SetPosition(bounds.Left, bounds.Top);
            return parameters;
        }

        private sealed class SelectionOverlay : IDisposable
        {
            private const int DisableTransitions = 3;
            private const int Cloak = 13;
            private readonly HwndSource source;
            private readonly Grid root;
            private readonly Image backdrop;
            private readonly Canvas toolHost;
            private readonly Action<CaptureTool> submit;
            private CaptureToolbar toolbar;
            private DrawingRect selected;
            private bool showTools;
            private bool staged;

            internal SelectionOverlay(CaptureSelection selection, CaptureDisplay display, bool transparentFallback = false)
            {
                Display = display;
                submit = selection.Submit;
                root = new Grid { ClipToBounds = true, SnapsToDevicePixels = true };
                backdrop = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(backdrop, BitmapScalingMode.NearestNeighbor);
                root.Children.Add(backdrop);
                Surface = new SelectionSurface(display.Bounds);
                root.Children.Add(Surface);
                toolHost = new Canvas { Visibility = Visibility.Collapsed };
                root.Children.Add(toolHost);
                root.SizeChanged += (sender, args) => PlaceTools();

                Surface.MouseLeftButtonDown += (sender, args) => { selection.Down(this, args); args.Handled = true; };
                Surface.MouseMove += (sender, args) => selection.Move(this, args);
                Surface.MouseLeftButtonUp += (sender, args) => { selection.Up(this, args); args.Handled = true; };
                Surface.LostMouseCapture += (sender, args) =>
                {
                    if (!ReferenceEquals(selection.dragOverlay, this)) return;
                    selection.dragOverlay = null;
                    selection.Redraw();
                };
                root.PreviewMouseRightButtonDown += (sender, args) => { args.Handled = true; selection.Submit(CaptureTool.Cancel); };
                root.PreviewKeyDown += (sender, args) =>
                {
                    if (args.Key == Key.Escape) { args.Handled = true; selection.Submit(CaptureTool.Cancel); }
                    if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && !(args.OriginalSource is Button))
                    {
                        args.Handled = true;
                        selection.Submit(CaptureTool.Confirm);
                    }
                };
                var parameters = BuildOverlayParameters(display.Bounds);
                parameters.UsesPerPixelOpacity = transparentFallback;
                using (new CaptureScreen.PhysicalDpiScope()) source = new HwndSource(parameters);
                try
                {
                    source.AddHook((IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        const int CloseMessage = 0x0010;
                        const int DisplayChangeMessage = 0x007E;
                        if ((message == CloseMessage || message == DisplayChangeMessage) && !selection.closed)
                        {
                            handled = message == CloseMessage;
                            source.Dispatcher.BeginInvoke(new Action(selection.Dispose));
                        }
                        return IntPtr.Zero;
                    });
                    SetDwmFlag(DisableTransitions, true);
                }
                catch
                {
                    source.Dispose();
                    throw;
                }
            }

            internal CaptureDisplay Display { get; }
            internal SelectionSurface Surface { get; }

            internal void SetFrame(BitmapSource frame)
            {
                // Bind the frozen pixels before the visual can submit any frame to its HWND.
                backdrop.Source = frame;
                source.RootVisual = root;
                var transform = source.CompositionTarget.TransformFromDevice;
                var size = transform.Transform(new Vector(Display.Bounds.Width, Display.Bounds.Height));
                var layout = new Size(size.X, size.Y);
                root.Measure(layout);
                root.Arrange(new Rect(layout));
            }

            [DllImport("user32.dll")]
            private static extern bool ShowWindow(IntPtr window, int command);

            [DllImport("user32.dll")]
            private static extern bool SetForegroundWindow(IntPtr window);

            [DllImport("dwmapi.dll")]
            private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

            [DllImport("dwmapi.dll")]
            private static extern int DwmFlush();

            private bool SetDwmFlag(int attribute, bool enabled)
            {
                int value = enabled ? 1 : 0;
                return DwmSetWindowAttribute(source.Handle, attribute, ref value, sizeof(int)) >= 0;
            }

            internal bool Stage()
            {
                // Cloaked HWNDs still get composed, but their initial backing surfaces stay hidden.
                staged = SetDwmFlag(Cloak, true);
                if (staged) ShowWindow(source.Handle, 4); // SW_SHOWNOACTIVATE
                return staged;
            }

            internal static void WaitForDesktopFrame() => DwmFlush();

            internal bool Reveal()
            {
                if (staged) return SetDwmFlag(Cloak, false);
                ShowWindow(source.Handle, 4); // SW_SHOWNOACTIVATE
                return true;
            }

            internal DrawingPoint PointerPixel(MouseEventArgs args)
            {
                var point = Surface.PointToScreen(args.GetPosition(Surface));
                return new DrawingPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
            }

            internal void Activate()
            {
                SetForegroundWindow(source.Handle);
                Surface.Focus();
            }

            public void Dispose() => source.Dispose();

            internal bool TryGetToolbar(out CaptureToolbar control, out DrawingRect bounds, out IntPtr owner)
            {
                control = null;
                bounds = DrawingRect.Empty;
                owner = IntPtr.Zero;
                if (!showTools || toolbar == null) return false;
                root.UpdateLayout();
                if (toolbar.ActualWidth <= 0 || toolbar.ActualHeight <= 0) return false;
                var start = toolbar.PointToScreen(new Point());
                var end = toolbar.PointToScreen(new Point(toolbar.ActualWidth, toolbar.ActualHeight));
                control = toolbar;
                bounds = DrawingRect.FromLTRB((int)Math.Round(start.X), (int)Math.Round(start.Y),
                    (int)Math.Round(end.X), (int)Math.Round(end.Y));
                owner = source.Handle;
                return true;
            }

            internal void ShowArea(DrawingRect area, bool withTools)
            {
                Surface.ShowArea(area);
                if (selected == area && showTools == withTools) return;
                selected = area;
                showTools = withTools;
                var visibility = showTools ? Visibility.Visible : Visibility.Collapsed;
                if (toolHost.Visibility != visibility) toolHost.Visibility = visibility;
                if (showTools) PlaceTools();
            }

            private void PlaceTools()
            {
                if (!showTools || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0) return;
                if (toolbar == null)
                {
                    toolbar = new CaptureToolbar(submit, false);
                    toolHost.Children.Add(toolbar);
                }
                var local = Surface.ToLocal(selected);
                toolbar.MaxWidth = Math.Max(1, Surface.ActualWidth - 12);
                toolbar.Measure(new Size(toolbar.MaxWidth, double.PositiveInfinity));
                var size = toolbar.DesiredSize;
                double x = Math.Max(6, Math.Min(local.Right - size.Width, Surface.ActualWidth - size.Width - 6));
                double y = local.Bottom + 8 + size.Height <= Surface.ActualHeight
                    ? local.Bottom + 6 : Math.Max(6, local.Bottom - size.Height - 6);
                Canvas.SetLeft(toolbar, x);
                Canvas.SetTop(toolbar, y);
            }
        }

        private sealed class SelectionSurface : FrameworkElement
        {
            private static readonly Brush Shade = FrozenBrush(Color.FromArgb(124, 12, 18, 28));
            private static readonly Brush BorderBrush = FrozenBrush(Color.FromRgb(56, 128, 214));
            private static readonly Pen Outline = CreateOutline();
            private static readonly Typeface LabelTypeface = new Typeface("Segoe UI");
            private readonly DrawingRect bounds;
            private readonly DrawingVisual drawingLayer = new DrawingVisual();
            private DrawingRect selection;
            private DispatcherOperation renderRequest;
            private DrawingRect labelDimensions;
            private double labelPixelsPerDip;
            private FormattedText label;

            internal SelectionSurface(DrawingRect bounds)
            {
                this.bounds = bounds;
                Focusable = true;
                Cursor = Cursors.Cross;
                AddVisualChild(drawingLayer);
            }

            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => index == 0 ? drawingLayer : throw new ArgumentOutOfRangeException(nameof(index));

            internal void ShowArea(DrawingRect area)
            {
                var visible = DrawingRect.Intersect(bounds, area);
                var updated = visible.Width > 0 && visible.Height > 0 ? area : DrawingRect.Empty;
                if (selection == updated) return;
                selection = updated;
                if (renderRequest == null)
                    renderRequest = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Paint));
            }

            internal Rect ToLocal(DrawingRect rect)
            {
                double x = ActualWidth / bounds.Width;
                double y = ActualHeight / bounds.Height;
                return new Rect((rect.Left - bounds.Left) * x, (rect.Top - bounds.Top) * y,
                    rect.Width * x, rect.Height * y);
            }

            protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
            {
                base.OnRenderSizeChanged(sizeInfo);
                // Size is arranged before the prepaint. Later pointer updates keep layout untouched.
                renderRequest?.Abort();
                Paint();
            }

            protected override HitTestResult HitTestCore(PointHitTestParameters hit)
            {
                return new Rect(RenderSize).Contains(hit.HitPoint) ? new PointHitTestResult(this, hit.HitPoint) : null;
            }

            private void Paint()
            {
                renderRequest = null;
                using (var drawing = drawingLayer.RenderOpen()) PaintArea(drawing);
            }

            private void PaintArea(DrawingContext drawing)
            {
                var viewport = new Rect(RenderSize);
                var visible = DrawingRect.Intersect(bounds, selection);
                if (visible.Width <= 0 || visible.Height <= 0)
                {
                    drawing.DrawRectangle(Shade, null, viewport);
                    return;
                }

                var hole = ToLocal(visible);
                drawing.DrawRectangle(Brushes.Transparent, null, hole);
                DrawShade(drawing, 0, 0, viewport.Width, hole.Top);
                DrawShade(drawing, 0, hole.Bottom, viewport.Width, viewport.Bottom - hole.Bottom);
                DrawShade(drawing, 0, hole.Top, hole.Left, hole.Height);
                DrawShade(drawing, hole.Right, hole.Top, viewport.Right - hole.Right, hole.Height);

                var local = ToLocal(selection);
                DrawOutline(drawing, hole, visible);
                for (int x = 0; x < 3; x++)
                    for (int y = 0; y < 3; y++)
                        if (x != 1 || y != 1)
                            drawing.DrawRectangle(Brushes.White, Outline,
                                new Rect(local.Left + x * local.Width / 2 - 2.5, local.Top + y * local.Height / 2 - 2.5, 5, 5));

                if (bounds.Contains(selection.Location))
                {
                    PaintLabel(drawing, local);
                }
            }

            private void PaintLabel(DrawingContext drawing, Rect local)
            {
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                if (label == null || labelDimensions.Width != selection.Width || labelDimensions.Height != selection.Height || labelPixelsPerDip != dpi)
                {
                    label = new FormattedText(selection.Width + " × " + selection.Height,
                        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 12, Brushes.White, dpi);
                    labelDimensions = selection;
                    labelPixelsPerDip = dpi;
                }

                var start = new Point(local.Left + 6, (local.Top > label.Height + 10 ? local.Top - label.Height - 7 : local.Top + 5));
                drawing.DrawRoundedRectangle(Shade, null, new Rect(start.X - 4, start.Y - 2, label.Width + 8, label.Height + 4), 3, 3);
                drawing.DrawText(label, start);
            }

            private void DrawOutline(DrawingContext drawing, Rect hole, DrawingRect visible)
            {
                // Keep the outline inside the crop, including the outermost pixel of a monitor.
                double vertical = Math.Min(1.5, hole.Width);
                double horizontal = Math.Min(1.5, hole.Height);
                if (visible.Left == selection.Left)
                    drawing.DrawRectangle(BorderBrush, null, new Rect(hole.Left, hole.Top, vertical, hole.Height));
                if (visible.Right == selection.Right)
                    drawing.DrawRectangle(BorderBrush, null, new Rect(hole.Right - vertical, hole.Top, vertical, hole.Height));
                if (visible.Top == selection.Top)
                    drawing.DrawRectangle(BorderBrush, null, new Rect(hole.Left, hole.Top, hole.Width, horizontal));
                if (visible.Bottom == selection.Bottom)
                    drawing.DrawRectangle(BorderBrush, null, new Rect(hole.Left, hole.Bottom - horizontal, hole.Width, horizontal));
            }

            private static void DrawShade(DrawingContext drawing, double x, double y, double width, double height)
            {
                if (width > 0 && height > 0) drawing.DrawRectangle(Shade, null, new Rect(x, y, width, height));
            }

            private static Brush FrozenBrush(Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }

            private static Pen CreateOutline()
            {
                var pen = new Pen(FrozenBrush(Color.FromRgb(86, 159, 246)), 1);
                pen.Freeze();
                return pen;
            }
        }
    }
}
