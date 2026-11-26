using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureDisplay
    {
        internal CaptureDisplay(DrawingRect bounds, DrawingRect workArea)
        {
            Bounds = bounds;
            WorkArea = workArea;
        }

        internal DrawingRect Bounds { get; }
        internal DrawingRect WorkArea { get; }
    }

    internal sealed class ScreenSnapshot
    {
        private readonly IReadOnlyDictionary<DrawingRect, BitmapSource> frames;

        internal ScreenSnapshot(IReadOnlyDictionary<DrawingRect, BitmapSource> frames)
        {
            this.frames = frames;
            Bounds = frames.Keys.Aggregate(DrawingRect.Union);
        }

        internal DrawingRect Bounds { get; }
        internal BitmapSource MonitorImage(DrawingRect bounds) => frames[bounds];

        internal BitmapSource Copy(DrawingRect area)
        {
            area = DrawingRect.Intersect(Bounds, area);
            if (area.Width <= 0 || area.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(area));
            if (frames.TryGetValue(area, out var fullMonitor)) return fullMonitor;

            int stride = checked(area.Width * 4);
            var pixels = new byte[checked(stride * area.Height)];
            foreach (var frame in frames)
            {
                var visible = DrawingRect.Intersect(area, frame.Key);
                if (visible.Width <= 0 || visible.Height <= 0) continue;
                var from = new Int32Rect(visible.Left - frame.Key.Left, visible.Top - frame.Key.Top,
                    visible.Width, visible.Height);
                int offset = (visible.Top - area.Top) * stride + (visible.Left - area.Left) * 4;
                frame.Value.CopyPixels(from, pixels, stride, offset);
            }
            var result = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            result.Freeze();
            return result;
        }
    }

    internal static class CaptureScreen
    {
        private delegate bool MonitorVisitor(IntPtr monitor, IntPtr dc, IntPtr bounds, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clipping, MonitorVisitor visitor, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetPhysicalCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint options);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            internal DrawingRect Bounds => DrawingRect.FromLTRB(Left, Top, Right, Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        internal static DrawingPoint Cursor
        {
            get
            {
                if (!GetPhysicalCursorPos(out var point))
                    throw new InvalidOperationException("暂时无法获取鼠标位置。");
                return new DrawingPoint(point.X, point.Y);
            }
        }

        internal static IReadOnlyList<CaptureDisplay> GetDisplays()
        {
            var displays = new List<CaptureDisplay>();
            using (new PhysicalDpiScope())
            {
                bool success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, __, ___) =>
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                    if (!GetMonitorInfo(monitor, ref info)) return true;
                    var bounds = info.Monitor.Bounds;
                    if (bounds.Width > 0 && bounds.Height > 0 && !displays.Any(display => display.Bounds == bounds))
                        displays.Add(new CaptureDisplay(bounds, info.Work.Bounds));
                    return true;
                }, IntPtr.Zero);
                if (!success || displays.Count == 0)
                    throw new InvalidOperationException("未找到可截图的显示器。");
            }
            return displays;
        }

        internal static CaptureDisplay DisplayNear(DrawingPoint point, IReadOnlyList<CaptureDisplay> displays)
        {
            return displays.FirstOrDefault(display => display.Bounds.Contains(point)) ??
                displays.OrderBy(display =>
                {
                    var bounds = display.Bounds;
                    double x = point.X - Math.Max(bounds.Left, Math.Min(point.X, bounds.Right));
                    double y = point.Y - Math.Max(bounds.Top, Math.Min(point.Y, bounds.Bottom));
                    return x * x + y * y;
                }).First();
        }

        internal static ScreenSnapshot Take(IReadOnlyList<CaptureDisplay> displays)
        {
            var frames = new Dictionary<DrawingRect, BitmapSource>(displays.Count);
            using (new PhysicalDpiScope())
                foreach (var display in displays)
                    frames.Add(display.Bounds, TakeMonitor(display.Bounds));
            return new ScreenSnapshot(frames);
        }

        private static BitmapSource TakeMonitor(DrawingRect area)
        {
            const System.Drawing.Imaging.PixelFormat format = System.Drawing.Imaging.PixelFormat.Format32bppRgb;
            using (var image = new DrawingBitmap(area.Width, area.Height, format))
            {
                using (var graphics = Graphics.FromImage(image))
                    graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);

                var data = image.LockBits(new DrawingRect(0, 0, area.Width, area.Height), ImageLockMode.ReadOnly, format);
                try
                {
                    BitmapSource source;
                    if (data.Stride > 0)
                    {
                        int size = checked(data.Stride * area.Height);
                        source = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32,
                            null, data.Scan0, size, data.Stride);
                    }
                    else
                    {
                        int stride = checked(area.Width * 4);
                        var pixels = new byte[checked(stride * area.Height)];
                        for (int row = 0; row < area.Height; row++)
                            Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * stride, stride);
                        source = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
                    }
                    source.Freeze();
                    return source;
                }
                finally
                {
                    image.UnlockBits(data);
                }
            }
        }

        internal static void Position(Window window, DrawingRect bounds)
        {
            const uint NoActivationOrZOrderChange = 0x0010 | 0x0004 | 0x0200;
            var handle = new WindowInteropHelper(window).EnsureHandle();
            if (!SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NoActivationOrZOrderChange))
                throw new InvalidOperationException("无法把窗口放到目标屏幕。");
        }

        internal static bool Move(IntPtr window, DrawingPoint position)
        {
            const uint MoveOnly = 0x0001 | 0x0004 | 0x0010 | 0x0200;
            return SetWindowPos(window, IntPtr.Zero, position.X, position.Y, 0, 0, MoveOnly);
        }

        internal sealed class PhysicalDpiScope : IDisposable
        {
            private readonly IntPtr previous;

            internal PhysicalDpiScope()
            {
                try
                {
                    previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // Per-monitor v2
                    if (previous == IntPtr.Zero) previous = SetThreadDpiAwarenessContext(new IntPtr(-3));
                }
                catch (EntryPointNotFoundException) { } // Older systems use the process manifest.
            }

            public void Dispose()
            {
                if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    internal static class CaptureBitmap
    {
        internal static BitmapSource FromDrawing(DrawingBitmap image)
        {
            using (var stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source.Freeze();
                return source;
            }
        }

        internal static DrawingBitmap ToDrawing(BitmapSource source)
        {
            using (var stream = new MemoryStream())
            {
                Write(stream, source, false);
                stream.Position = 0;
                using (var image = new DrawingBitmap(stream))
                    return new DrawingBitmap(image);
            }
        }

        internal static void Write(Stream stream, BitmapSource source, bool jpeg)
        {
            BitmapEncoder encoder = jpeg ? (BitmapEncoder)new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
        }
    }
}
