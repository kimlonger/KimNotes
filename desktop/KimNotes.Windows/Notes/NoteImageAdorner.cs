using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace KimNotes.Windows.Notes
{
    internal sealed class NoteImageAdorner : Adorner
    {
        private const double HandleSize = 10;
        private static readonly Pen SelectionOutline = CreateSelectionOutline();
        private readonly AdornerLayer layer;
        private readonly Thumb resize;
        private readonly VisualCollection visuals;
        private double currentWidth;
        private double heightPerWidth;

        private NoteImageAdorner(Image image, AdornerLayer layer, Action<double> resizeImage) : base(image)
        {
            this.layer = layer;
            resize = new Thumb { Width = HandleSize, Height = HandleSize, Cursor = Cursors.SizeNWSE, Focusable = false,
                Background = Brushes.White, BorderBrush = Brushes.SteelBlue, BorderThickness = new Thickness(1) };
            resize.DragStarted += (sender, args) =>
            {
                currentWidth = image.ActualWidth;
                heightPerWidth = currentWidth > 0 ? image.ActualHeight / currentWidth : 0;
            };
            resize.DragDelta += (sender, args) =>
            {
                currentWidth += (args.HorizontalChange + args.VerticalChange * heightPerWidth) / (1 + heightPerWidth * heightPerWidth);
                resizeImage(currentWidth);
            };
            visuals = new VisualCollection(this) { resize };
        }

        internal static NoteImageAdorner ShowOn(Image image, Action<double> resize)
        {
            var layer = AdornerLayer.GetAdornerLayer(image);
            if (layer == null) return null;
            var adorner = new NoteImageAdorner(image, layer, resize);
            layer.Add(adorner);
            return adorner;
        }

        internal void Dismiss() => layer.Remove(this);
        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];

        protected override Size MeasureOverride(Size constraint)
        {
            resize.Measure(new Size(HandleSize, HandleSize));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var image = AdornedElement.RenderSize;
            resize.Arrange(new Rect(Math.Max(0, image.Width - HandleSize), Math.Max(0, image.Height - HandleSize), HandleSize, HandleSize));
            return finalSize;
        }

        protected override void OnRender(DrawingContext context)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width > 1 && size.Height > 1)
                context.DrawRectangle(null, SelectionOutline, new Rect(.5, .5, size.Width - 1, size.Height - 1));
        }

        private static Pen CreateSelectionOutline()
        {
            var pen = new Pen(Brushes.SteelBlue, 1);
            pen.Freeze();
            return pen;
        }
    }
}
