using KimNotes;
using System;
using System.IO;
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

        // Let .NET Framework initialize PerMonitorV2 from App.config. Calling the
        // older SetProcessDpiAwareness API first permanently locks the process to V1.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 使用全局的 ApplicationContext
        AppContext = new MyApplicationContext();
        Application.Run(AppContext);
    }

    // 异常落盘，便于事后定位（%AppData%\KimNotes\error.log）
    internal static void LogError(Exception ex)
    {
        try
        {
            if (KimNotes.utils.Storage.Incognito) return;
            string dir = KimNotes.utils.Storage.Root;
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
