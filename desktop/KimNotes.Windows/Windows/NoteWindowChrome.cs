using System;
using System.Windows;
using System.Windows.Interop;

namespace KimNotes.Windows.Presentation
{
    internal sealed class NoteWindowChrome
    {
        private const int HitTestMessage = 0x0084;
        private const int GetDpiScaledSizeMessage = 0x02E4;
        private const double ResizeMargin = 5;
        private const int Left = 10;
        private const int Right = 11;
        private const int Top = 12;
        private const int TopLeft = 13;
        private const int TopRight = 14;
        private const int Bottom = 15;
        private const int BottomLeft = 16;
        private const int BottomRight = 17;

        private readonly Window window;
        private HwndSource source;

        internal NoteWindowChrome(Window window)
        {
            this.window = window;
            window.SourceInitialized += Connect;
            window.Closed += Disconnect;
        }

        private void Connect(object sender, EventArgs e)
        {
            source = (HwndSource)PresentationSource.FromVisual(window);
            source.DpiChanged += PreserveRenderScale;
            source.AddHook(HandleMessage);
        }

        private void Disconnect(object sender, EventArgs e)
        {
            if (source == null) return;
            source.DpiChanged -= PreserveRenderScale;
            if (!source.IsDisposed) source.RemoveHook(HandleMessage);
        }

        private static void PreserveRenderScale(object sender, HwndDpiChangedEventArgs e)
        {
            // Use the render scale chosen when the card opens throughout its lifetime.
            e.Handled = true;
        }

        private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == GetDpiScaledSizeMessage)
            {
                // The incoming size already represents the pending move or edge resize.
                handled = true;
                return new IntPtr(1);
            }

            if (message != HitTestMessage || window.WindowState != WindowState.Normal ||
                window.ResizeMode == ResizeMode.NoResize || window.ResizeMode == ResizeMode.CanMinimize)
                return IntPtr.Zero;

            long coordinates = lParam.ToInt64();
            var screenPoint = new Point(unchecked((short)coordinates), unchecked((short)(coordinates >> 16)));
            var point = window.PointFromScreen(screenPoint);
            int edge = ResizeEdge(point, window.ActualWidth, window.ActualHeight);
            if (edge == 0) return IntPtr.Zero;
            handled = true;
            return new IntPtr(edge);
        }

        private static int ResizeEdge(Point point, double width, double height)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height) return 0;
            bool left = point.X < ResizeMargin;
            bool right = width - point.X <= ResizeMargin;
            if (point.Y < ResizeMargin) return left ? TopLeft : right ? TopRight : Top;
            if (height - point.Y <= ResizeMargin) return left ? BottomLeft : right ? BottomRight : Bottom;
            if (left) return Left;
            return right ? Right : 0;
        }
    }
}
