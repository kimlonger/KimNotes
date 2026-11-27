using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using KimNotes.utils;

namespace KimNotes.Windows.Capture
{
    // The UI owns the queue. Workers only encode frozen images and update the disk archive in order.
    internal sealed class CaptureArchiveWrites
    {
        private readonly Dictionary<string, Task<Exception>> pending = new Dictionary<string, Task<Exception>>();
        private Task tail = Task.CompletedTask;

        internal void Queue(string id, BitmapSource image)
        {
            if (!image.IsFrozen)
            {
                image = image.CloneCurrentValue();
                image.Freeze();
            }

            var write = tail.ContinueWith(_ => Write(id, image), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            pending[id] = write;
            tail = write;
        }

        internal Exception Wait(string id)
        {
            if (!pending.TryGetValue(id, out var write)) return null;
            pending.Remove(id);
            return write.GetAwaiter().GetResult();
        }

        internal void Drain() => tail.GetAwaiter().GetResult();

        private static Exception Write(string id, BitmapSource image)
        {
            try
            {
                ScreenshotArchive.SavePng(id, stream => CaptureBitmap.Write(stream, image, false));
                return null;
            }
            catch (Exception failure)
            {
                Program.LogError(failure);
                return failure;
            }
        }
    }
}
