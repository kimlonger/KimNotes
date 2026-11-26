using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureClipboard
    {
        private const uint DibV5 = 17;
        private const uint UnicodeText = 13;
        private const uint MoveableAndZeroed = 0x0042;
        private const int RetryDelayMilliseconds = 30;
        private const int HeaderSize = 124;
        private readonly object publicationGate = new object();
        private int request;
        private Publication lastCopy;

        internal Task CopyAsync(BitmapSource source)
        {
            var image = source.IsFrozen ? source : (BitmapSource)source.GetAsFrozen();
            var (ticket, sequence) = BeginPublication();
            return Task.Run(() => Publish(() => BuildDib(image), DibV5, ticket, sequence, 10, CancellationToken.None));
        }

        internal Task<bool> CopyTextAsync(string text, CancellationToken token = default) =>
            string.IsNullOrEmpty(text) || token.IsCancellationRequested
                ? Task.FromResult(false)
                : BeginTextCopy()(text, token);

        internal Func<string, CancellationToken, Task<bool>> BeginTextCopy()
        {
            // Keep the order of the user's requests while OCR is still preparing its result.
            var (ticket, sequence) = BeginPublication();
            return (text, token) => PublishTextAsync(text, ticket, sequence, token);
        }

        internal void CancelPending() => Interlocked.Increment(ref request);

        private (int Ticket, uint Sequence) BeginPublication()
        {
            // Do not see our own intermediate sequence between EmptyClipboard and SetClipboardData.
            lock (publicationGate)
                return (Interlocked.Increment(ref request), GetClipboardSequenceNumber());
        }

        private async Task<bool> PublishTextAsync(string text, int ticket, uint sequence, CancellationToken token)
        {
            if (string.IsNullOrEmpty(text) || !IsCurrent(ticket, token)) return false;
            try
            {
                return await Task.Run(() => Publish(() => BuildText(text), UnicodeText, ticket, sequence, 34, token))
                    .ConfigureAwait(false);
            }
            catch (ExternalException) { return false; }
        }

        private bool IsCurrent(int ticket, CancellationToken token) =>
            !token.IsCancellationRequested && ticket == Volatile.Read(ref request);

        private bool Publish(Func<IntPtr> buildData, uint format, int ticket, uint sequence, int attempts, CancellationToken token)
        {
            if (!IsCurrent(ticket, token)) return false;
            IntPtr memory = buildData();
            IntPtr owner = IntPtr.Zero;
            try
            {
                if (!IsCurrent(ticket, token)) return false;
                // A message-only window provides a clipboard owner without showing a window.
                owner = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (owner == IntPtr.Zero) throw new Win32Exception();
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    if (!IsCurrent(ticket, token)) return false;
                    if (!OpenClipboard(owner))
                    {
                        Thread.Sleep(RetryDelayMilliseconds);
                        continue;
                    }
                    lock (publicationGate)
                    {
                        try
                        {
                            // Keep a subsequent copy in this or another app when preparation finishes late.
                            uint current = GetClipboardSequenceNumber();
                            if (!IsCurrent(ticket, token) || !MayReplace(sequence, current, ticket, lastCopy)) return false;
                            if (!EmptyClipboard() || SetClipboardData(format, memory) == IntPtr.Zero) throw new Win32Exception();
                            memory = IntPtr.Zero; // The system now owns the completed buffer.
                            lastCopy = new Publication(current, GetClipboardSequenceNumber(), ticket);
                            return true;
                        }
                        finally { CloseClipboard(); }
                    }
                }
                if (IsCurrent(ticket, token))
                    throw new ExternalException("剪贴板正在使用，请重试。");
                return false;
            }
            finally
            {
                if (owner != IntPtr.Zero) DestroyWindow(owner);
                if (memory != IntPtr.Zero) GlobalFree(memory);
            }
        }

        private static bool MayReplace(uint expected, uint current, int ticket, Publication last) =>
            expected == current || (last != null && last.Ticket < ticket && last.Before == expected && last.After == current);

        private sealed class Publication
        {
            internal readonly uint Before, After;
            internal readonly int Ticket;

            internal Publication(uint before, uint after, int ticket)
            {
                Before = before;
                After = after;
                Ticket = ticket;
            }
        }

        private static IntPtr BuildText(string text)
        {
            uint bytes = (uint)checked((text.Length + 1) * sizeof(char));
            IntPtr memory = GlobalAlloc(MoveableAndZeroed, new UIntPtr(bytes));
            if (memory == IntPtr.Zero) throw new OutOfMemoryException();
            try
            {
                IntPtr buffer = GlobalLock(memory);
                if (buffer == IntPtr.Zero) throw new Win32Exception();
                try { Marshal.Copy(text.ToCharArray(), 0, buffer, text.Length); }
                finally { GlobalUnlock(memory); }
                return memory;
            }
            catch
            {
                GlobalFree(memory);
                throw;
            }
        }

        private static IntPtr BuildDib(BitmapSource image)
        {
            var pixels = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int stride = checked(pixels.PixelWidth * 4);
            int bytes = checked(stride * pixels.PixelHeight);
            IntPtr memory = GlobalAlloc(MoveableAndZeroed, new UIntPtr((uint)checked(bytes + HeaderSize)));
            if (memory == IntPtr.Zero) throw new OutOfMemoryException();
            try
            {
                IntPtr buffer = GlobalLock(memory);
                if (buffer == IntPtr.Zero) throw new Win32Exception();
                try
                {
                    Marshal.StructureToPtr(new DibHeader
                    {
                        Size = HeaderSize, Width = pixels.PixelWidth, Height = -pixels.PixelHeight,
                        Planes = 1, BitCount = 32, Compression = 3, ImageBytes = bytes,
                        RedMask = 0x00FF0000, GreenMask = 0x0000FF00, BlueMask = 0x000000FF, AlphaMask = 0xFF000000,
                        ColorSpace = 0x73524742, Intent = 4 // LCS_sRGB and LCS_GM_IMAGES
                    }, buffer, false);
                    pixels.CopyPixels(Int32Rect.Empty, IntPtr.Add(buffer, HeaderSize), bytes, stride);
                }
                finally { GlobalUnlock(memory); }
                return memory;
            }
            catch
            {
                GlobalFree(memory);
                throw;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
        private struct DibHeader
        {
            [FieldOffset(0)] internal int Size;
            [FieldOffset(4)] internal int Width;
            [FieldOffset(8)] internal int Height;
            [FieldOffset(12)] internal ushort Planes;
            [FieldOffset(14)] internal ushort BitCount;
            [FieldOffset(16)] internal int Compression;
            [FieldOffset(20)] internal int ImageBytes;
            [FieldOffset(40)] internal uint RedMask;
            [FieldOffset(44)] internal uint GreenMask;
            [FieldOffset(48)] internal uint BlueMask;
            [FieldOffset(52)] internal uint AlphaMask;
            [FieldOffset(56)] internal uint ColorSpace;
            [FieldOffset(108)] internal int Intent;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint style, string windowClass, string name, uint flags,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    }
}
