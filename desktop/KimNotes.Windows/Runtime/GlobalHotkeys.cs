using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using KimNotes.utils;
using Forms = System.Windows.Forms;

namespace KimNotes.Windows.Runtime
{
    internal sealed class GlobalHotkeys : IDisposable
    {
        private static readonly string[] Settings = { "shortcutKey", "recallKey", "previousImageKey", "nextImageKey", InitConfig.LatestNoteHotkeyKey };
        private readonly HwndSource sink = new HwndSource(new HwndSourceParameters("KimNotes Hotkeys")
            { ParentWindow = new IntPtr(-3) });
        private readonly Action[] commands;
        private readonly List<int> active = new List<int>();
        private Dictionary<string, string> current;
        private bool suspended;

        internal GlobalHotkeys(params Action[] commands)
        {
            this.commands = commands;
            sink.AddHook(Receive);
        }

        internal static Dictionary<string, string> Read()
        {
            var keys = new Dictionary<string, string>();
            foreach (var setting in Settings) keys[setting] = InitConfig.GetConfigValue(setting);
            return keys;
        }

        internal void Apply(Dictionary<string, string> keys)
        {
            var previous = current;
            Unregister();
            suspended = false;
            try
            {
                Register(keys);
                current = keys;
            }
            catch
            {
                Unregister();
                current = previous;
                if (previous != null) Register(previous);
                throw;
            }
        }

        internal void SetSuspended(bool value)
        {
            if (value == suspended) return;
            suspended = value;
            if (value) Unregister();
            else if (current != null) Register(current);
        }

        private void Register(Dictionary<string, string> keys)
        {
            var used = new HashSet<Forms.Keys>();
            for (int i = 0; i < Settings.Length; i++)
            {
                if (!keys.TryGetValue(Settings[i], out var text) || !Enum.TryParse(text, true, out Forms.Keys key))
                    throw new InvalidOperationException("快捷键无效。");
                if (key == Forms.Keys.None) continue;
                if ((key & Forms.Keys.Modifiers) != Forms.Keys.None || !used.Add(key))
                    throw new InvalidOperationException("快捷键重复或包含额外修饰键。");
                if (!RegisterHotKey(sink.Handle, i + 1, 0x4002, (uint)key))
                    throw new InvalidOperationException("Ctrl + " + key + " 已被占用。");
                active.Add(i + 1);
            }
        }

        private void Unregister()
        {
            foreach (int id in active) UnregisterHotKey(sink.Handle, id);
            active.Clear();
        }

        private IntPtr Receive(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x0312) return IntPtr.Zero;
            int id = wParam.ToInt32();
            if (!suspended && id >= 1 && id <= commands.Length && active.Contains(id))
                sink.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (!suspended && !sink.IsDisposed) commands[id - 1]();
                }));
            handled = true;
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
            sink.RemoveHook(Receive);
            sink.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr handle, int id);
    }
}
