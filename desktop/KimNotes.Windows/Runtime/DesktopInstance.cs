using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using KimNotes.utils;

namespace KimNotes.Windows.Runtime
{
    internal sealed class DesktopInstance : IDisposable
    {
        private readonly Mutex mutex;
        private readonly EventWaitHandle activation;
        private readonly RegisteredWaitHandle listener;
        internal bool IsPrimary { get; }
        internal event Action ActivateRequested;

        internal DesktopInstance(Dispatcher dispatcher)
        {
            string identity = Path.GetFullPath(Storage.Root).TrimEnd('\\').ToUpperInvariant();
            string hash;
            using (var algorithm = SHA256.Create())
                hash = BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "");
            string name = @"Local\KimNotesWpf-" + hash;
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
            mutex = new Mutex(true, name, out bool created);
            IsPrimary = created;
            if (created)
                listener = ThreadPool.RegisterWaitForSingleObject(activation,
                    (state, timedOut) => dispatcher.BeginInvoke(new Action(() => ActivateRequested?.Invoke())), null,
                    Timeout.Infinite, false);
            else activation.Set();
        }

        internal static bool UsesLegacyProfile(string root)
        {
            string legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KimNotes");
            return string.Equals(Path.GetFullPath(root).TrimEnd('\\'), legacyRoot, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsLegacyProfileInUse()
        {
            if (!UsesLegacyProfile(Storage.Root)) return false;
            using (var current = Process.GetCurrentProcess())
            {
                var processes = Process.GetProcessesByName("KimNotes");
                try
                {
                    foreach (var process in processes)
                        if (process.SessionId == current.SessionId && !process.HasExited) return true;
                    return false;
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
        }

        public void Dispose()
        {
            listener?.Unregister(null);
            activation.Dispose();
            if (IsPrimary) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
