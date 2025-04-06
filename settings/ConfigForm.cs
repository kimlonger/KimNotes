
using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes.settings
{
    public partial class ConfigForm : Form
    {

        private string config = "D:\\kimNotes\\config\\config.txt";


        public ConfigForm()
        {
            InitializeComponent();
            
            
        }

        private void ConfigForm_Load(object sender, System.EventArgs e)
        {
            // 设置文件夹选择文本框双击事件
            textBox3.DoubleClick += TextBox3_DoubleClick;
            textBox4.DoubleClick += TextBox4_DoubleClick;
        }

        private void TextBox3_DoubleClick(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    textBox3.Text = folderDialog.SelectedPath;
                    // 将光标设置在文本末尾
                    textBox3.SelectionStart = textBox3.Text.Length;
                    textBox3.SelectionLength = 0;
                }
            }
        }

        private void TextBox4_DoubleClick(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    textBox4.Text = folderDialog.SelectedPath;
                    // 将光标设置在文本末尾
                    textBox4.SelectionStart = textBox4.Text.Length;
                    textBox4.SelectionLength = 0;
                }
            }
        }


        private void button6_Click(object sender, System.EventArgs e)
        {

        }

        private void button7_Click(object sender, System.EventArgs e)
        {

        }

    }
}
