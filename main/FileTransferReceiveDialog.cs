using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    internal sealed partial class FileTransferForm
    {
        internal sealed class ReceiveFileDialog : AppWindow
        {
            private readonly IncomingTransfer offer;
            private readonly string initialFolder;
            private readonly Func<IWin32Window, string, CancellationToken, string> chooseFolder;
            private readonly CancellationToken token;
            private readonly Font bodyFont, fileFont;
            private readonly ToolTip fileInfo = new ToolTip();
            private readonly Label feedback;
            private CancellationTokenRegistration cancelRegistration;

            internal string Destination { get; private set; }

            internal ReceiveFileDialog(IncomingTransfer offer, NoteTheme theme, float scale, string initialFolder,
                Func<IWin32Window, string, CancellationToken, string> chooseFolder, CancellationToken token)
            {
                this.offer = offer;
                this.initialFolder = initialFolder;
                this.chooseFolder = chooseFolder;
                this.token = token;
                int P(int value) => (int)Math.Round(value * scale);

                Text = AccessibleName = "接收文件";
                Icon = FormChrome.AppIcon;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                AutoScaleMode = AutoScaleMode.None;
                ClientSize = new Size(P(366), P(210));
                BackColor = theme.Chrome;
                bodyFont = new Font("Microsoft YaHei UI", 9.5f);
                fileFont = new Font(bodyFont.FontFamily, 10.5f, FontStyle.Bold);
                Font = bodyFont;

                var body = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.White,
                    Padding = new Padding(P(16), P(9), P(16), P(13))
                };
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, P(31)));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, P(66)));
                body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, P(34)));
                var sender = CleanDisplayText(offer.Sender).Trim();
                if (sender.Length == 0) sender = "对方设备";
                var from = MakeLabel("receive-sender", sender + " 发来一个文件", theme.Text);
                body.Controls.Add(from, 0, 0);
                fileInfo.SetToolTip(from, from.Text);

                var card = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
                card.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var shape = Rounded(Rectangle.Inflate(card.ClientRectangle, -1, -1), P(9)))
                    using (var fill = new SolidBrush(theme.Body))
                    using (var outline = new Pen(theme.Divider))
                    {
                        e.Graphics.FillPath(fill, shape);
                        e.Graphics.DrawPath(outline, shape);
                    }
                };
                var name = MakeLabel("receive-file-name", CleanDisplayText(offer.Name), theme.Text);
                name.Font = fileFont;
                name.BackColor = theme.Body;
                name.Dock = DockStyle.Top;
                name.Height = P(24);
                string extension = CleanDisplayText(Path.GetExtension(offer.Name));
                var size = MakeLabel("receive-file-size", extension.Length > 1 && extension.Length <= 12
                    ? extension.Substring(1).ToUpperInvariant() + " 文件  ·  " + FormatBytes(offer.Length)
                    : FormatBytes(offer.Length), theme.Icon);
                size.BackColor = theme.Body;
                card.Padding = new Padding(P(13), P(8), P(13), P(8));
                card.Controls.Add(size);
                card.Controls.Add(name);
                body.Controls.Add(card, 0, 1);
                fileInfo.SetToolTip(name, name.Text);

                feedback = MakeLabel("receive-feedback", "", theme.Icon);
                body.Controls.Add(feedback, 0, 2);

                Color accent = Mix(theme.IconHover, Color.Black, .15);
                TransferButton ActionButton(string nameId, string caption, bool primary = false) =>
                    new TransferButton(theme, accent, P(7))
                    {
                        Name = nameId, Text = caption, Primary = primary, Dock = DockStyle.Fill,
                        Margin = Padding.Empty, Cursor = Cursors.Hand
                    };
                var decline = ActionButton("receive-decline", "拒绝");
                decline.DialogResult = DialogResult.Cancel;
                CancelButton = decline;
                var choose = ActionButton("receive-select-folder", "选择保存位置", true);
                choose.Click += (s, e) => ChooseFolder();
                var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
                footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, P(64)));
                footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, P(118)));
                footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                footer.Controls.Add(decline, 0, 0);
                footer.Controls.Add(choose, 2, 0);
                body.Controls.Add(footer, 0, 3);
                Controls.Add(body);
                FormChrome.ApplyStandardHeader(this, Text, theme);
                body.BringToFront();
            }

            private static Label MakeLabel(string name, string text, Color color) => new Label
            {
                Name = name, Text = text, AutoEllipsis = true, UseMnemonic = false,
                ForeColor = color, Margin = Padding.Empty, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
            };

            private static string CleanDisplayText(string text) => new string((text ?? "").Where(c =>
                !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());

            private void ChooseFolder()
            {
                if (token.IsCancellationRequested) return;
                string selected;
                try { selected = chooseFolder(this, initialFolder, token); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                    error is System.Runtime.InteropServices.COMException)
                {
                    feedback.Text = "无法打开文件夹选择器，请重试。";
                    return;
                }
                if (selected != null) ConfirmFolder(selected);
            }

            private void ConfirmFolder(string directory)
            {
                if (token.IsCancellationRequested) return;
                if (!Directory.Exists(directory))
                {
                    feedback.Text = "此文件夹不可用，请选择其他位置。";
                    return;
                }
                var destination = Path.Combine(directory, offer.Name);
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    feedback.Text = "已有同名文件或文件夹，请选择其他位置。";
                    return;
                }
                Destination = destination;
                DialogResult = DialogResult.OK;
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                cancelRegistration = token.Register(() =>
                {
                    try { BeginInvoke((Action)(() => { if (Visible) DialogResult = DialogResult.Cancel; })); }
                    catch (InvalidOperationException) { }
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) cancelRegistration.Dispose();
                base.Dispose(disposing);
                if (disposing) { fileInfo.Dispose(); fileFont?.Dispose(); bodyFont?.Dispose(); }
            }
        }
    }
}
