using KimNotes;
using System;
using System.Windows.Forms;

internal static class Program
{
    public static MyApplicationContext AppContext;

    /// <summary>
    /// 应用程序的主入口点。
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 设置DPI感知模式，让整个应用程序适应高分辨率屏幕
        SetDpiAwareness();
        
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 到期日期设置
        DateTime expiryDate = new DateTime(2025, 09, 13); 

        // 检查当前日期是否晚于或等于到期日期
        if (DateTime.Now.Date >= expiryDate.Date)
        {
            // 如果是，显示提示信息并退出程序
            MessageBox.Show("软件试用已结束，请访问官网http://kimlulu.com下载新版！", "试用到期", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return; // 退出程序
        }

        // 使用全局的 ApplicationContext
        AppContext = new MyApplicationContext();
        Application.Run(AppContext);
    }
    
    /// <summary>
    /// 设置应用程序的DPI感知模式
    /// </summary>
    private static void SetDpiAwareness()
    {
        try
        {
            // 尝试设置DPI感知模式 - 适用于Windows 8.1及以上
            if (Environment.OSVersion.Version.Major >= 6 && Environment.OSVersion.Version.Minor >= 3)
            {
                Win32ApiHelper.SetProcessDpiAwareness(Win32ApiHelper.PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
            }
            // 对于Windows Vista/7/8，使用旧的DPI感知API
            else if (Environment.OSVersion.Version.Major >= 6)
            {
                Win32ApiHelper.SetProcessDPIAware();
            }
        }
        catch (Exception)
        {
            // 忽略错误，如果设置失败则回退到默认行为
        }
    }
}