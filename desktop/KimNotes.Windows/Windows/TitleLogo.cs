using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KimNotes.Windows.Presentation
{
    internal sealed class TitleLogo : Image
    {
        private static readonly BitmapFrame[] Frames = LoadFrames();
        private static readonly Dictionary<(BitmapFrame Frame, Color? Wool, Color? Ink), BitmapSource> TintedFrames =
            new Dictionary<(BitmapFrame, Color?, Color?), BitmapSource>();
        private BitmapFrame frame;

        public static readonly DependencyProperty WoolTintProperty = DependencyProperty.Register(
            nameof(WoolTint), typeof(Brush), typeof(TitleLogo), new PropertyMetadata(null, ThemeChanged));
        public static readonly DependencyProperty OutlineTintProperty = DependencyProperty.Register(
            nameof(OutlineTint), typeof(Brush), typeof(TitleLogo), new PropertyMetadata(null, ThemeChanged));

        public Brush WoolTint
        {
            get => (Brush)GetValue(WoolTintProperty);
            set => SetValue(WoolTintProperty, value);
        }

        public Brush OutlineTint
        {
            get => (Brush)GetValue(OutlineTintProperty);
            set => SetValue(OutlineTintProperty, value);
        }

        public TitleLogo()
        {
            SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            SetResourceReference(WoolTintProperty, DesktopCardTheme.LogoWool);
            SetResourceReference(OutlineTintProperty, DesktopCardTheme.LogoOutline);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            UpdateFrame(constraint, VisualTreeHelper.GetDpi(this));
            return base.MeasureOverride(constraint);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            UpdateFrame(RenderSize, newDpi);
        }

        private void UpdateFrame(Size size, DpiScale dpi)
        {
            double pixels = Math.Max(size.Width * dpi.DpiScaleX, size.Height * dpi.DpiScaleY);
            if (pixels <= 0 || double.IsInfinity(pixels)) return;

            // Choose a frame we can display without upsampling its pixel art.
            var next = Array.Find(Frames, candidate => candidate.PixelWidth >= pixels) ?? Frames[Frames.Length - 1];
            if (frame == next) return;
            frame = next;
            UpdateSource();
        }

        private static void ThemeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
            ((TitleLogo)sender).UpdateSource();

        private void UpdateSource()
        {
            if (frame == null) return;
            var wool = (WoolTint as SolidColorBrush)?.Color;
            var ink = (OutlineTint as SolidColorBrush)?.Color;
            if (wool == null && ink == null)
            {
                if (Source != frame) Source = frame;
                return;
            }

            var key = (frame, wool, ink);
            if (!TintedFrames.TryGetValue(key, out var source))
            {
                source = Tint(frame, wool, ink);
                TintedFrames.Add(key, source);
            }
            if (Source != source) Source = source;
        }

        private static BitmapSource Tint(BitmapFrame source, Color? wool, Color? ink)
        {
            var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = rgba.PixelWidth * 4;
            var pixels = new byte[stride * rgba.PixelHeight];
            rgba.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;
                byte low = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                byte high = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                int chroma = high - low;
                // Restrict toning to near-white wool and dark details. Preserve skin, gold and the original alpha.
                if (wool.HasValue && low >= 205 && chroma < 25)
                    Blend(pixels, i, wool.Value, 0.5 * (1 - chroma / 25.0));
                else if (ink.HasValue && high < 120)
                    Blend(pixels, i, ink.Value, 0.28);
            }
            var bitmap = BitmapSource.Create(rgba.PixelWidth, rgba.PixelHeight, rgba.DpiX, rgba.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static void Blend(byte[] pixels, int index, Color color, double weight)
        {
            byte Mix(byte from, byte to) => (byte)Math.Round(from + (to - from) * weight);
            pixels[index] = Mix(pixels[index], color.B);
            pixels[index + 1] = Mix(pixels[index + 1], color.G);
            pixels[index + 2] = Mix(pixels[index + 2], color.R);
        }

        private static BitmapFrame[] LoadFrames()
        {
            string assemblyName = typeof(TitleLogo).Assembly.GetName().Name;
            var uri = new Uri("pack://application:,,,/" + assemblyName + ";component/Sheep.ico");
            var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(frame => frame.PixelWidth).ToArray();
        }
    }
}
