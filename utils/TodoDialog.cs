using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes.utils
{
    /// <summary>
    /// 「插入待办」对话框：输入内容 + 可选提醒时间，生成规范的待办行。
    /// </summary>
    internal class TodoDialog : Form
    {
        private readonly TextBox txtContent;
        private readonly CheckBox chkRemind;
        private readonly DateTimePicker dtp;
        private readonly Button btnOk;
        private readonly Button btnCancel;

        public string TodoText => txtContent.Text.Trim();
        public DateTime? Due => chkRemind.Checked ? (DateTime?)dtp.Value : null;

        public TodoDialog()
        {
            Text = "插入待办";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(320, 150);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.White;

            var lbl = new Label { Text = "待办内容", Location = new Point(16, 16), AutoSize = true };
            txtContent = new TextBox { Location = new Point(16, 38), Width = 288 };

            chkRemind = new CheckBox { Text = "设置提醒时间", Location = new Point(16, 70), AutoSize = true };
            dtp = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd HH:mm",
                ShowUpDown = false,
                Location = new Point(130, 68),
                Width = 174,
                Enabled = false
            };
            chkRemind.CheckedChanged += (s, e) => dtp.Enabled = chkRemind.Checked;

            btnOk = new Button { Text = "插入", DialogResult = DialogResult.OK, Location = new Point(148, 106), Width = 76 };
            btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(228, 106), Width = 76 };
            btnOk.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtContent.Text))
                {
                    MessageBox.Show("请输入待办内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    DialogResult = DialogResult.None;
                }
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.AddRange(new Control[] { lbl, txtContent, chkRemind, dtp, btnOk, btnCancel });
        }
    }
}
