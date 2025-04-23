using KimNotes;
using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;

internal static class Program
{
    public static MyApplicationContext AppContext;
    
    // 用于设置DPI感知模式
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    /// <summary>
    /// 应用程序的主入口点。
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 在应用程序启动时设置DPI感知
        try
        {
            // 尝试设置为每显示器DPI感知（Windows 8.1及以上）
            if (Environment.OSVersion.Version.Major > 6 || 
                (Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor >= 3))
            {
                SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
            }
            else
            {
                // 旧版Windows
                SetProcessDPIAware();
            }
        }
        catch
        {
            // 忽略错误，继续运行
        }
        
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
}