using System.Windows.Forms;

namespace KimNotes
{
    internal class MyApplicationContext : ApplicationContext
    {
        private int openFormCount = 0;
        public MyApplicationContext()
        {
            // 创建并显示初始窗体
            AddNewForm();
        }
        public void AddNewForm(string fileName = null)
        {
            Form1 form = new Form1(fileName);
            form.FormClosed += OnFormClosed;
            openFormCount++;
            form.Show();
        }

        public void AddNewForm2()
        {
            Form2 form = new Form2();
            form.FormClosed += OnFormClosed;
            openFormCount++;
            form.Show();
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            openFormCount--;
            // 检查是否还有其他打开的窗体
            if (openFormCount == 0)
            {
                ExitThread(); // 所有窗体都关闭时退出应用程序
            }
        }
    }
}