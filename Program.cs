using KimNotes;
using System;
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