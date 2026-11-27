using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow
    {
        private void ScrollEditorWithWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0 || composing || Keyboard.Modifiers != ModifierKeys.None) return;
            if (selectedImage != null && TryResizeSelectedImageWithWheel(e.GetPosition(Editor), e.Delta))
            {
                e.Handled = true;
                return;
            }

            if (!(Editor.Template?.FindName("PART_ContentHost", Editor) is ScrollViewer host) ||
                !(host.Content is IScrollInfo scroll) || scroll.ScrollOwner != host || !scroll.CanVerticallyScroll)
                return;

            const double WheelDelta = 120;
            const double LineHeight = 16;
            int lines = SystemParameters.WheelScrollLines;
            double distance = lines < 0 ? scroll.ViewportHeight : lines * LineHeight;

            // The document updates its offset immediately, so small ticks accumulate before the next layout.
            double before = scroll.VerticalOffset;
            scroll.SetVerticalOffset(before - e.Delta / WheelDelta * distance);
            if (dirty && scroll.VerticalOffset != before)
            {
                var afterScroll = DateTime.UtcNow.AddMilliseconds(250);
                if (saveAfterUtc < afterScroll) saveAfterUtc = afterScroll;
            }
            e.Handled = true;
        }

        private bool TryResizeSelectedImageWithWheel(Point editorPoint, int delta)
        {
            var image = selectedImage;
            if (delta == 0 || ImageContainer(image) == null || !image.IsDescendantOf(Editor)) return false;
            var bounds = new Rect(image.RenderSize);
            if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(Editor.TranslatePoint(editorPoint, image)))
                return false;

            ResizeImage(image, Math.Pow(1.12, delta / 120.0));
            return true;
        }
    }
}
