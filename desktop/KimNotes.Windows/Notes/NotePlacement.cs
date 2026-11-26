using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using KimNotes.utils;
using WinForms = System.Windows.Forms;

namespace KimNotes.Windows.Notes
{
    internal static class NotePlacement
    {
        internal static NoteMetadata Capture(Window window, int theme)
        {
            var handle = new WindowInteropHelper(window).Handle;
            var metadata = new NoteMetadata { ThemeId = theme, Pinned = window.Topmost, Dpi = WindowDpi(window, handle) };
            if (handle == IntPtr.Zero) return metadata;
            if (window.WindowState == WindowState.Normal)
            {
                if (GetWindowRect(handle, out var rect))
                    metadata.Bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            }
            else if (HwndSource.FromHwnd(handle)?.CompositionTarget is CompositionTarget target)
            {
                // RestoreBounds already includes the monitor's work-area offset. Persist screen pixels
                // just like the normal-window path so reopening can use the same SetWindowPos call.
                var bounds = window.RestoreBounds;
                if (!bounds.IsEmpty)
                {
                    bounds.Transform(target.TransformToDevice);
                    metadata.Bounds = System.Drawing.Rectangle.FromLTRB((int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
                        (int)Math.Round(bounds.Right), (int)Math.Round(bounds.Bottom));
                }
            }
            if (metadata.Bounds.HasValue) metadata.Monitor = WinForms.Screen.FromRectangle(metadata.Bounds.Value).DeviceName;
            return metadata;
        }

        private static int WindowDpi(Window window, IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var dpi = GetDpiForWindow(handle);
                    if (dpi > 0) return (int)dpi;
                }
                catch (EntryPointNotFoundException) { } // Windows versions before 10, version 1607.
            }
            return (int)Math.Round(VisualTreeHelper.GetDpi(window).PixelsPerInchX);
        }

        internal static void Restore(Window window, NoteMetadata metadata)
        {
            if (!metadata.Bounds.HasValue) return;
            var rect = metadata.Bounds.Value;
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var screen = WinForms.Screen.FromRectangle(rect);
            var work = screen.WorkingArea;
            var width = Math.Min(rect.Width, work.Width);
            var height = Math.Min(rect.Height, work.Height);
            var x = Math.Max(work.Left, Math.Min(rect.Left, work.Right - width));
            var y = Math.Max(work.Top, Math.Min(rect.Top, work.Bottom - height));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SourceInitialized += (sender, args) =>
                SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, width, height, 0x14);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr handle);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
