using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureAnnotationDocument
    {
        internal static readonly FontFamily TextFont = new FontFamily("Segoe UI");
        private static readonly Pen MosaicOutline = CreatePen(Color.FromRgb(222, 66, 74), 2.5);
        private static readonly Brush MosaicPreview = CreateInk(Color.FromArgb(116, 126, 137, 149));
        private readonly DrawingGroup marks = new DrawingGroup();
        private BitmapSource composition;
        private BitmapSource colorSample;

        internal CaptureAnnotationDocument(BitmapSource source)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }

        internal BitmapSource Source { get; }
        internal bool CanUndo => marks.Children.Count != 0;
        internal Rect Bounds => new Rect(0, 0, Source.PixelWidth, Source.PixelHeight);

        internal void DrawMarks(DrawingContext drawing) => drawing.DrawDrawing(marks);

        internal bool Add(CaptureTool tool, Point start, Point end, Pen pen, bool filled)
        {
            if ((end - start).Length < 1) return false;
            if (tool == CaptureTool.Mosaic)
            {
                var bounds = Rect.Intersect(Bounds, new Rect(start, end));
                if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return false;
                var mosaic = Mosaic(bounds);
                AddMark(drawing => drawing.DrawImage(mosaic.Item1, mosaic.Item2));
            }
            else if (tool == CaptureTool.Rectangle || tool == CaptureTool.Arrow)
                AddMark(drawing => DrawGesture(drawing, tool, start, end, pen, filled));
            else return false;
            return true;
        }

        internal void AddStroke(IReadOnlyList<Point> points, Pen pen)
        {
            if (points.Count == 0) return;
            AddMark(drawing => DrawStroke(drawing, points, pen));
        }

        internal void AddText(Point at, string text, double fontSize, double width, Brush foreground)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var lettering = FormatText(text, fontSize, width, foreground);
            AddMark(drawing => drawing.DrawText(lettering, at));
        }

        internal void AddReplacement(BitmapSource image) => AddMark(drawing => drawing.DrawImage(image, Bounds));

        internal static Size MeasureText(string text, double fontSize, double width)
        {
            // A trailing newline creates a line for the caret even before anything is typed on it.
            if (string.IsNullOrEmpty(text) || text.EndsWith("\n", StringComparison.Ordinal)) text += "\u200B";
            var lettering = FormatText(text, fontSize, width, Brushes.Black);
            return new Size(lettering.WidthIncludingTrailingWhitespace, lettering.Height);
        }

        private static FormattedText FormatText(string text, double fontSize, double width, Brush foreground) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(TextFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                fontSize, foreground, 1.0) { MaxTextWidth = Math.Max(1, width), Trimming = TextTrimming.None };

        internal void Undo()
        {
            if (!CanUndo) return;
            marks.Children.RemoveAt(marks.Children.Count - 1);
            InvalidateSnapshots();
        }

        private void AddMark(Action<DrawingContext> draw)
        {
            var mark = new DrawingGroup();
            using (var drawing = mark.Open()) draw(drawing);
            mark.Freeze();
            marks.Children.Add(mark);
            InvalidateSnapshots();
        }

        private void InvalidateSnapshots()
        {
            composition = null;
            colorSample = null;
        }

        internal BitmapSource Render()
        {
            if (!CanUndo) return Source;
            return composition ?? (composition = Rasterize(new Int32Rect(0, 0, Source.PixelWidth, Source.PixelHeight)));
        }

        private BitmapSource Rasterize(Int32Rect region)
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushClip(new RectangleGeometry(new Rect(0, 0, region.Width, region.Height)));
                if (region.X != 0 || region.Y != 0) drawing.PushTransform(new TranslateTransform(-region.X, -region.Y));
                drawing.DrawImage(Source, Bounds);
                DrawMarks(drawing);
                if (region.X != 0 || region.Y != 0) drawing.Pop();
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        internal string ColorAt(Point location)
        {
            var image = colorSample ?? (colorSample = ReadableColors(Render()));
            int x = Math.Max(0, Math.Min((int)location.X, image.PixelWidth - 1));
            int y = Math.Max(0, Math.Min((int)location.Y, image.PixelHeight - 1));
            var pixel = new byte[4];
            image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";
        }

        internal static Brush CreateInk(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        internal static Pen CreatePen(Color color, double width)
        {
            var pen = new Pen(CreateInk(color), width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            return pen;
        }

        internal static void DrawGesture(DrawingContext drawing, CaptureTool tool, Point start, Point end, Pen pen, bool filled)
        {
            if (tool == CaptureTool.Rectangle)
                drawing.DrawRectangle(filled ? pen.Brush : null, pen, new Rect(start, end));
            else if (tool == CaptureTool.Mosaic)
                drawing.DrawRectangle(MosaicPreview, MosaicOutline, new Rect(start, end));
            else if (tool == CaptureTool.Arrow)
            {
                var vector = end - start;
                if (vector.Length < 1) return;
                double head = Math.Min(Math.Max(15, pen.Thickness * 3.5), vector.Length / 2);
                vector.Normalize();
                var normal = new Vector(-vector.Y, vector.X);
                drawing.DrawLine(pen, start, end);
                drawing.DrawLine(pen, end, end - vector * head + normal * (head * 0.5));
                drawing.DrawLine(pen, end, end - vector * head - normal * (head * 0.5));
            }
        }

        internal static void DrawStroke(DrawingContext drawing, IReadOnlyList<Point> points, Pen pen)
        {
            if (points.Count == 1)
            {
                double radius = pen.Thickness / 2;
                drawing.DrawEllipse(pen.Brush, null, points[0], radius, radius);
                return;
            }

            var path = new StreamGeometry();
            using (var curve = path.Open())
            {
                curve.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count - 1; i++)
                {
                    var adjust = (points[i + 1] - points[i]) * 0.5;
                    curve.QuadraticBezierTo(points[i], points[i] + adjust, true, true);
                }
                curve.LineTo(points[points.Count - 1], true, true);
            }
            path.Freeze();
            drawing.DrawGeometry(null, pen, path);
        }

        private Tuple<BitmapSource, Rect> Mosaic(Rect bounds)
        {
            const int BlockSize = 12;
            int left = (int)Math.Floor(bounds.Left);
            int top = (int)Math.Floor(bounds.Top);
            int width = Math.Min(Source.PixelWidth - left, (int)Math.Ceiling(bounds.Right) - left);
            int height = Math.Min(Source.PixelHeight - top, (int)Math.Ceiling(bounds.Bottom) - top);
            var sourceArea = new Int32Rect(left, top, width, height);
            var input = composition ?? Source;
            if (CanUndo && composition == null)
            {
                // Include any earlier marks, using just the pixels covered by this mosaic.
                input = Rasterize(sourceArea);
                sourceArea = new Int32Rect(0, 0, width, height);
            }
            int stride = checked(width * 4);
            var pixel = new byte[checked(stride * height)];
            ReadableColors(input).CopyPixels(sourceArea, pixel, stride, 0);

            // Average each source block, then fill exactly its extent, including partial edge blocks.
            for (int y = 0; y < height; y += BlockSize)
                for (int x = 0; x < width; x += BlockSize)
                {
                    int bottom = Math.Min(y + BlockSize, height), right = Math.Min(x + BlockSize, width);
                    int red = 0, green = 0, blue = 0, count = (bottom - y) * (right - x);
                    for (int row = y; row < bottom; row++)
                        for (int col = x; col < right; col++)
                        {
                            int i = row * stride + col * 4;
                            blue += pixel[i]; green += pixel[i + 1]; red += pixel[i + 2];
                        }
                    for (int row = y; row < bottom; row++)
                        for (int col = x; col < right; col++)
                        {
                            int i = row * stride + col * 4;
                            pixel[i] = (byte)(blue / count);
                            pixel[i + 1] = (byte)(green / count);
                            pixel[i + 2] = (byte)(red / count);
                        }
                }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixel, stride);
            result.Freeze();
            return Tuple.Create(result, new Rect(left, top, width, height));
        }

        private static BitmapSource ReadableColors(BitmapSource image) =>
            image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32 ? image :
                new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
    }
}
