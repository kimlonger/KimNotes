using KimNotes;
using System;
using System.IO;
using System.Windows.Forms;
using System.Runtime.InteropServices;

internal static class Program
{
    public static MyApplicationContext AppContext;
    /// <summary>
    /// 应用程序的主入口点。
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 全局异常兜底，避免未处理异常直接闪退
        Application.ThreadException += (s, e) =>
        {
            LogError(e.Exception);
            MessageBox.Show("程序发生异常：" + e.Exception.Message, "小羊便签",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogError(e.ExceptionObject as Exception);
            MessageBox.Show("程序发生严重异常：" + (e.ExceptionObject as Exception)?.Message, "小羊便签",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        TryEnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        //// 到期日期设置
        //DateTime expiryDate = new DateTime(2025, 09, 13); 

        //// 检查当前日期是否晚于或等于到期日期
        //if (DateTime.Now.Date >= expiryDate.Date)
        //{
        //    // 如果是，显示提示信息并退出程序
        //    MessageBox.Show("软件试用已结束，请访问官网http://kimlulu.com下载新版！", "试用到期", MessageBoxButtons.OK, MessageBoxIcon.Information);
        //    return; // 退出程序
        //}
        // 使用全局的 ApplicationContext
        AppContext = new MyApplicationContext();
        Application.Run(AppContext);
    }

    private static void TryEnableDpiAwareness()
    {
        try
        {
            Win32ApiHelper.SetProcessDpiAwareness(
                Win32ApiHelper.PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
        }
        catch (DllNotFoundException)
        {
            Win32ApiHelper.SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            Win32ApiHelper.SetProcessDPIAware();
        }
        catch
        {
            // Ignore if DPI awareness cannot be set.
        }
    }

    // 异常落盘，便于事后定位（%AppData%\KimNotes\error.log）
    private static void LogError(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KimNotes");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + ex + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
