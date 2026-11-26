using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureTranslationItem
    {
        internal CaptureTranslationItem(Int32Rect bounds, string translation) { Bounds = bounds; Translation = translation; }
        internal Int32Rect Bounds { get; }
        internal string Translation { get; }
    }

    internal static class CaptureTranslation
    {
        internal static async Task<BitmapSource> TranslateAsync(BitmapSource source, CancellationToken cancellation)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            // Capture may be a live, thread-affine bitmap. Freeze an independent snapshot
            // before OCR encoding leaves the caller's thread.
            if (!source.IsFrozen) { source = source.Clone(); source.Freeze(); }
            var regions = await CaptureOcr.RecognizeLocatedAsync(source, cancellation);
            var pending = regions.Select(r => new { Region = r, Text = Normalize(r.Text) })
                .Where(x => HasLetters(x.Text)).ToList();
            if (pending.Count == 0) return null;

            var translated = new List<CaptureTranslationItem>();
            var translator = new Translator();
            bool firstRequest = true;
            foreach (var direction in pending.GroupBy(x => ContainsChinese(x.Text) ? "en" : "zh"))
            {
                var group = direction.ToList();
                for (int offset = 0; offset < group.Count;)
                {
                    int end = offset, size = 0;
                    // Keep well below the API's request-size limits while batching lines.
                    while (end < group.Count && end - offset < 40)
                    {
                        int next = Encoding.UTF8.GetByteCount(group[end].Text) + 1;
                        if (next > 4500) throw new InvalidOperationException("识别到的单行文字过长，无法翻译。");
                        if (size + next > 4500) break;
                        size += next;
                        end++;
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (!firstRequest) await Task.Delay(1100, cancellation);
                    firstRequest = false;
                    var batch = group.GetRange(offset, end - offset);
                    var result = await translator.TranslateLinesAsync(batch.Select(x => x.Text).ToArray(), direction.Key, cancellation);
                    for (int i = 0; i < batch.Count; i++)
                        translated.Add(new CaptureTranslationItem(batch[i].Region.Bounds, result[i]));
                    offset = end;
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return Compose(source, translated, regions);
        }

        // Kept independent of services so rendering can be checked with synthetic images.
        internal static BitmapSource Compose(BitmapSource source, IReadOnlyList<CaptureTranslationItem> items, IReadOnlyList<CaptureTextRegion> allRegions)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int width = source.PixelWidth, height = source.PixelHeight;
            var pixelsSource = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            pixelsSource.CopyPixels(pixels, stride, 0);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new Rect(0, 0, width, height));
                foreach (var item in items)
                {
                    var b = item.Bounds;
                    if (b.Width <= 0 || b.Height <= 0 || b.X < 0 || b.Y < 0 ||
                        (long)b.X + b.Width > width || (long)b.Y + b.Height > height || string.IsNullOrWhiteSpace(item.Translation))
                        continue;
                    Color background = SampleBackground(pixels, stride, width, height, b, allRegions);
                    double lightness = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0;
                    var ink = new SolidColorBrush(lightness < 0.48 ? Colors.White : Color.FromRgb(25, 25, 25));
                    var rect = new Rect(b.X, b.Y, b.Width, b.Height);
                    dc.DrawRectangle(new SolidColorBrush(background), null, rect);
                    dc.PushClip(new RectangleGeometry(rect));
                    var formatted = FitText(item.Translation, Math.Max(1, b.Width - 2), Math.Max(1, b.Height - 2), ink);
                    dc.DrawText(formatted, new Point(b.X + 1, b.Y + Math.Max(0, (b.Height - formatted.Height) / 2)));
                    dc.Pop();
                }
            }
            var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            return result;
        }

        private static FormattedText FitText(string text, double width, double height, Brush ink)
        {
            // Allow line wrapping, then binary-search the largest complete text that fits.
            double low = 0.5, high = Math.Max(1, height), best = low;
            for (int i = 0; i < 14; i++)
            {
                double size = (low + high) / 2;
                var probe = MakeText(text, size, width, ink);
                if (probe.Height <= height && probe.WidthIncludingTrailingWhitespace <= width + 0.01) { best = size; low = size; }
                else high = size;
            }
            return MakeText(text, best, width, ink);
        }

        private static FormattedText MakeText(string text, double size, double width, Brush ink)
        {
            var result = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), size, ink, 1.0);
            result.MaxTextWidth = width;
            return result;
        }

        private static Color SampleBackground(byte[] pixels, int stride, int width, int height, Int32Rect box, IReadOnlyList<CaptureTextRegion> regions)
        {
            var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
            Action<int, int> sample = (x, y) =>
            {
                if (x < 0 || y < 0 || x >= width || y >= height) return;
                if (regions != null && regions.Any(r => x >= r.Bounds.X && x < r.Bounds.X + r.Bounds.Width && y >= r.Bounds.Y && y < r.Bounds.Y + r.Bounds.Height)) return;
                int p = y * stride + x * 4;
                if (pixels[p + 3] < 128) return;
                bs.Add(pixels[p]); gs.Add(pixels[p + 1]); rs.Add(pixels[p + 2]);
            };
            int stepX = Math.Max(1, box.Width / 40), stepY = Math.Max(1, box.Height / 20);
            for (int x = box.X; x < box.X + box.Width; x += stepX) { sample(x, box.Y - 1); sample(x, box.Y + box.Height); }
            for (int y = box.Y; y < box.Y + box.Height; y += stepY) { sample(box.X - 1, y); sample(box.X + box.Width, y); }
            if (rs.Count == 0)
            {
                int p = Math.Min(height - 1, box.Y) * stride + Math.Min(width - 1, box.X) * 4;
                return Color.FromRgb(pixels[p + 2], pixels[p + 1], pixels[p]);
            }
            rs.Sort(); gs.Sort(); bs.Sort();
            return Color.FromRgb(rs[rs.Count / 2], gs[gs.Count / 2], bs[bs.Count / 2]);
        }

        private static string Normalize(string text) => string.Join(" ", (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        private static bool HasLetters(string text) => text.Any(char.IsLetter);
        private static bool ContainsChinese(string text) => text.Any(c => c >= '\u3400' && c <= '\u9fff');
    }
}
