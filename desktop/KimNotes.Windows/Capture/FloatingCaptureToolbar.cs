using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace KimNotes.Windows.Capture
{
    internal sealed class FloatingCaptureToolbar : Window
    {
        private HwndSource source;
        private IReadOnlyList<CaptureDisplay> displays;
        private DrawingRect positioned;
        private DrawingSize cardSize;
        private int measuredWidth;
        private bool closed;

        internal FloatingCaptureToolbar(Action<CaptureTool> use, bool canUndo)
        {
            Title = "截图工具栏";
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            Width = Height = 1;
            Tools = new CaptureToolbar(use, canUndo);
            Content = Tools;
            Closed += (sender, args) => closed = true;
        }

        internal CaptureToolbar Tools { get; }

        internal void Follow(Window owner, DrawingRect image)
        {
            if (closed) return;
            if (Owner == null) Owner = owner;
            if (!IsVisible || displays == null) displays = CaptureScreen.GetDisplays();
            var center = new DrawingPoint(image.Left + image.Width / 2, image.Top + image.Height / 2);
            var workArea = CaptureScreen.DisplayNear(center, displays).WorkArea;
            if (source == null)
            {
                CaptureScreen.Position(this, new DrawingRect(workArea.Left, workArea.Top, 1, 1));
                source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                source.DpiChanged += (sender, args) => args.Handled = true;
                source.AddHook(HandleMessage);
            }

            var dpi = source.CompositionTarget.TransformToDevice;
            if (cardSize.IsEmpty || measuredWidth != workArea.Width)
            {
                measuredWidth = workArea.Width;
                Tools.Measure(new Size(measuredWidth / dpi.M11, double.PositiveInfinity));
                cardSize = new DrawingSize(Math.Min(workArea.Width, (int)Math.Ceiling(Tools.DesiredSize.Width * dpi.M11)),
                    Math.Min(workArea.Height, (int)Math.Ceiling(Tools.DesiredSize.Height * dpi.M22)));
            }

            var bounds = Place(image, cardSize, workArea, (int)Math.Round(8 * dpi.M22));
            if (!IsVisible)
            {
                Width = bounds.Width / dpi.M11;
                Height = bounds.Height / dpi.M22;
                CaptureScreen.Position(this, bounds);
                Show();
            }
            else if (bounds == positioned) return;
            else if (bounds.Size != positioned.Size) CaptureScreen.Position(this, bounds);
            else if (!CaptureScreen.Move(source.Handle, bounds.Location)) return;
            positioned = bounds;
        }

        internal void CloseWithOwner()
        {
            if (!closed) Close();
        }

        private static DrawingRect Place(DrawingRect image, DrawingSize toolbar, DrawingRect workArea, int gap)
        {
            int left = Math.Max(workArea.Left, Math.Min(image.Right - toolbar.Width, workArea.Right - toolbar.Width));
            int top = image.Bottom + gap;
            if (top + toolbar.Height > workArea.Bottom) top = image.Top - gap - toolbar.Height;
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - toolbar.Height));
            return new DrawingRect(left, top, toolbar.Width, toolbar.Height);
        }

        internal static IntPtr HandleMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0021) // WM_MOUSEACTIVATE
            {
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE keeps keyboard input on the image.
            }
            if (message == 0x02E4) // WM_GETDPISCALEDSIZE
            {
                handled = true;
                return new IntPtr(1);
            }
            return IntPtr.Zero;
        }
    }
}
