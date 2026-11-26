using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using KimNotes.Windows.Capture;
using KimNotes.Windows.Notes;
using WinForms = System.Windows.Forms;

namespace KimNotes.Windows.Todos
{
    internal static class TodoEditorPlacement
    {
        private const uint MoveOnlyWithoutActivation = 0x0015;

        internal static void PlaceByAnchor(Window dialog, Window anchor, bool beside)
        {
            var ownerHandle = new WindowInteropHelper(anchor).Handle;
            var dialogHandle = new WindowInteropHelper(dialog).Handle;
            if (ownerHandle == IntPtr.Zero || dialogHandle == IntPtr.Zero) return;

            using (new CaptureScreen.PhysicalDpiScope())
            {
                if (!NotePlacement.GetWindowRect(ownerHandle, out var bounds)) return;
                var area = WinForms.Screen.FromHandle(ownerHandle).WorkingArea;
                var dpi = VisualTreeHelper.GetDpi(anchor);
                double width = dialog.Width * dpi.DpiScaleX;
                double height = dialog.Height * dpi.DpiScaleY;
                if (NotePlacement.GetWindowRect(dialogHandle, out var actualBounds))
                {
                    width = Math.Max(width, actualBounds.Right - actualBounds.Left);
                    height = Math.Max(height, actualBounds.Bottom - actualBounds.Top);
                }
                var size = new Size(width, height);
                var ownerRect = new Rect(new Point(bounds.Left, bounds.Top), new Point(bounds.Right, bounds.Bottom));
                var workArea = new Rect(area.X, area.Y, area.Width, area.Height);
                var position = beside ? GetPosition(ownerRect, workArea, size, 12 * dpi.DpiScaleX, 30 * dpi.DpiScaleY) :
                    new Point(Clamp(ownerRect.Left + (ownerRect.Width - width) / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - width)),
                        Clamp(ownerRect.Top + (ownerRect.Height - height) / 2, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height)));
                // The dialog already has its requested size. Move in native pixels before its first paint.
                NotePlacement.SetWindowPos(dialogHandle, IntPtr.Zero, (int)Math.Round(position.X), (int)Math.Round(position.Y),
                    0, 0, MoveOnlyWithoutActivation);
            }
        }

        internal static Point GetPosition(Rect owner, Rect workArea, Size dialog, double gap, double fallbackOffset)
        {
            double right = owner.Right + gap;
            double left = owner.Left - gap - dialog.Width;
            double maxX = Math.Max(workArea.Left, workArea.Right - dialog.Width);
            double maxY = Math.Max(workArea.Top, workArea.Bottom - dialog.Height);
            double y = Clamp(owner.Top, workArea.Top, maxY);

            if (right <= maxX && right >= workArea.Left) return new Point(right, y);
            if (left >= workArea.Left && left <= maxX) return new Point(left, y);

            bool moreRoomOnRight = workArea.Right - owner.Right >= owner.Left - workArea.Left;
            double fallbackX = Clamp(moreRoomOnRight ? right : left, workArea.Left, maxX);
            double fallbackY = Clamp(owner.Top + fallbackOffset, workArea.Top, maxY);
            if (fallbackY == y) fallbackY = Clamp(owner.Top - fallbackOffset, workArea.Top, maxY);
            return new Point(fallbackX, fallbackY);
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));
    }
}
