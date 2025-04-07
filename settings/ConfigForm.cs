using System;
using System.IO; // 添加对System.IO命名空间的引用，以便操作文件和文件夹
using System.Windows.Forms;

namespace KimNotes.settings
{
    public partial class ConfigForm : Form
    {
        // noteConfig变量用于存储配置文件的路径
        private string noteConfig = "D:\\kimNotes\\config\\config.txt";

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
            // 开启窗体的按键预览功能，以便监听按键操作
            this.KeyPreview = true;
            this.KeyDown += ConfigForm_KeyDown;

            if (!File.Exists(noteConfig))
            {
                // 如果配置文件不存在，则创建文件夹和文件，并调用数据初始化方法
                Directory.CreateDirectory(Path.GetDirectoryName(noteConfig));
                File.Create(noteConfig).Close();
                InitData();
            }
            else
            {
                // 如果配置文件存在，则直接调用数据读取方法
                ReadData();
            }
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

        // 初始化默认配置数据，并写入配置文件中的方法 
        public void InitData()
        {
            checkBox1.Checked = true;
            checkBox2.Checked = true;
            textBox1.Text = "F1";
            textBox3.Text = "D:\\kimNotes\\notes";
            textBox4.Text = "D:\\kimNotes\\images";
            SaveData(); // 调用保存数据到配置文件中的方法
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
            // 启动新的进程实例
            System.Diagnostics.Process.Start(applicationPath);
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
                sw.WriteLine($"shortcutKey={textBox1.Text}");
                sw.WriteLine($"notesPath={textBox3.Text}");
                sw.WriteLine($"imagesPath={textBox4.Text}");
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            InitData();
            // 获取当前应用程序的路径
            string applicationPath = Application.ExecutablePath;
            // 启动新的进程实例
            System.Diagnostics.Process.Start(applicationPath);
            // 关闭当前应用程序
            Application.Exit();
        }
    }
}