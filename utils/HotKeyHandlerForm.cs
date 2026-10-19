using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes.utils
{
    public partial class HotKeyHandlerForm : Form
    {
        private const int HOTKEY_ID = 1;
        private const uint HistoryModifiers = 0x4002; // Ctrl + MOD_NOREPEAT
        private volatile bool isCapturing;
        private bool captureQueued;
        private bool suspended;
        private readonly Func<IntPtr, bool> register;
        private readonly Action<IntPtr> unregister;
        private IntPtr registeredHandle;
        private readonly bool historyHotkeys;
        private readonly System.Collections.Generic.List<int> registeredHistory = new System.Collections.Generic.List<int>();
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotKeyHandlerForm() : this(null, null) { }

        internal HotKeyHandlerForm(Func<IntPtr, bool> register, Action<IntPtr> unregister)
        {
            historyHotkeys = register == null;
            this.register = register ?? RegisterGlobalHotKey;
            this.unregister = unregister ?? (hwnd => UnregisterHotKey(hwnd, HOTKEY_ID));
            this.Load += (s, e) => this.Visible = false; // 隐藏窗体
            var createdHandle = Handle;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!suspended && register != null && register(Handle)) registeredHandle = Handle;
            if (!suspended && historyHotkeys && registeredHistory != null) RegisterHistoryKeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            captureQueued = false;
            ReleaseHotkeys();
            base.OnHandleDestroyed(e);
        }

        private void ReleaseHotkeys()
        {
            if (registeredHistory != null)
            {
                foreach (int id in registeredHistory) UnregisterHotKey(Handle, id);
                registeredHistory.Clear();
            }
            if (registeredHandle != IntPtr.Zero)
            {
                unregister(registeredHandle);
                registeredHandle = IntPtr.Zero;
            }
        }

        internal void SetSuspended(bool value)
        {
            if (suspended == value || IsDisposed || Disposing) return;
            suspended = value;
            captureQueued = false;
            if (!IsHandleCreated) return;
            if (suspended) ReleaseHotkeys();
            else
            {
                if (register(Handle)) registeredHandle = Handle;
                if (historyHotkeys) RegisterHistoryKeys();
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312 && suspended) return;

            if (m.Msg == 0x0312 && m.WParam.ToInt32() >= 2 && m.WParam.ToInt32() <= 4 && !IsDisposed && IsHandleCreated)
            {
                int id = m.WParam.ToInt32();
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || !IsHandleCreated || isCapturing || suspended) return;
                    if (id == 2) ScreenshotHelper.RecallLatest();
                    else ScreenshotHelper.NavigateHistory(id == 3 ? 1 : -1);
                }));
                return;
            }

            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
            {
                // 热键按住会连续重复触发；截图期间忽略新的热键，避免无限嵌套弹出截图
                if (isCapturing || captureQueued || IsDisposed || Disposing || !IsHandleCreated) return;
                captureQueued = true;

                // 不在 WndProc 里同步弹模态框，转到消息队列执行
                this.BeginInvoke((Action)(() =>
                {
                    captureQueued = false;
                    if (isCapturing || suspended || IsDisposed || Disposing || !IsHandleCreated) return;
                    isCapturing = true;
                    try
                    {
                        var screenshotForm = ScreenshotHelper.CaptureInteractive(InitConfig.GetConfigValue("imagesPath"), Storage.Incognito);
                        if (screenshotForm != null && !screenshotForm.Visible) screenshotForm.Show();
                    }
                    finally
                    {
                        isCapturing = false;
                    }
                }));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) components?.Dispose();
            base.Dispose(disposing);
        }

        private bool RegisterGlobalHotKey(IntPtr hwnd)
        {
            uint hotkeyValue = (uint)Keys.F1; // 默认值
            if (Enum.TryParse<Keys>(InitConfig.GetConfigValue("shortcutKey"), true, out Keys parsedKey))
            {
                if (parsedKey == Keys.None) return false;
                hotkeyValue = (uint)parsedKey;
            }
            bool ok = RegisterHotKey(hwnd, HOTKEY_ID, 0x2, hotkeyValue); // Ctrl+hotkey
            if (!ok)
            {
                MessageBox.Show(
                    $"截图热键 Ctrl+{InitConfig.GetConfigValue("shortcutKey")} 注册失败，可能已被其他程序占用，可在配置中更换。",
                    "小羊便签", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return ok;
        }

        private void RegisterHistoryKeys()
        {
            string[] keys = { "recallKey", "previousImageKey", "nextImageKey" };
            string[] labels = { "唤回最近贴图", "上一张贴图", "下一张贴图" };
            var failed = new System.Collections.Generic.List<string>();
            var used = new System.Collections.Generic.HashSet<Keys>();
            if (Enum.TryParse(InitConfig.GetConfigValue("shortcutKey"), true, out Keys captureKey) && captureKey != Keys.None) used.Add(captureKey);
            for (int i = 0; i < keys.Length; i++)
            {
                if (!Enum.TryParse(InitConfig.GetConfigValue(keys[i]), true, out Keys key) || key == Keys.None) continue;
                if (!used.Add(key)) { failed.Add(labels[i] + "：Ctrl+" + key + " 与其他功能重复"); continue; }
                if (RegisterHotKey(Handle, i + 2, HistoryModifiers, (uint)key)) registeredHistory.Add(i + 2);
                else failed.Add(labels[i] + "：Ctrl+" + key);
            }
            if (failed.Count > 0) MessageBox.Show(string.Join("\n", failed) + "\n快捷键已被占用，可在设置中修改。", "贴图快捷键", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }


    }
}
