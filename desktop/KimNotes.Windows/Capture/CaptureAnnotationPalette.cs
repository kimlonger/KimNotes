using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureAnnotationPalette : Window
    {
        private readonly CaptureAnnotationOptions options = new CaptureAnnotationOptions();
        private CaptureToolbar toolbar;
        private Button anchor;
        private HwndSource source;
        private DrawingRect positioned;

        internal CaptureAnnotationPalette(Window owner, Action<CaptureAnnotationStyle> apply)
        {
            Owner = owner;
            Title = "标注选项";
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            Width = Height = 1;
            Content = options;
            options.Changed += apply;
        }

        internal void Present(CaptureToolbar tools, CaptureTool tool, CaptureAnnotationStyle style)
        {
            toolbar = tools;
            anchor = tools.GetToolButton(tool);
            options.Bind(tool, style);
            if (Position() && !IsVisible) Show();
        }

        internal void Follow()
        {
            if (IsVisible) Position();
        }

        internal void SetTextStyle(CaptureAnnotationStyle style) => options.Bind(CaptureTool.Text, style);

        private bool Position()
        {
            if (anchor == null || PresentationSource.FromVisual(anchor) == null) return false;
            if (!anchor.IsArrangeValid) toolbar.UpdateLayout();
            var button = ScreenBounds(anchor);
            var bar = ScreenBounds(toolbar);
            if (button.Width == 0 || button.Height == 0 || bar.Width == 0 || bar.Height == 0) return false;

            var center = new DrawingPoint(button.Left + button.Width / 2, button.Top + button.Height / 2);
            var workArea = CaptureScreen.DisplayNear(center, CaptureScreen.GetDisplays()).WorkArea;
            if (source == null)
            {
                CaptureScreen.Position(this, new DrawingRect(workArea.Left, workArea.Top, 1, 1));
                source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                source.DpiChanged += (sender, args) => args.Handled = true;
                source.AddHook(FloatingCaptureToolbar.HandleMessage);
            }

            var dpi = source.CompositionTarget.TransformToDevice;
            double available = workArea.Width / dpi.M11;
            options.MaxWidth = available;
            options.Measure(new Size(available, double.PositiveInfinity));
            var size = new DrawingSize(
                Math.Min(workArea.Width, (int)Math.Ceiling(options.DesiredSize.Width * dpi.M11)),
                Math.Min(workArea.Height, (int)Math.Ceiling(options.DesiredSize.Height * dpi.M22)));
            var bounds = Place(button, bar, size, workArea, (int)Math.Round(4 * dpi.M22));
            if (bounds == positioned && IsVisible) return true;
            if (bounds.Size != positioned.Size)
            {
                Width = bounds.Width / dpi.M11;
                Height = bounds.Height / dpi.M22;
            }
            CaptureScreen.Position(this, bounds);
            positioned = bounds;
            return true;
        }

        private static DrawingRect ScreenBounds(FrameworkElement element)
        {
            var start = element.PointToScreen(new Point());
            var end = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return DrawingRect.FromLTRB((int)Math.Round(start.X), (int)Math.Round(start.Y),
                (int)Math.Round(end.X), (int)Math.Round(end.Y));
        }

        private static DrawingRect Place(DrawingRect anchor, DrawingRect toolbar, DrawingSize palette, DrawingRect workArea, int gap)
        {
            int left = anchor.Left + (anchor.Width - palette.Width) / 2;
            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - palette.Width));
            int below = toolbar.Bottom + gap;
            int above = toolbar.Top - gap - palette.Height;
            int top = below;
            if (below + palette.Height > workArea.Bottom && (above >= workArea.Top || toolbar.Top - workArea.Top > workArea.Bottom - toolbar.Bottom))
                top = above;
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - palette.Height));
            return new DrawingRect(left, top, palette.Width, palette.Height);
        }
    }
}
