using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KimNotes.Windows.Capture;
using DrawingPoint = System.Drawing.Point;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow
    {
        private UIElement dragSurface;
        private IntPtr dragHandle;
        private DrawingPoint dragStart;
        private DrawingPoint dragOrigin;
        private DrawingPoint dragPosition;

        private bool IsMovingNote => dragSurface != null;

        private void TitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            if (IsMovingNote || WindowState != WindowState.Normal || args.ClickCount != 1 ||
                args.ButtonState != MouseButtonState.Pressed || IsTitleButton(args.OriginalSource as DependencyObject, (UIElement)sender))
                return;

            var surface = (UIElement)sender;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !TryReadCursor(out var cursor) ||
                !NotePlacement.GetWindowRect(handle, out var bounds) || !surface.CaptureMouse()) return;

            dragSurface = surface;
            dragHandle = handle;
            dragStart = cursor;
            dragOrigin = dragPosition = new DrawingPoint(bounds.Left, bounds.Top);
            surface.MouseMove += MoveNoteFromTitle;
            surface.MouseLeftButtonUp += ReleaseNoteFromTitle;
            surface.LostMouseCapture += CancelNoteDrag;
            args.Handled = true;
        }

        private void MoveNoteFromTitle(object sender, MouseEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed)
            {
                EndNoteDrag();
                return;
            }

            if (TryReadCursor(out var cursor)) UpdateNoteDragPosition(cursor);
            args.Handled = true;
        }

        private void ReleaseNoteFromTitle(object sender, MouseButtonEventArgs args)
        {
            bool hasCursor = TryReadCursor(out var cursor);
            if (hasCursor) UpdateNoteDragPosition(cursor);
            var travel = new DrawingPoint(dragPosition.X - dragOrigin.X, dragPosition.Y - dragOrigin.Y);
            EndNoteDrag();
            args.Handled = true;
            if (hasCursor && !travel.IsEmpty) DockAtEdge(cursor, travel);
        }

        private void CancelNoteDrag(object sender, MouseEventArgs args) => EndNoteDrag();

        private void UpdateNoteDragPosition(DrawingPoint cursor)
        {
            var target = DragOriginForCursor(dragOrigin, dragStart, cursor);
            if (target != dragPosition && CaptureScreen.Move(dragHandle, target)) dragPosition = target;
        }

        private void EndNoteDrag()
        {
            var surface = dragSurface;
            if (surface == null) return;
            dragSurface = null;
            dragHandle = IntPtr.Zero;
            surface.MouseMove -= MoveNoteFromTitle;
            surface.MouseLeftButtonUp -= ReleaseNoteFromTitle;
            surface.LostMouseCapture -= CancelNoteDrag;
            if (surface.IsMouseCaptured) surface.ReleaseMouseCapture();
        }

        private static DrawingPoint DragOriginForCursor(DrawingPoint origin, DrawingPoint start, DrawingPoint cursor) =>
            new DrawingPoint(origin.X + cursor.X - start.X, origin.Y + cursor.Y - start.Y);

        private static bool IsTitleButton(DependencyObject source, UIElement title)
        {
            for (var current = source; current != null && current != title;
                 current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (current is ButtonBase) return true;
            return false;
        }

        private static bool TryReadCursor(out DrawingPoint cursor)
        {
            try
            {
                cursor = CaptureScreen.Cursor;
                return true;
            }
            catch (InvalidOperationException)
            {
                cursor = default;
                return false;
            }
        }
    }
}
