using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO; // 添加对System.IO命名空间的引用，以便操作文件和文件夹
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes.settings
{
    public partial class ConfigForm : Form
    {
        private static string appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KimNotes"
        );
        private static string noteConfig = Path.Combine(appDataPath, "config.txt");

        // 构造函数，初始化组件
        public ConfigForm()
        {
            InitializeComponent();
        }

        // 窗体加载时执行的事件处理方法
        private void ConfigForm_Load(object sender, EventArgs e)
        {
            // 将textBox1设置为只读，用户不能直接在其中输入内容
            textBox1.ReadOnly = true;
            // 为textBox3和textBox4设置双击事件处理方法，用于选择文件夹路径
            textBox3.DoubleClick += TextBox3_DoubleClick;
            textBox4.DoubleClick += TextBox4_DoubleClick;
            textBox3.ReadOnly = true;
            textBox4.ReadOnly = true;
            //显示版本信息
            label5.Text = "版本号: " + Application.ProductVersion;
            // 开启窗体的按键预览功能，以便监听按键操作
            this.KeyPreview = true;
            this.KeyDown += ConfigForm_KeyDown;
            // 设置窗体位置
            SetFormPosition();
            ReadData();
        }

        // 设置窗体位置的方法
        private void SetFormPosition()
        {
            // 获取屏幕的工作区域
            var screenBounds = Screen.PrimaryScreen.WorkingArea;

            // 计算窗体的位置
            int x = screenBounds.Width * 3 / 4 - this.Width;
            int y = (screenBounds.Height - this.Height) / 2;

            // 设置窗体的位置
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(x, y);
        }

        // 处理窗体上任何按键操作的事件处理方法
        private void ConfigForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (textBox1.Focused)
            {
                if (e.KeyCode != Keys.Back && e.KeyCode != Keys.Delete && e.KeyCode != Keys.Enter)
                {
                    textBox1.Text = e.KeyCode.ToString();
                }
                else if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                {
                    textBox1.Text = "请按下按键";
                }
            }
        }

        // 处理textBox3双击事件的方法，用于选择文件夹路径
        private void TextBox3_DoubleClick(object sender, EventArgs e)
        {
            ChooseFolderPath(textBox3);
        }

        // 处理textBox4双击事件的方法，同样用于选择文件夹路径
        private void TextBox4_DoubleClick(object sender, EventArgs e)
        {
            ChooseFolderPath(textBox4);
        }

        // 封装了一个选择文件夹路径并赋值给相应文本框的方法 
        private void ChooseFolderPath(TextBox textBox)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    textBox.Text = folderDialog.SelectedPath;
                    textBox.SelectionStart = textBox.Text.Length;
                    textBox.SelectionLength = 0;
                }
            }
        }



        // 从配置文件中读取配置数据并设置到界面上的方法 
        private void ReadData()
        {
            string[] lines = File.ReadAllLines(noteConfig);
            foreach (string line in lines)
            {
                string[] parts = line.Split('=');
                switch (parts[0])
                {
                    case "checkBox1":
                        checkBox1.Checked = Convert.ToBoolean(parts[1]);
                        break;
                    case "checkBox2":
                        checkBox2.Checked = Convert.ToBoolean(parts[1]);
                        break;
                    case "checkBox3":
                        checkBox3.Checked = Convert.ToBoolean(parts[1]);
                        break;
                    case "shortcutKey":
                        textBox1.Text = parts[1];
                        break;
                    case "notesPath":
                        textBox3.Text = parts[1];
                        break;
                    case "imagesPath":
                        textBox4.Text = parts[1];
                        break;
                }
            }
        }

        // 获取当前界面上控件的值，并保存到配置文件中
        private void button7_Click(object sender, System.EventArgs e)
        {
            SaveData();

            // 获取当前应用程序的路径
            string applicationPath = Application.ExecutablePath;
            // 启动新的进程实例，并传递配置更新标志
            System.Diagnostics.Process.Start(applicationPath, "--config-update");
            // 关闭当前应用程序
            Application.Exit();
        }

        // 将当前界面上控件的值保存到配置文件中 
        private void SaveData()
        {
            using (StreamWriter sw = new StreamWriter(noteConfig))
            {
                sw.WriteLine($"checkBox1={checkBox1.Checked}");
                sw.WriteLine($"checkBox2={checkBox2.Checked}");
                sw.WriteLine($"checkBox3={checkBox3.Checked}");
                sw.WriteLine($"shortcutKey={textBox1.Text}");
                sw.WriteLine($"notesPath={textBox3.Text}");
                sw.WriteLine($"imagesPath={textBox4.Text}");
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            InitConfig.InitData();
            // 获取当前应用程序的路径
            string applicationPath = Application.ExecutablePath;
            // 启动新的进程实例，并传递配置更新标志
            System.Diagnostics.Process.Start(applicationPath, "--config-update");
            // 关闭当前应用程序
            Application.Exit();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            List<string> list = RemoteCallUtils.getDownloadAppUrl();
            if (list.Count > 0)
            {
                string newVersion = list[0];
                string downloadUrl = list[1];
                // 弹窗提示用户
                DialogResult result = MessageBox.Show(
                    $"检测到新版本 {newVersion}，是否自动更新？",
                    "小羊便签",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.None);
                if (result == DialogResult.Yes)
                {
                    try
                    {
                        // 下载新版本安装包到临时目录
                        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KimNotesUpdate.msi");
                        using (var client = new System.Net.WebClient())
                        {
                            client.DownloadFile(downloadUrl, tempPath);
                        }
                        // 启动安装包
                        Process.Start(tempPath);
                        Environment.Exit(0); // 退出当前进程
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("自动更新失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("当前已是最新版本", "小羊便签", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e)
        {
            //先检测配置有无用户名和密码
            //如果没有 则弹出一个页面 让用户输入用户名和密码
            //如果有 则提示 自勾选开始，会将截图和笔记上传到服务器，是否继续？ 继续不做操作，取消则取消勾选

            //勾选之后，弹出一个页面 让用户输入 用户名 和密码
            //存储到配置文件中  以及更新到数据库中
        }

        private void button2_Click(object sender, EventArgs e)
        {
            //从指定地址文件中读取用户名和密码
            //根据用户名和密码 进行登录
            //调用下载链接  下载对应的便签记录 和截图到本地
            //如果有相同的则以本地为主
        }
    }
}