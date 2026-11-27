using System.Windows;
using System.Windows.Media;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureTextOutline : FrameworkElement
    {
        private const double Inset = 3.5;
        private static readonly Pen BaseLine = CreateLine(Color.FromArgb(155, 38, 48, 60));
        private static readonly Pen Dashes = CreateLine(Color.FromArgb(230, 255, 255, 255), DashStyles.Dash);
        private static readonly Pen HandleEdge = CreateLine(Color.FromArgb(170, 47, 59, 75));

        public CaptureTextOutline()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            if (ActualWidth <= Inset * 2 || ActualHeight <= Inset * 2) return;
            var bounds = new Rect(Inset, Inset, ActualWidth - Inset * 2, ActualHeight - Inset * 2);
            drawing.DrawRectangle(null, BaseLine, bounds);
            drawing.DrawRectangle(null, Dashes, bounds);
            drawing.DrawEllipse(Brushes.White, HandleEdge, bounds.TopLeft, 2.5, 2.5);
            drawing.DrawEllipse(Brushes.White, HandleEdge, bounds.TopRight, 2.5, 2.5);
            drawing.DrawEllipse(Brushes.White, HandleEdge, bounds.BottomLeft, 2.5, 2.5);
            drawing.DrawEllipse(Brushes.White, HandleEdge, bounds.BottomRight, 2.5, 2.5);
        }

        private static Pen CreateLine(Color color, DashStyle dashes = null)
        {
            var line = new Pen(CaptureAnnotationDocument.CreateInk(color), 1);
            if (dashes != null) line.DashStyle = dashes;
            line.Freeze();
            return line;
        }
    }
}
