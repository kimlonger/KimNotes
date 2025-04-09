using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes.utils
{
    public partial class HotKeyHandlerForm : Form
    {
        private const int HOTKEY_ID = 1;
        private string noteConfig = "D:\\kimNotes\\config\\config.txt";
        private string imagePath = "D:\\kimNotes\\images";
        private string hotKey = "F1";
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public HotKeyHandlerForm()
        {
            ReadConfig();
            RegisterGlobalHotKey();
            this.Load += (s, e) => this.Visible = false; // 隐藏窗体
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
            {
                var screenshotForm = ScreenshotHelper.CaptureInteractive(imagePath);
                screenshotForm?.Show();
            }
        }

        protected override void Dispose(bool disposing)
        {
            UnregisterHotKey(this.Handle, HOTKEY_ID);
            base.Dispose(disposing);
        }


        private void ReadConfig()
        {
           
            if (File.Exists(noteConfig))
            {
                foreach (var line in File.ReadAllLines(noteConfig))
                {
                    var parts = line.Split('=');
                    if (parts[0] == "imagesPath") imagePath = parts[1];
                    if (parts[0] == "shortcutKey") hotKey = parts[1];
                }
            }
        }



        private void RegisterGlobalHotKey()
        {
            uint hotkeyValue = (uint)Keys.F1; // 默认值
            if (Enum.TryParse<Keys>(hotKey, true, out Keys parsedKey))
            {
                hotkeyValue = (uint)parsedKey;
            }
            RegisterHotKey(this.Handle, HOTKEY_ID, 0x2, hotkeyValue); // Ctrl+hotkey
        }


    }
}
