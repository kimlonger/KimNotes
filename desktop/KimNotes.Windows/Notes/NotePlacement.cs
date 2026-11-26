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
            var dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX;
            var metadata = new NoteMetadata { ThemeId = theme, Pinned = window.Topmost, Dpi = (int)Math.Round(dpi) };
            if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
            {
                metadata.Bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                metadata.Monitor = WinForms.Screen.FromHandle(handle).DeviceName;
            }
            return metadata;
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
        internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
