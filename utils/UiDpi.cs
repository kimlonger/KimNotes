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
    }
}
