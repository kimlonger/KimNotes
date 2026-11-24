using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace KimNotes
{
    internal sealed partial class FileTransferForm
    {
        private Control sendEmpty, sendWaiting, sendFinished;
        private Panel sendContent;
        private Label pickupFileName, pickupWaiting, sendFinishedText;
        private TextBox pickupCodeText;
        private TransferButton sendPrimary, sendSecondary;
        private TableLayoutPanel sendActions;
        private TransferProgressView sendProgress;

        private void BuildSendView()
        {
            var surface = TransferRows(-1, 42);
            sendView.Controls.Add(surface);
            sendContent = new BufferedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, Px(12), 0, Px(10)) };
            surface.Controls.Add(sendContent, 0, 0);

            var selection = new BufferedPanel
            {
                Name = "send-drop-target", Dock = DockStyle.Fill, Margin = Padding.Empty,
                AllowDrop = true, Cursor = Cursors.Hand
            };
            selection.AccessibleName = "拖入文件或文件夹";
            selection.Click += (s, e) => ChooseBundle(false);
            selection.DragEnter += (s, e) => e.Effect = CanStartBundleSend && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            selection.DragDrop += (s, e) =>
            {
                if (CanStartBundleSend && e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = SendBundleAsync(paths);
            };
            selection.Paint += (s, e) => DrawDropTarget(e.Graphics, selection.ClientRectangle);
            var dropLayout = TransferRows(-1, 165, -1);
            dropLayout.Padding = new Padding(Px(13), 0, Px(13), 0);
            dropLayout.Controls.Add(selection, 0, 1);
            sendEmpty = dropLayout;
            sendContent.Controls.Add(sendEmpty);

            sendWaiting = new BufferedPanel { Dock = DockStyle.Fill };
            var waitingRows = TransferRows(44, 14, 58, 26, 28, -1);
            sendWaiting.Controls.Add(waitingRows);
            var fileCard = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(Px(12), Px(3), Px(12), Px(3)) };
            fileCard.Paint += (s, e) => PaintCard(e.Graphics, fileCard.ClientRectangle, Color.White, theme.Divider, Px(9));
            pickupFileName = TransferLabel("", theme.Text);
            pickupFileName.Name = "send-file-summary";
            fileCard.Controls.Add(pickupFileName);
            waitingRows.Controls.Add(fileCard, 0, 0);
            var codeField = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(Px(10), Px(14), Px(10), 0) };
            pickupCodeText = new TextBox
            {
                Name = "send-pickup-code", AccessibleName = "取件码", ReadOnly = true, Dock = DockStyle.Top,
                BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, BackColor = Color.White,
                ForeColor = theme.Text, Font = pickupCodeFont
            };
            codeField.Controls.Add(pickupCodeText);
            waitingRows.Controls.Add(codeField, 0, 2);
            var expires = TransferLabel("5 分钟有效，只能用一次", theme.Icon, ContentAlignment.MiddleCenter);
            waitingRows.Controls.Add(expires, 0, 3);
            pickupWaiting = TransferLabel("", theme.Icon, ContentAlignment.MiddleCenter);
            pickupWaiting.Name = "send-pickup-status";
            waitingRows.Controls.Add(pickupWaiting, 0, 4);
            sendContent.Controls.Add(sendWaiting);

            sendProgress = new TransferProgressView(this);
            sendContent.Controls.Add(sendProgress);

            sendFinished = BuildTransferComplete(out sendFinishedText, out _);
            sendContent.Controls.Add(sendFinished);

            sendPrimary = Button("选择文件", OnSendPrimary, 1, 1, true);
            sendPrimary.Name = "send-primary";
            sendSecondary = Button("选择文件夹", OnSendSecondary, 1, 1);
            sendSecondary.Name = "send-secondary";
            sendActions = TransferActionRow(sendSecondary, sendPrimary);
            surface.Controls.Add(sendActions, 0, 1);
        }

        private void OnSendPrimary()
        {
            if (shortPickupCode != null) CopyPickupCode(shortPickupCode);
            else if (sendComplete) ClearReceiveResult();
            else if (CanBackgroundCurrentTransfer) BackgroundCurrentTransfer();
            else if (operation != null) operation.Cancel();
            else ChooseBundle(false);
        }

        private void OnSendSecondary()
        {
            if (shortPickupCode != null || CanBackgroundCurrentTransfer) operation?.Cancel();
            else ChooseBundle(true);
        }

        private void UpdateSendView()
        {
            bool busy = operation != null && !receiving;
            bool waiting = busy && activeBundle != null && !progressActive;
            bool inProgress = busy && progressActive;
            bool complete = !busy && sendComplete;
            sendEmpty.Visible = !waiting && !inProgress && !complete;
            sendWaiting.Visible = waiting;
            sendProgress.Visible = inProgress;
            sendFinished.Visible = complete;

            bool hasCode = busy && shortPickupCode != null;
            bool background = inProgress && CanBackgroundCurrentTransfer;
            pickupCodeText.Text = shortPickupCode ?? "";
            pickupWaiting.Text = hasCode ? (status.Text.Length == 0 ? "等对方接收" : status.Text) : status.Text;
            pickupFileName.Text = activeBundle == null ? "" : CleanTransferLabel(activeBundle.Name) + "  ·  " + FormatBytes(activeBundle.Length);
            if (inProgress) sendProgress.UpdateTransfer(activeBundle, activePickupRoute, activeTransferredBytes, fileLength, percent);
            if (complete) sendFinishedText.Text = "已发送 " + BundleCounts(activeBundle);

            bool showSecondary = hasCode || background || !busy && !complete;
            sendActions.ColumnStyles[0].Width = showSecondary ? 45 : 0;
            sendSecondary.Visible = showSecondary;
            sendSecondary.Enabled = hasCode || background || CanStartBundleSend;
            sendSecondary.Text = background ? "取消传输" : hasCode ? "取消" : "选择文件夹";
            sendPrimary.Text = hasCode ? "复制取件码" : background ? "后台传输" : busy ? "取消" : complete ? "完成" : "选择文件";
            sendPrimary.Enabled = busy || complete || CanStartBundleSend;
            sendPrimary.Primary = !busy || hasCode || background;
            sendPrimary.Invalidate();
        }

        private void DrawDropTarget(Graphics graphics, Rectangle bounds)
        {
            var card = Rectangle.Inflate(bounds, -1, -1);
            if (card.Width < 4 || card.Height < 4) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(card, Px(11)))
            using (var fill = new SolidBrush(Mix(theme.Body, Color.White, .57)))
            using (var outline = new Pen(theme.Divider) { DashStyle = DashStyle.Dash })
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(outline, path);
            }
            int center = bounds.Width / 2, top = Math.Max(Px(7), bounds.Height / 2 - Px(61));
            var icon = new Rectangle(center - Px(22), top, Px(44), Px(44));
            PaintCard(graphics, icon, theme.Body, Color.Empty, Px(21));
            using (var ink = new Pen(accent, Math.Max(1.35f, scale * 1.6f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                graphics.DrawLines(ink, new[] { new Point(center - Px(5), top + Px(19)), new Point(center, top + Px(14)),
                    new Point(center + Px(5), top + Px(19)) });
                graphics.DrawLine(ink, center, top + Px(14), center, top + Px(26));
                graphics.DrawLine(ink, center - Px(6), top + Px(29), center + Px(6), top + Px(29));
            }
            const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(graphics, "拖入文件或文件夹", headingFont, new Rectangle(0, top + Px(54), bounds.Width, Px(28)),
                theme.Text, centered);
            TextRenderer.DrawText(graphics, "选好后生成取件码", bodyFont, new Rectangle(0, top + Px(83), bounds.Width, Px(23)),
                theme.Icon, centered);
        }

        private TableLayoutPanel TransferRows(params int[] heights)
        {
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, Margin = Padding.Empty };
            foreach (int height in heights)
                rows.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, Px(height)));
            return rows;
        }

        private TableLayoutPanel TransferActionRow(TransferButton left, TransferButton right)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            if (left != null)
            {
                left.Margin = new Padding(0, 0, Px(6), 0);
                left.Dock = DockStyle.Fill;
                row.Controls.Add(left, 0, 0);
            }
            else row.ColumnStyles[0].Width = 0;
            right.Margin = Padding.Empty;
            right.Dock = DockStyle.Fill;
            row.Controls.Add(right, 1, 0);
            return row;
        }

        private static Label TransferLabel(string text, Color color, ContentAlignment align = ContentAlignment.MiddleLeft) =>
            new Label { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = color, TextAlign = align,
                AutoEllipsis = true, UseMnemonic = false };

        private Panel BuildTransferComplete(out Label headline, out Label description)
        {
            var card = new BufferedPanel { Dock = DockStyle.Fill };
            var rows = TransferRows(-1, 66, 36, 29, -1);
            card.Controls.Add(rows);
            var mark = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            mark.Paint += (s, e) =>
            {
                int x = mark.Width / 2, y = mark.Height / 2;
                var circle = new Rectangle(x - Px(26), y - Px(26), Px(52), Px(52));
                PaintCard(e.Graphics, circle, accent, Color.Empty, Px(28));
                using (var pen = new Pen(Color.White, Math.Max(2.2f, scale * 2.5f))
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    e.Graphics.DrawLines(pen, new[] { new Point(x - Px(8), y), new Point(x - Px(3), y + Px(5)), new Point(x + Px(8), y - Px(7)) });
            };
            rows.Controls.Add(mark, 0, 1);
            headline = TransferLabel("", theme.Text, ContentAlignment.MiddleCenter);
            headline.Font = completeFont;
            rows.Controls.Add(headline, 0, 2);
            description = TransferLabel("", theme.Icon, ContentAlignment.MiddleCenter);
            rows.Controls.Add(description, 0, 3);
            return card;
        }

        private sealed class TransferProgressView : BufferedPanel
        {
            private readonly FileTransferForm form;
            private readonly Label heading, details, path, moreFiles;
            private readonly Label[] fileNames = new Label[3], fileStates = new Label[3];
            private readonly Panel track;
            private int percent;
            private FileTransferBundle displayed;
            private string displayedFile;
            private FileTransferBundle.Entry[] progressFiles = Array.Empty<FileTransferBundle.Entry>();

            internal TransferProgressView(FileTransferForm form)
            {
                this.form = form;
                Dock = DockStyle.Fill;
                var rows = form.TransferRows(4, 43, 9, 22, -1, 22);
                Controls.Add(rows);
                var metrics = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
                heading = TransferLabel("", form.theme.Text);
                heading.Name = "transfer-percent";
                heading.Font = form.pickupCodeFont;
                heading.Dock = DockStyle.Left;
                heading.Width = form.Px(104);
                details = TransferLabel("", form.theme.Icon, ContentAlignment.MiddleRight);
                metrics.Controls.Add(details);
                metrics.Controls.Add(heading);
                rows.Controls.Add(metrics, 0, 1);
                track = new BufferedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, form.Px(2), 0, form.Px(2)) };
                track.Paint += PaintProgress;
                rows.Controls.Add(track, 0, 2);
                path = TransferLabel("", form.theme.Icon);
                path.Name = "transfer-route";
                rows.Controls.Add(path, 0, 3);
                var files = form.TransferRows(24, 24, 24, 19, -1);
                for (int i = 0; i < fileNames.Length; i++)
                {
                    var line = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 1, 0, 0) };
                    var name = TransferLabel("", form.theme.Text);
                    fileNames[i] = name;
                    line.Paint += (s, e) =>
                    {
                        if (name.Text.Length > 0)
                        {
                            using (var pen = new Pen(form.theme.Divider)) e.Graphics.DrawLine(pen, 0, 0, line.Width, 0);
                        }
                    };
                    fileStates[i] = TransferLabel("", form.theme.Icon, ContentAlignment.MiddleRight);
                    fileStates[i].Dock = DockStyle.Right;
                    fileStates[i].Width = form.Px(64);
                    fileNames[i].Padding = new Padding(0, 0, form.Px(7), 0);
                    line.Controls.Add(fileNames[i]);
                    line.Controls.Add(fileStates[i]);
                    files.Controls.Add(line, 0, i);
                }
                moreFiles = TransferLabel("", form.theme.Icon);
                files.Controls.Add(moreFiles, 0, 3);
                rows.Controls.Add(files, 0, 4);
                rows.Controls.Add(TransferLabel("收起后会继续传输", form.theme.Icon, ContentAlignment.MiddleCenter), 0, 5);
            }

            internal void UpdateTransfer(FileTransferBundle bundle, string route, long moved, long total, int finished, string filename = null)
            {
                percent = finished;
                heading.Text = finished + "%";
                details.Text = FormatBytes(moved) + " / " + FormatBytes(total);
                path.Text = route ?? "";
                if (displayed != bundle || displayedFile != filename)
                {
                    displayed = bundle;
                    displayedFile = filename;
                    progressFiles = bundle?.Entries.Where(entry => !entry.IsDirectory).Take(fileNames.Length).ToArray()
                        ?? Array.Empty<FileTransferBundle.Entry>();
                    for (int i = 0; i < fileNames.Length; i++)
                    {
                        string name = i < progressFiles.Length ? progressFiles[i].Path : i == 0 && progressFiles.Length == 0
                            ? bundle?.Name ?? filename : "";
                        fileNames[i].Text = CleanTransferLabel(name);
                        fileNames[i].Parent.Invalidate();
                    }
                    int remaining = (bundle?.FileCount ?? 0) - progressFiles.Length;
                    moreFiles.Text = remaining > 0 ? "另有 " + remaining + " 个文件" : "";
                }
                long earlier = 0;
                for (int i = 0; i < fileStates.Length; i++)
                {
                    if (i >= progressFiles.Length) { fileStates[i].Text = ""; continue; }
                    long length = progressFiles[i].Length;
                    long bytes = Math.Max(0, Math.Min(length, moved - earlier));
                    fileStates[i].Text = length == 0 ? "0 B" : bytes == length ? "已传输"
                        : bytes == 0 ? "等待中" : (int)(bytes * 100d / length) + "%";
                    earlier += length;
                }
                track.Invalidate();
            }

            private void PaintProgress(object sender, PaintEventArgs e)
            {
                var bounds = track.ClientRectangle;
                if (bounds.Height < 2 || bounds.Width < 2) return;
                PaintCard(e.Graphics, bounds, form.theme.Body, Color.Empty, bounds.Height / 2);
                if (percent > 0)
                    PaintCard(e.Graphics, new Rectangle(0, 0, bounds.Width * percent / 100, bounds.Height),
                        form.accent, Color.Empty, bounds.Height / 2);
            }
        }

        private static string BundleCounts(FileTransferBundle bundle)
        {
            if (bundle.FolderCount == 0) return bundle.FileCount + " 个文件";
            if (bundle.FileCount == 0) return bundle.FolderCount + " 个文件夹";
            return bundle.FileCount + " 个文件、" + bundle.FolderCount + " 个文件夹";
        }
    }
}
