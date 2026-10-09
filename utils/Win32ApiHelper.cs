// Helpers/Win32ApiHelper.cs
using System;
using System.Runtime.InteropServices;

namespace KimNotes
{
    public static class Win32ApiHelper
    {
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);

        public const int SB_BOTH = 3;

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        
        // DPI感知相关API
        
        /// <summary>
        /// DPI感知级别枚举
        /// </summary>
        public enum PROCESS_DPI_AWARENESS
        {
            PROCESS_DPI_UNAWARE = 0,           // 不感知DPI变化，总是使用系统默认DPI缩放
            PROCESS_SYSTEM_DPI_AWARE = 1,      // 系统DPI感知，在应用程序启动时感知系统DPI
            PROCESS_PER_MONITOR_DPI_AWARE = 2  // 每个显示器DPI感知，每个显示器单独处理DPI缩放
        }
        
        /// <summary>
        /// 设置进程的DPI感知模式 (Windows 8.1及以上)
        /// </summary>
        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(PROCESS_DPI_AWARENESS awareness);
        
        /// <summary>
        /// 设置进程为DPI感知 (Windows Vista/7/8)
        /// </summary>
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        
        /// <summary>
        /// 获取指定显示器的DPI
        /// </summary>
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        /// <summary>
        /// 获取窗口所在显示器的DPI（Win10 1607+），多屏混缩放时按窗口取
        /// </summary>
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);
        
        /// <summary>
        /// 获取当前工作的DPI值
        /// </summary>
        public static float GetScalingFactor()
        {
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    return g.DpiX / 96.0f; // 96是标准DPI
                }
            }
            catch
            {
                return 1.0f; // 如果失败，返回1.0作为默认值
            }
        }
    }
}