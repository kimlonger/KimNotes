using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes.utils
{
    public partial class HotKeyHandlerForm : Form
    {
        private const int HOTKEY_ID = 1;
        private volatile bool isCapturing;
        private bool captureQueued;
        private readonly Func<IntPtr, bool> register;
        private readonly Action<IntPtr> unregister;
        private IntPtr registeredHandle;
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotKeyHandlerForm() : this(null, null) { }

        internal HotKeyHandlerForm(Func<IntPtr, bool> register, Action<IntPtr> unregister)
        {
            this.register = register ?? RegisterGlobalHotKey;
            this.unregister = unregister ?? (hwnd => UnregisterHotKey(hwnd, HOTKEY_ID));
            this.Load += (s, e) => this.Visible = false; // 隐藏窗体
            var createdHandle = Handle;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (register != null && register(Handle)) registeredHandle = Handle;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            captureQueued = false;
            if (registeredHandle != IntPtr.Zero)
            {
                unregister(registeredHandle);
                registeredHandle = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
            {
                // 热键按住会连续重复触发；截图期间忽略新的热键，避免无限嵌套弹出截图
                if (isCapturing || captureQueued || IsDisposed || Disposing || !IsHandleCreated) return;
                captureQueued = true;

                // 不在 WndProc 里同步弹模态框，转到消息队列执行
                this.BeginInvoke((Action)(() =>
                {
                    captureQueued = false;
                    if (isCapturing || IsDisposed || Disposing || !IsHandleCreated) return;
                    isCapturing = true;
                    try
                    {
                        var screenshotForm = ScreenshotHelper.CaptureInteractive(InitConfig.GetConfigValue("imagesPath"), Storage.Incognito);
                        screenshotForm?.Show();
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


    }
}
