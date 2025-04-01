using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KimNotes
{
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

            // 使用全局的 ApplicationContext
            AppContext = new MyApplicationContext();
            Application.Run(AppContext);
        }
    }
}
