using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace KimNotes
{
    public partial class Form1 : Form
    {

        private Point mouseDownLocation;
        public Form1()
        {
            InitializeComponent();
            // Set the TextBox to resize with the form
            richTextBox1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            // 设置窗体的背景颜色
            this.BackColor = Color.FromArgb(255, 242, 171);
            // 设置窗体为无边框
            this.FormBorderStyle = FormBorderStyle.None;
            // 设置 TextBox 的背景颜色
            richTextBox1.BackColor = Color.FromArgb(255, 247, 209);
            richTextBox1.BorderStyle=BorderStyle.None;
            // 隐藏滚动条
            this.MouseDown += new MouseEventHandler(Form1_MouseDown);
            this.MouseMove += new MouseEventHandler(Form1_MouseMove);

            // 设置圆角
            SetFormRoundCorners(15); // 例如，圆角半径设为30
        }

       


        private void Form1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                mouseDownLocation = e.Location;
            }
        }

        private void Form1_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                this.Left += e.X - mouseDownLocation.X;
                this.Top += e.Y - mouseDownLocation.Y;
            }
        }

        private void SetFormRoundCorners(int radius)
        {
            GraphicsPath path = new GraphicsPath();
            path.StartFigure();

            path.AddArc(new Rectangle(0, 0, radius, radius), 180, 90); // 左上角
            path.AddArc(new Rectangle(this.Width - radius, 0, radius, radius), 270, 90); // 右上角
            path.AddArc(new Rectangle(this.Width - radius, this.Height - radius, radius, radius), 0, 90); // 右下角
            path.AddArc(new Rectangle(0, this.Height - radius, radius, radius), 90, 90); // 左下角

            path.CloseFigure();

            this.Region = new Region(path);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            // 确保在调整大小时保持圆角
            SetFormRoundCorners(15);
        }
    }
}
