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
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotKeyHandlerForm()
        {
          
            RegisterGlobalHotKey();
            this.Load += (s, e) => this.Visible = false; // 隐藏窗体
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
            {
                // 热键按住会连续重复触发；截图期间忽略新的热键，避免无限嵌套弹出截图
                if (isCapturing) return;

                // 不在 WndProc 里同步弹模态框，转到消息队列执行
                this.BeginInvoke((Action)(() =>
                {
                    if (isCapturing) return;
                    isCapturing = true;
                    try
                    {
                        var screenshotForm = ScreenshotHelper.CaptureInteractive(InitConfig.GetConfigValue("imagesPath"), Convert.ToBoolean(InitConfig.GetConfigValue("checkBox3")));
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
            UnregisterHotKey(this.Handle, HOTKEY_ID);
            base.Dispose(disposing);
        }

        private void RegisterGlobalHotKey()
        {
            uint hotkeyValue = (uint)Keys.F1; // 默认值
            if (Enum.TryParse<Keys>(InitConfig.GetConfigValue("shortcutKey"), true, out Keys parsedKey))
            {
                hotkeyValue = (uint)parsedKey;
            }
            bool ok = RegisterHotKey(this.Handle, HOTKEY_ID, 0x2, hotkeyValue); // Ctrl+hotkey
            if (!ok)
            {
                MessageBox.Show(
                    $"截图热键 Ctrl+{InitConfig.GetConfigValue("shortcutKey")} 注册失败，可能已被其他程序占用，可在配置中更换。",
                    "小羊便签", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }


    }
}
