using System;
using System.Drawing;

namespace KimNotes.utils
{
    /// <summary>
    /// 全局缩放系数。Control.DeviceDpi 在窗口句柄创建前读不到真实 DPI（返回 96），
    /// 构造期布局就会整体偏小；这里直接问屏幕 DC，进程感知到的 DPI 是多少就是多少。
    /// </summary>
    internal static class UiDpi
    {
        private static float? factor;

        public static float Factor
        {
            get
            {
                if (factor == null)
                {
                    float f = 1f;
                    try
                    {
                        using (var g = Graphics.FromHwnd(IntPtr.Zero))
                            f = g.DpiX / 96f;
                    }
                    catch { }
                    if (f <= 0f) f = 1f;
                    factor = f;
                }
                return factor.Value;
            }
        }

        /// <summary>
        /// 取某个窗口所在屏幕的缩放系数。多显示器混缩放（如主屏 150% 副屏 100%）时
        /// 用系统 DPI 会把窗口在小屏上撑大、在大屏上裁字，必须按窗口自己的 DPI 量。
        /// </summary>
        public static float FactorFor(IntPtr hwnd)
        {
            uint dpi = 0;
            if (hwnd != IntPtr.Zero)
            {
                try { dpi = Win32ApiHelper.GetDpiForWindow(hwnd); }
                catch { }   // Win10 1607 以前没有这个导出
            }
            if (dpi == 0) return Factor;
            return dpi / 96f;
        }
    }
}
