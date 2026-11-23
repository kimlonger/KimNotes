using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    internal sealed class FileTransferForm : AppWindow
    {
        private readonly float scale = UiDpi.Factor;
        private readonly NoteTheme theme = NoteTheme.Current();
        private readonly Font bodyFont, headingFont;
        private readonly Icon appIcon;
        private readonly FlowLayoutPanel tools;
        private readonly Panel transfer, sendView, receiveView, progress, deviceBody;
        private readonly Label status, emptyDevices;
        private readonly ListBox devices;
        private readonly TransferButton back, sendTab, receiveTab, refresh, chooseFile, invite, startReceive, openInvite, cancel;
        private readonly Color accent;
        private CancellationTokenSource operation;
        private string invitation;
        private int percent;
        private bool receiving;
        private long fileLength;

        internal FileTransferForm()
        {
            Text = "桌面工具";
            AccessibleName = Text;
            appIcon = Icon.ExtractAssociatedIcon(typeof(FileTransferForm).Assembly.Location);
            if (appIcon != null) Icon = appIcon;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Px(400), Px(414));
            MinimumSize = new Size(Px(350), Px(375));
            BackColor = Color.White;
            bodyFont = new Font("Microsoft YaHei UI", 9.5f);
            headingFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            Font = bodyFont;
            accent = Mix(theme.IconHover, Color.Black, .15);

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            Controls.Add(content);
            FormChrome.ApplyStandardHeader(this, Text, theme);
            tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(Px(18))
            };
            tools.SizeChanged += (s, e) =>
            {
                foreach (Control tool in tools.Controls)
                    tool.Width = Math.Max(1, tools.ClientSize.Width - tools.Padding.Horizontal);
            };
            content.Controls.Add(tools);
            AddTool("文件互传", "发送或接收文件", OpenFileTransfer);

            transfer = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(Px(16), Px(10), Px(16), Px(10)) };
            content.Controls.Add(transfer);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(34)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(43)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(29)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(40)));
            transfer.Controls.Add(layout);

            back = Button("返回", () => ShowTools(), Px(33), Px(28));
            back.LinkStyle = true;
            back.DrawContent = (g, r) => DrawArrow(g, r, theme.Icon, -1);
            var title = new Label { Text = "文件互传", Font = headingFont, ForeColor = theme.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            var titleRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var backSlot = new Panel { Dock = DockStyle.Left, Width = Px(38) };
            backSlot.Controls.Add(back);
            titleRow.Controls.Add(title);
            titleRow.Controls.Add(backSlot);
            layout.Controls.Add(titleRow, 0, 0);

            sendTab = Button("发送", () => SetReceiveMode(false), Px(75), Px(30), true);
            receiveTab = Button("接收", () => SetReceiveMode(true), Px(75), Px(30));
            var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            tabs.Controls.Add(sendTab);
            tabs.Controls.Add(receiveTab);
            layout.Controls.Add(tabs, 0, 1);

            var workArea = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            sendView = new Panel { Dock = DockStyle.Fill };
            receiveView = new Panel { Dock = DockStyle.Fill, Visible = false };
            workArea.Controls.Add(sendView);
            workArea.Controls.Add(receiveView);
            layout.Controls.Add(workArea, 0, 2);

            var deviceHeading = new Panel { Dock = DockStyle.Top, Height = Px(33) };
            var deviceLabel = new Label { Text = "接收设备", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Text };
            refresh = Button("刷新", RefreshDevices, Px(55), Px(26));
            refresh.LinkStyle = true;
            refresh.Dock = DockStyle.Right;
            deviceHeading.Controls.Add(deviceLabel);
            deviceHeading.Controls.Add(refresh);
            devices = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = Px(50), IntegralHeight = false, BackColor = Color.White, AccessibleName = "接收设备"
            };
            devices.DrawItem += DrawDevice;
            devices.SelectedIndexChanged += (s, e) => UpdateButtons();
            deviceBody = new Panel { Dock = DockStyle.Top, Height = Px(62), Padding = new Padding(1), BackColor = theme.Divider };
            deviceBody.Resize += (s, e) =>
            {
                using (var outline = Rounded(deviceBody.ClientRectangle, Px(8)))
                {
                    var previous = deviceBody.Region;
                    deviceBody.Region = new Region(outline);
                    previous?.Dispose();
                }
            };
            emptyDevices = new Label
            {
                Dock = DockStyle.Fill, Text = "请让对方开启接收，再刷新设备。", TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = theme.Icon, BackColor = Color.White, Padding = new Padding(Px(8))
            };
            deviceBody.Controls.Add(devices);
            deviceBody.Controls.Add(emptyDevices);
            sendView.Controls.Add(deviceBody);
            sendView.Controls.Add(deviceHeading);
            sendView.Resize += (s, e) => ResizeDeviceList();

            var receiverHeading = new Label
            {
                Text = "本机设备", Dock = DockStyle.Top, Height = Px(33), TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Text
            };
            var receiverCard = new Panel { Dock = DockStyle.Top, Height = Px(63), BackColor = theme.Body, Padding = new Padding(Px(12), Px(8), Px(12), Px(6)) };
            var receiverName = new Label
            {
                Text = Environment.MachineName, Dock = DockStyle.Top, Height = Px(24), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Text
            };
            var receiverHint = new Label
            {
                Text = "开启接收后，对方就能找到这台电脑。", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Icon
            };
            receiverCard.Controls.Add(receiverHint);
            receiverCard.Controls.Add(receiverName);
            receiveView.Controls.Add(receiverCard);
            receiveView.Controls.Add(receiverHeading);
            startReceive = Button("开始接收", ReceiveFiles, Px(124), Px(35), true);
            startReceive.Dock = DockStyle.Right;
            openInvite = Button("打开邀请", ReceiveInvitation, Px(104), Px(35));
            openInvite.Dock = DockStyle.Left;
            openInvite.LinkStyle = true;

            var statusRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Icon, AutoEllipsis = true };
            statusRow.Controls.Add(status);
            progress = new Panel { Dock = DockStyle.Bottom, Height = Px(2), BackColor = theme.Body, Visible = false };
            progress.Paint += (s, e) =>
            {
                using (var fill = new SolidBrush(accent)) e.Graphics.FillRectangle(fill, 0, 0, progress.Width * percent / 100, progress.Height);
            };
            statusRow.Controls.Add(progress);
            layout.Controls.Add(statusRow, 0, 3);

            var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            invite = Button("邀请其他账号", SendInvitation, Px(126), Px(35));
            invite.LinkStyle = true;
            invite.Dock = DockStyle.Left;
            chooseFile = Button("选择文件", SendSelected, Px(126), Px(35), true);
            chooseFile.Dock = DockStyle.Right;
            cancel = Button("取消", () => operation?.Cancel(), Px(75), Px(35));
            cancel.Dock = DockStyle.Right;
            footer.Controls.Add(invite);
            footer.Controls.Add(chooseFile);
            footer.Controls.Add(openInvite);
            footer.Controls.Add(startReceive);
            footer.Controls.Add(cancel);
            layout.Controls.Add(footer, 0, 4);
            UpdateButtons();
        }

        private int Px(int value) => (int)Math.Round(value * scale);

        private void AddTool(string name, string summary, Action open)
        {
            var tile = Button(name, open, tools.ClientSize.Width - tools.Padding.Horizontal, Px(69));
            tile.AccessibleDescription = summary;
            tile.Margin = new Padding(0, 0, 0, Px(8));
            tile.DrawContent = (g, r) =>
            {
                var icon = new Rectangle(Px(11), (r.Height - Px(38)) / 2, Px(38), Px(38));
                using (var color = new SolidBrush(theme.Body))
                using (var edge = Rounded(icon, Px(9))) g.FillPath(color, edge);
                DrawTransferIcon(g, icon);
                var text = new Rectangle(Px(60), Px(11), Math.Max(1, r.Width - Px(100)), Px(24));
                TextRenderer.DrawText(g, name, headingFont, text, theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                text.Y += Px(25);
                TextRenderer.DrawText(g, summary, bodyFont, text, theme.Icon, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                DrawArrow(g, new Rectangle(r.Width - Px(36), 0, Px(28), r.Height), theme.Icon, 1);
            };
            tools.Controls.Add(tile);
        }

        private TransferButton Button(string label, Action action, int width, int height, bool primary = false)
        {
            var button = new TransferButton(theme, accent, Px(7))
            {
                Text = label, Size = new Size(Math.Max(1, width), height), Primary = primary, Cursor = Cursors.Hand
            };
            button.Click += (s, e) => action();
            return button;
        }

        private void ShowTools()
        {
            if (operation != null) return;
            transfer.Visible = false;
            tools.Visible = true;
            tools.Controls[0].Focus();
        }

        private void OpenFileTransfer()
        {
            tools.Visible = false;
            transfer.Visible = true;
            SetReceiveMode(false);
        }

        private void SetReceiveMode(bool receive)
        {
            if (operation != null) return;
            receiving = receive;
            sendTab.Primary = !receive;
            receiveTab.Primary = receive;
            sendTab.Invalidate();
            receiveTab.Invalidate();
            receiveView.Visible = receive;
            sendView.Visible = !receive;
            status.Text = "";
            progress.Visible = false;
            UpdateButtons();
            if (!receive) RefreshDevices();
        }

        private async void RefreshDevices()
        {
            await RunAsync("正在查找接收设备…", async token =>
            {
                var nearby = FileTransferLan.DiscoverAsync(token);
                var account = FileTransferAccount.IsAvailable ? FileTransferAccount.DiscoverAsync(token) : null;
                var found = new List<TransferDevice>();
                try { found.AddRange((await nearby).Select(d => new TransferDevice(d))); }
                catch (Exception) when (!token.IsCancellationRequested) { }
                string message = "";
                if (account != null)
                {
                    try { found.AddRange((await account).Select(d => new TransferDevice(d))); }
                    catch (Exception ex) when (!token.IsCancellationRequested) { message = ex.Message; }
                }
                devices.Items.Clear();
                foreach (var device in found) devices.Items.Add(device);
                if (devices.Items.Count == 1) devices.SelectedIndex = 0;
                emptyDevices.Visible = devices.Items.Count == 0;
                ResizeDeviceList();
                status.Text = devices.Items.Count == 0 ? message : "";
            });
        }

        private void ResizeDeviceList()
        {
            int height = devices.Items.Count == 0 ? Px(62) : devices.ItemHeight * Math.Min(devices.Items.Count, 4) + 2;
            deviceBody.Height = Math.Max(1, Math.Min(height, sendView.ClientSize.Height - Px(33)));
        }

        private async void ReceiveFiles()
        {
            await RunAsync("等待对方发送文件…", async token =>
            {
                using (var localStop = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var accountStop = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var local = AcceptLocalAsync(localStop.Token);
                    var account = FileTransferAccount.IsAvailable ? FileTransferAccount.AcceptAsync(null, accountStop.Token) : null;
                    Task<IFileTransferConnection> winner = null;
                    try
                    {
                        if (account == null)
                        {
                            winner = local;
                            using (var connection = await local) await ReceiveAsync(connection, token);
                            return;
                        }
                        winner = await Task.WhenAny(local, account);
                        IFileTransferConnection accepted;
                        try { accepted = await winner; }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            status.Text = winner == account ? "等待局域网设备发送…" : "等待同账号设备发送…";
                            System.Diagnostics.Debug.WriteLine(ex);
                            winner = winner == account ? local : account;
                            accepted = await winner;
                        }
                        if (winner == account) localStop.Cancel(); else accountStop.Cancel();
                        using (accepted) await ReceiveAsync(accepted, token);
                    }
                    finally
                    {
                        if (winner != local)
                        {
                            localStop.Cancel();
                            await DisposeUnselected(local);
                        }
                        if (account != null && winner != account)
                        {
                            accountStop.Cancel();
                            await DisposeUnselected(account);
                        }
                    }
                }
            });
        }

        private static async Task<IFileTransferConnection> AcceptLocalAsync(CancellationToken token)
        {
            using (var receiver = await Task.Run(() => LanTransferReceiver.Start(), token))
                return await receiver.AcceptAsync(token);
        }

        private static async Task DisposeUnselected(Task<IFileTransferConnection> pending)
        {
            try { (await pending).Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        private async void SendSelected()
        {
            if (!(devices.SelectedItem is TransferDevice device)) return;
            string path = ChooseFile();
            if (path == null) return;
            await RunAsync("正在连接 " + device.Name + "…", async token =>
            {
                using (var connection = await device.ConnectAsync(token)) await SendAsync(connection, path, token);
            });
        }

        private async void SendInvitation()
        {
            if (invitation != null) { CopyInvitation(); return; }
            string path = ChooseFile();
            if (path == null) return;
            await RunAsync("等待对方打开邀请…", async token =>
            {
                string code = FileTransferP2p.CreateCode();
                invitation = FileTransferInvite.Create(code);
                UpdateButtons();
                CopyInvitation();
                using (var wire = await FileTransferP2p.ConnectInvitationAsync(code, token))
                {
                    invitation = null;
                    UpdateButtons();
                    using (var direct = await EncryptedFileTransfer.OpenAsync(wire, code, true, token))
                        await SendAsync(direct, path, token);
                }
            });
        }

        private void CopyInvitation()
        {
            try { Clipboard.SetText(invitation); status.Text = "邀请已复制，2 分钟内有效。"; }
            catch (ExternalException) { status.Text = "剪贴板正忙，请再点一次「复制邀请」。"; }
        }

        private async void ReceiveInvitation()
        {
            string text;
            try { text = Clipboard.GetText(); }
            catch (ExternalException) { status.Text = "剪贴板正忙，请稍后重试。"; return; }
            if (!FileTransferInvite.TryRead(text, out string code))
            {
                status.Text = "请先复制对方发来的邀请链接。";
                return;
            }
            await RunAsync("正在连接发送方…", async token =>
            {
                using (var wire = await FileTransferP2p.AcceptAsync(code, token))
                using (var direct = await EncryptedFileTransfer.OpenAsync(wire, code, false, token))
                    await ReceiveAsync(direct, token);
            });
        }

        private async Task SendAsync(IFileTransferConnection connection, string path, CancellationToken token)
        {
            fileLength = new FileInfo(path).Length;
            status.Text = "等待对方确认接收…";
            var feedback = new Progress<long>(count => UpdateProgress(count, "发送中"));
            bool sent = await Task.Run(() => FileTransferProtocol.SendAsync(connection, path, feedback, token), token);
            if (sent) UpdateProgress(fileLength, "发送完成");
            status.Text = sent ? "发送完成。" : "对方未接收文件。";
        }

        private async Task ReceiveAsync(IFileTransferConnection connection, CancellationToken token)
        {
            status.Text = "已连接，等待文件信息…";
            var feedback = new Progress<long>(count => UpdateProgress(count, "接收中"));
            bool saved = await FileTransferProtocol.ReceiveAsync(connection, SelectDestination, feedback, token);
            if (saved) UpdateProgress(fileLength, "接收完成");
            status.Text = saved ? "文件已保存。" : "已拒绝接收。";
        }

        private string SelectDestination(IncomingTransfer offer)
        {
            fileLength = offer.Length;
            string sender = new string(Array.FindAll(offer.Sender.ToCharArray(), c => !char.IsControl(c)));
            if (sender.Length == 0) sender = "对方设备";
            using (var dialog = new SaveFileDialog
            {
                Title = $"接收 {sender} 的文件（{FormatBytes(fileLength)}）", FileName = offer.Name,
                AddExtension = false, OverwritePrompt = false, CheckPathExists = true
            })
            {
                dialog.FileOk += (s, e) =>
                {
                    if (!File.Exists(dialog.FileName)) return;
                    e.Cancel = true;
                    MessageBox.Show(this, "这个文件已存在，请选择新的保存名称。", "文件互传", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private string ChooseFile()
        {
            using (var dialog = new OpenFileDialog { Title = "选择要发送的文件", CheckFileExists = true })
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }

        private void UpdateProgress(long count, string label)
        {
            if (IsDisposed || Disposing || operation == null || operation.IsCancellationRequested) return;
            percent = fileLength == 0 ? 100 : (int)Math.Min(100, count * 100d / fileLength);
            progress.Visible = true;
            progress.Invalidate();
            status.Text = $"{label} · {FormatBytes(count)} / {FormatBytes(fileLength)}";
        }

        private static string FormatBytes(long bytes) => bytes >= 1024L * 1024
            ? $"{bytes / (1024d * 1024):0.##} MiB" : $"{bytes / 1024d:0.##} KiB";

        private async Task RunAsync(string message, Func<CancellationToken, Task> task)
        {
            if (operation != null) return;
            using (var current = new CancellationTokenSource())
            {
                operation = current;
                progress.Visible = false;
                status.Text = message;
                UpdateButtons();
                try { await task(current.Token); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    if (!IsDisposed && !Disposing) status.Text = current.IsCancellationRequested ? "已取消。" : ex.Message;
                }
                finally
                {
                    invitation = null;
                    operation = null;
                    if (!IsDisposed && !Disposing) UpdateButtons();
                }
            }
        }

        private void UpdateButtons()
        {
            bool busy = operation != null;
            back.Enabled = sendTab.Enabled = receiveTab.Enabled = !busy;
            refresh.Enabled = devices.Enabled = startReceive.Enabled = openInvite.Enabled = !busy;
            cancel.Visible = busy;
            invite.Visible = !receiving && (!busy || invitation != null);
            invite.Text = invitation == null ? "邀请其他账号" : "复制邀请";
            chooseFile.Visible = !receiving && !busy;
            chooseFile.Enabled = devices.SelectedItem != null;
            startReceive.Visible = openInvite.Visible = receiving && !busy;
        }

        private void DrawDevice(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || !(devices.Items[e.Index] is TransferDevice device)) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? theme.Body : Color.White)) e.Graphics.FillRectangle(brush, e.Bounds);
            using (var pen = new Pen(theme.Divider)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(theme.Icon, Math.Max(1, Px(1))))
            {
                int x = e.Bounds.X + Px(13), y = e.Bounds.Y + Px(16);
                e.Graphics.DrawRectangle(pen, x, y, Px(17), Px(11));
                e.Graphics.DrawLine(pen, x + Px(8), y + Px(12), x + Px(8), y + Px(15));
                e.Graphics.DrawLine(pen, x + Px(4), y + Px(15), x + Px(12), y + Px(15));
            }
            var label = new Rectangle(e.Bounds.X + Px(44), e.Bounds.Y + Px(4), Math.Max(1, e.Bounds.Width - Px(83)), Px(23));
            TextRenderer.DrawText(e.Graphics, device.Name, bodyFont, label, theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            label.Y += Px(20);
            TextRenderer.DrawText(e.Graphics, device.Network, bodyFont, label, theme.Icon, TextFormatFlags.VerticalCenter);
            var choice = new Rectangle(e.Bounds.Right - Px(25), e.Bounds.Top + (e.Bounds.Height - Px(13)) / 2, Px(13), Px(13));
            using (var pen = new Pen(selected ? accent : theme.Divider, Math.Max(1, Px(1)))) e.Graphics.DrawEllipse(pen, choice);
            if (selected)
            {
                choice.Inflate(-Px(3), -Px(3));
                using (var fill = new SolidBrush(accent)) e.Graphics.FillEllipse(fill, choice);
            }
            e.DrawFocusRectangle();
        }

        private void DrawTransferIcon(Graphics g, Rectangle icon)
        {
            var upper = new Rectangle(icon.X, icon.Y, icon.Width, icon.Height / 2);
            upper.Offset(0, Px(4));
            DrawArrow(g, upper, accent, 1);
            upper.Offset(0, Px(12));
            DrawArrow(g, upper, accent, -1);
        }

        private void DrawArrow(Graphics g, Rectangle r, Color color, int direction)
        {
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            int p = Px(6), head = Px(4);
            using (var pen = new Pen(color, Math.Max(1.3f, scale * 1.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, cx - p, cy, cx + p, cy);
                g.DrawLines(pen, new[] { new Point(cx + direction * (p - head), cy - head), new Point(cx + direction * p, cy), new Point(cx + direction * (p - head), cy + head) });
            }
        }

        private static Color Mix(Color color, Color shade, double amount) => Color.FromArgb(
            (int)(color.R * (1 - amount) + shade.R * amount), (int)(color.G * (1 - amount) + shade.G * amount), (int)(color.B * (1 - amount) + shade.B * amount));

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            var shape = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (d < 2) { shape.AddRectangle(rect); return shape; }
            shape.AddArc(rect.X, rect.Y, d, d, 180, 90);
            shape.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            shape.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            shape.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            shape.CloseFigure();
            return shape;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) operation?.Cancel();
            base.Dispose(disposing);
            if (disposing) { appIcon?.Dispose(); bodyFont?.Dispose(); headingFont?.Dispose(); }
        }

        private sealed class TransferButton : Button
        {
            private readonly NoteTheme theme;
            private readonly Color accent;
            private readonly int radius;
            private bool hot;
            internal bool Primary;
            internal bool LinkStyle;
            internal Action<Graphics, Rectangle> DrawContent;

            internal TransferButton(NoteTheme theme, Color accent, int radius)
            {
                this.theme = theme;
                this.accent = accent;
                this.radius = radius;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
            }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Parent?.BackColor ?? Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(1, 1, Math.Max(1, ClientSize.Width - 3), Math.Max(1, ClientSize.Height - 3));
                var surface = Parent?.BackColor ?? Color.White;
                var fill = Primary && Enabled ? (hot ? Mix(accent, Color.Black, .08) : accent) : hot && Enabled ? theme.Body : surface;
                using (var shape = Rounded(rect, radius))
                using (var brush = new SolidBrush(fill))
                using (var outline = new Pen(Primary && Enabled ? fill : theme.Divider))
                {
                    g.FillPath(brush, shape);
                    if (!LinkStyle) g.DrawPath(outline, shape);
                }
                if (DrawContent == null) TextRenderer.DrawText(g, Text, Font, rect,
                    !Enabled ? Color.Gray : Primary ? Color.White : LinkStyle ? accent : theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                else DrawContent(g, rect);
                if (Focused && ShowFocusCues) { rect.Inflate(-4, -4); ControlPaint.DrawFocusRectangle(g, rect); }
            }
        }

        private sealed class TransferDevice
        {
            private readonly LanTransferDevice local;
            private readonly AccountTransferDevice account;
            internal string Name => local?.Name ?? account.Name;
            internal string Network => local != null ? "局域网" : "同账号";

            internal TransferDevice(LanTransferDevice device) { local = device; }
            internal TransferDevice(AccountTransferDevice device) { account = device; }
            internal Task<IFileTransferConnection> ConnectAsync(CancellationToken token) => local != null
                ? FileTransferLan.ConnectAsync(local, token) : FileTransferAccount.ConnectAsync(account, token);
            public override string ToString() => Name + " · " + Network;
        }
    }
}
