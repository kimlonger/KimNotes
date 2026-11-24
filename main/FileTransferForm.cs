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
        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);

        private const string ReceiveCodeHint = "复制收到的取件码后，点「粘贴取件码」。";
        private const int PickupIdleSeconds = 120;
        private readonly float scale = UiDpi.Factor;
        private readonly NoteTheme theme = NoteTheme.Current();
        private readonly Font bodyFont, headingFont, pickupCodeFont;
        private readonly Icon appIcon;
        private readonly FlowLayoutPanel tools;
        private readonly Panel transfer, sendView, receiveView, progress, deviceBody, pickupCodeCard;
        private readonly TextBox pickupCodeText;
        private readonly Label pickupFileName;
        private readonly Label status, emptyDevices, receiverHint;
        private readonly Panel devices;
        private ToolStripDropDown transferHelp;
        private readonly Dictionary<string, CheckBox> deviceSelectors = new Dictionary<string, CheckBox>();
        private TransferDevice[] discoveredDevices = Array.Empty<TransferDevice>();
        private string selectedDeviceKey;
        private TransferDevice SelectedDevice => selectedDeviceKey == null
            ? null : discoveredDevices.FirstOrDefault(device => device.Key == selectedDeviceKey);
        private const int DeviceRowHeight = 36;
        private readonly TransferButton back, sendTab, receiveTab, refresh, chooseFile, createPickupCode, startReceive, pastePickupCode, cancel, copyDiagnostics, disconnectPickup;
        private readonly Color accent;
        private readonly CancellationTokenSource windowStop;
        private readonly System.Windows.Forms.Timer discoveryTimer = new System.Windows.Forms.Timer { Interval = 7000 };
        private readonly System.Windows.Forms.Timer attentionTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly FileTransferReception reception;
        private CancellationTokenSource operation;
        private PickupLink pickupLink;
        private string lastUsedPickupCode, directDiagnostic;
        private int percent;
        private bool receiving, refreshing, choosingFile, suspendingReception, closing;
        private long fileLength;
        private bool CanUseTransfer => !closing && reception.IsSignedIn && !windowStop.IsCancellationRequested;

        internal FileTransferForm(FileTransferReception reception)
        {
            this.reception = reception;
            windowStop = CancellationTokenSource.CreateLinkedTokenSource(reception.TransferLifetime);
            Text = "桌面工具";
            AccessibleName = Text;
            appIcon = Icon.ExtractAssociatedIcon(typeof(FileTransferForm).Assembly.Location);
            if (appIcon != null) Icon = appIcon;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            var active = ActiveForm;
            var area = (active == null ? Screen.FromPoint(Cursor.Position) : Screen.FromControl(active)).WorkingArea;
            ClientSize = new Size(Math.Min(Px(390), area.Width), Math.Min(Px(500), area.Height));
            MinimumSize = new Size(Math.Min(Px(350), area.Width), Math.Min(Px(375), area.Height));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            Deactivate += (s, e) =>
            {
                attentionTimer.Stop();
                TopMost = false;
            };
            attentionTimer.Tick += (s, e) =>
            {
                attentionTimer.Stop();
                if (ActiveForm != this) TopMost = false;
            };
            Disposed += (s, e) => attentionTimer.Dispose();
            BackColor = theme.Chrome;
            bodyFont = new Font("Microsoft YaHei UI", 9.5f);
            headingFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            pickupCodeFont = new Font("Consolas", 10f);
            Font = bodyFont;
            accent = Mix(theme.IconHover, Color.Black, .15);

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            Controls.Add(content);
            var chrome = FormChrome.ApplyStandardHeader(this, Text, theme);
            var caption = (Label)chrome.Tag;
            caption.AutoSize = false;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.Height = chrome.Height;
            caption.Top = 0;
            tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true, Padding = new Padding(Px(16)), BackColor = Color.White
            };
            content.Controls.Add(tools);
            AddTool("文件互传", OpenFileTransfer);

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
            var help = new TransferButton(theme, accent, Px(7))
            {
                Text = "说明", LinkStyle = true, Cursor = Cursors.Hand, Width = Px(55), Dock = DockStyle.Right,
                AccessibleDescription = "查看文件互传和取件码说明"
            };
            help.Click += (s, e) => ShowTransferHelp(help);
            titleRow.Controls.Add(title);
            titleRow.Controls.Add(backSlot);
            titleRow.Controls.Add(help);
            layout.Controls.Add(titleRow, 0, 0);

            sendTab = Button("发送", () => SetReceiveMode(false), Px(75), Px(30), true);
            receiveTab = Button("接收", () => SetReceiveMode(true), Px(75), Px(30));
            sendTab.ShowSelectedWhenDisabled = receiveTab.ShowSelectedWhenDisabled = true;
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
            devices = new BufferedPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White,
                AccessibleName = "接收设备"
            };
            deviceBody = new DeviceFrame(theme.Divider, Px(8)) { Dock = DockStyle.Fill };
            emptyDevices = new Label
            {
                Dock = DockStyle.Fill, Text = "等待其他设备开启接收…", TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = theme.Icon, BackColor = Color.White, Padding = new Padding(Px(8))
            };
            deviceBody.Controls.Add(devices);
            deviceBody.Controls.Add(emptyDevices);
            sendView.Controls.Add(deviceBody);
            sendView.Controls.Add(deviceHeading);
            pickupCodeCard = new BufferedPanel
            {
                Dock = DockStyle.Bottom, Height = Px(65), Padding = new Padding(Px(12), Px(15), Px(12), Px(6)),
                BackColor = Color.White, Visible = false
            };
            pickupCodeCard.Paint += (s, e) =>
            {
                var bounds = Rectangle.Inflate(pickupCodeCard.ClientRectangle, -1, -1);
                bounds.Y += Px(7);
                bounds.Height -= Px(7);
                PaintRoundedCard(e.Graphics, bounds, Px(9));
            };
            pickupFileName = new Label
            {
                Dock = DockStyle.Top, Height = Px(23), AutoEllipsis = true, UseMnemonic = false,
                BackColor = theme.Body, ForeColor = theme.Text, TextAlign = ContentAlignment.MiddleLeft
            };
            pickupCodeText = new TextBox
            {
                Dock = DockStyle.Top, ReadOnly = true, BorderStyle = BorderStyle.None, Font = pickupCodeFont,
                ForeColor = theme.Text, BackColor = theme.Body, AccessibleName = "取件码"
            };
            pickupCodeCard.Controls.Add(pickupCodeText);
            pickupCodeCard.Controls.Add(pickupFileName);
            sendView.Controls.Add(pickupCodeCard);

            var receiverHeading = new Label
            {
                Text = "本机设备", Dock = DockStyle.Top, Height = Px(33), TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Text
            };
            var receiverCard = new BufferedPanel { Dock = DockStyle.Top, Height = Px(63), BackColor = Color.White, Padding = new Padding(Px(12), Px(8), Px(12), Px(6)) };
            receiverCard.Paint += (s, e) => PaintRoundedCard(e.Graphics, Rectangle.Inflate(receiverCard.ClientRectangle, -1, -1), Px(9));
            var receiverName = new Label
            {
                Text = Environment.MachineName, Dock = DockStyle.Top, Height = Px(24), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Text, BackColor = theme.Body
            };
            receiverHint = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Icon, BackColor = theme.Body
            };
            receiverCard.Controls.Add(receiverHint);
            receiverCard.Controls.Add(receiverName);
            receiveView.Controls.Add(receiverCard);
            receiveView.Controls.Add(receiverHeading);
            startReceive = Button("停止接收", ToggleReception, Px(124), Px(35), true);
            startReceive.Dock = DockStyle.Right;
            pastePickupCode = Button("粘贴取件码", ReceivePickupCode, Px(104), Px(35));
            pastePickupCode.Dock = DockStyle.Left;
            pastePickupCode.LinkStyle = true;
            pastePickupCode.AccessibleDescription = "先手动复制对方发来的取件码，再点击粘贴并连接。";

            var statusRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = theme.Icon, AutoEllipsis = true };
            statusRow.Controls.Add(status);
            copyDiagnostics = Button("复制诊断", CopyDirectDiagnostic, Px(87), Px(26));
            copyDiagnostics.Dock = DockStyle.Right;
            copyDiagnostics.LinkStyle = true;
            copyDiagnostics.Visible = false;
            statusRow.Controls.Add(copyDiagnostics);
            disconnectPickup = Button("断开连接", DisconnectPickup, Px(87), Px(26));
            disconnectPickup.Dock = DockStyle.Right;
            disconnectPickup.LinkStyle = true;
            disconnectPickup.Visible = false;
            statusRow.Controls.Add(disconnectPickup);
            progress = new Panel { Dock = DockStyle.Bottom, Height = Px(2), BackColor = theme.Body, Visible = false };
            progress.Paint += (s, e) =>
            {
                using (var fill = new SolidBrush(accent)) e.Graphics.FillRectangle(fill, 0, 0, progress.Width * percent / 100, progress.Height);
            };
            statusRow.Controls.Add(progress);
            layout.Controls.Add(statusRow, 0, 3);

            var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            createPickupCode = Button("生成取件码", SendPickupCode, Px(126), Px(35));
            createPickupCode.LinkStyle = true;
            createPickupCode.Dock = DockStyle.Left;
            chooseFile = Button("连接并发送", SendSelected, Px(126), Px(35), true);
            chooseFile.Dock = DockStyle.Right;
            cancel = Button("取消", () => operation?.Cancel(), Px(75), Px(35));
            cancel.Dock = DockStyle.Right;
            footer.Controls.Add(createPickupCode);
            footer.Controls.Add(chooseFile);
            footer.Controls.Add(pastePickupCode);
            footer.Controls.Add(startReceive);
            footer.Controls.Add(cancel);
            layout.Controls.Add(footer, 0, 4);
            discoveryTimer.Tick += (s, e) => RefreshDevices();
            transfer.VisibleChanged += (s, e) => UpdateToolActive();
            VisibleChanged += (s, e) => UpdateToolActive();
            Resize += (s, e) => UpdateToolActive();
            reception.Changed += UpdateReception;
            UpdateButtons();
            UpdateReceptionHint();
        }

        private int Px(int value) => (int)Math.Round(value * scale);

        private void ShowTransferHelp(Control button)
        {
            if (transferHelp?.Visible == true)
            {
                transferHelp.Close();
                return;
            }
            transferHelp?.Dispose();

            var lines = new[] { "勾选后可发送", "先选文件再取码", FileTransferP2p.PickupCodeWindowMinutes + " 分钟，限用一次" };
            // Size the host at construction, before WinForms caches its initial layout.
            Size contentSize;
            using (var measureSurface = new Bitmap(1, 1))
            {
                measureSurface.SetResolution(button.DeviceDpi, button.DeviceDpi);
                using (var graphics = Graphics.FromImage(measureSurface))
                {
                    int width = (int)Math.Ceiling(lines.Max(line => graphics.MeasureString(line, bodyFont).Width));
                    int height = ((int)Math.Ceiling(bodyFont.GetHeight(graphics) + 2)) * lines.Length;
                    contentSize = new Size(Math.Max(Px(167), width + Px(3)), Math.Max(Px(53), height + Px(2)));
                }
            }

            var padding = new Padding(Px(8));
            transferHelp = new RoundedHelpPopup(theme.Divider, Px(12)) { AutoSize = false, Padding = padding,
                Size = new Size(contentSize.Width + padding.Horizontal, contentSize.Height + padding.Vertical) };
            var text = new Label
            {
                Text = string.Join("\r\n", lines), UseMnemonic = false,
                Font = bodyFont, ForeColor = theme.Icon, BackColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft, Size = contentSize
            };
            transferHelp.Items.Add(new ToolStripControlHost(text)
            {
                AutoSize = false, Size = contentSize, Padding = Padding.Empty, Margin = Padding.Empty
            });

            var anchor = new Point(button.Width, button.Height);
            var receiveCorner = receiveTab.PointToScreen(new Point(receiveTab.Width, receiveTab.Height));
            if (button.PointToScreen(anchor).X - transferHelp.Width < receiveCorner.X + Px(8))
                anchor.Y = Math.Max(anchor.Y, button.PointToClient(receiveCorner).Y + Px(6));
            transferHelp.Show(button, anchor, ToolStripDropDownDirection.BelowLeft);
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x0232 && !closing && IsHandleCreated && Visible) // WM_EXITSIZEMOVE
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            discoveryTimer.Start();
        }

        internal void ShowOrActivate()
        {
            if (!CanUseTransfer) return;
            // Keep the first frame above pinned notes, even when Windows disallows background activation.
            attentionTimer.Stop();
            TopMost = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            BringToFront();
            Activate();
            if (ActiveForm != this) attentionTimer.Start();
        }

        private void AddTool(string name, Action open)
        {
            var tile = Button(name, open, Px(86), Px(86));
            tile.LinkStyle = true;
            tile.Margin = new Padding(0, 0, Px(8), Px(8));
            tile.DrawContent = (g, r) =>
            {
                int iconSize = Px(47);
                var icon = new Rectangle(r.X + (r.Width - iconSize) / 2, r.Y + Px(7), iconSize, iconSize);
                using (var color = new SolidBrush(theme.Body))
                using (var edge = Rounded(icon, Px(12))) g.FillPath(color, edge);
                DrawTransferIcon(g, icon);
                var text = new Rectangle(r.X, icon.Bottom + Px(6), r.Width, Px(20));
                TextRenderer.DrawText(g, name, bodyFont, text, theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
            if (!CanUseTransfer) return;
            tools.Visible = false;
            transfer.Visible = true;
            SetReceiveMode(false);
        }

        private void SetReceiveMode(bool receive)
        {
            if (operation != null || !CanUseTransfer) return;
            receiving = receive;
            sendTab.Primary = !receive;
            receiveTab.Primary = receive;
            sendTab.Invalidate();
            receiveTab.Invalidate();
            receiveView.Visible = receive;
            sendView.Visible = !receive;
            directDiagnostic = null;
            status.Text = pickupLink?.Connection != null && pickupLink.Sender != receive
                ? receive ? "取件已连接，等待对方发送。" : "取件已连接，可以选择文件。"
                : !receive ? "" : reception.LastDirectDiagnostic == null
                    ? ReceiveCodeHint : FileTransferDirectTimeoutException.UserMessage;
            progress.Visible = false;
            UpdateButtons();
            if (!receive) RefreshDevices();
        }

        private async void RefreshDevices()
        {
            if (refreshing || !CanUseTransfer || !transfer.Visible || receiving || IsMovingWindow
                || WindowState == FormWindowState.Minimized) return;
            refreshing = true;
            UpdateButtons();
            var token = windowStop.Token;
            try
            {
                var nearby = DiscoverAsync(FileTransferLan.DiscoverAsync(token, true), d => new TransferDevice(d), token);
                var account = FileTransferAccount.IsAvailable
                    ? DiscoverAsync(FileTransferAccount.DiscoverAsync(token), d => new TransferDevice(d), token)
                    : Task.FromResult(Array.Empty<TransferDevice>());
                var both = await Task.WhenAll(nearby, account);
                token.ThrowIfCancellationRequested();
                if (!IsMovingWindow) UpdateDeviceList(both.SelectMany(group => group).ToArray());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                refreshing = false;
                if (!closing) UpdateButtons();
            }
        }

        private static async Task<TransferDevice[]> DiscoverAsync<T>(Task<IReadOnlyList<T>> pending, Func<T, TransferDevice> wrap, CancellationToken token)
        {
            try { return (await pending).Select(wrap).ToArray(); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                return Array.Empty<TransferDevice>();
            }
        }

        private void UpdateDeviceList(TransferDevice[] found)
        {
            var keys = new HashSet<string>();
            found = found.Where(device => keys.Add(device.Key)).ToArray();
            var previous = discoveredDevices;
            discoveredDevices = found;
            if (previous.Select(d => (d.Key, d.Name)).SequenceEqual(found.Select(d => (d.Key, d.Name)))) return;
            if (!keys.Contains(selectedDeviceKey)) selectedDeviceKey = null;
            string focusedKey = deviceSelectors.FirstOrDefault(pair => pair.Value.Focused).Key;
            int scrollOffset = -devices.AutoScrollPosition.Y;
            devices.SuspendLayout();
            try
            {
                foreach (Control row in devices.Controls.Cast<Control>().ToArray()) row.Dispose();
                deviceSelectors.Clear();
                for (int index = found.Length - 1; index >= 0; index--)
                    devices.Controls.Add(CreateDeviceRow(found[index], index));
                devices.AutoScrollMinSize = new Size(0, Px(DeviceRowHeight) * found.Length);
                emptyDevices.Visible = found.Length == 0;
            }
            finally { devices.ResumeLayout(true); }
            devices.AutoScrollPosition = new Point(0, scrollOffset);
            if (focusedKey != null && deviceSelectors.TryGetValue(focusedKey, out var focus)) focus.Focus();
            UpdateButtons();
        }

        private Control CreateDeviceRow(TransferDevice device, int index)
        {
            var row = new BufferedPanel { Dock = DockStyle.Top, Height = Px(DeviceRowHeight), TabIndex = index };
            int size = Px(26);
            var selector = new CheckBox
            {
                Size = new Size(size, size),
                Location = new Point(row.Width - Px(10) - size, (row.Height - size) / 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                CheckAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                AccessibleName = "选择 " + device.Name,
                AccessibleDescription = device.Network,
                Checked = device.Key == selectedDeviceKey
            };
            deviceSelectors.Add(device.Key, selector);
            selector.CheckedChanged += (s, e) =>
            {
                if (!selector.Checked && selectedDeviceKey != device.Key) return;
                string previousKey = selectedDeviceKey;
                selectedDeviceKey = selector.Checked ? device.Key : null;
                if (selector.Checked && previousKey != null && previousKey != device.Key)
                    deviceSelectors[previousKey].Checked = false;
                UpdateButtons();
            };
            row.Controls.Add(selector);
            row.Paint += (s, e) => DrawDevice(e.Graphics, row.ClientRectangle, device, selector.Left);
            return row;
        }

        private void ToggleReception()
        {
            if (CanUseTransfer) reception.SetEnabled(!reception.Enabled);
        }

        private void UpdateToolActive() =>
            reception.SetToolActive(CanUseTransfer && Visible && WindowState != FormWindowState.Minimized && transfer.Visible);

        private void UpdateReception()
        {
            if (closing) return;
            if (!CanUseTransfer)
            {
                Close();
                return;
            }
            if (receiving && operation == null && directDiagnostic == null)
            {
                if (reception.LastDirectDiagnostic != null && status.Text == ReceiveCodeHint)
                    status.Text = FileTransferDirectTimeoutException.UserMessage;
                else if (reception.LastDirectDiagnostic == null && status.Text == FileTransferDirectTimeoutException.UserMessage)
                    status.Text = ReceiveCodeHint;
            }
            UpdateButtons();
            UpdateReceptionHint();
        }

        internal Task ReceiveIncomingAsync(IFileTransferConnection connection, IncomingTransfer offer, CancellationToken token)
        {
            if (!CanUseTransfer || choosingFile || operation != null || token.IsCancellationRequested) return Task.CompletedTask;
            tools.Hide();
            transfer.Show();
            SetReceiveMode(true);
            return RunAsync("请确认接收文件。", cancel =>
            {
                ShowOrActivate();
                return ReceiveAsync(connection, cancel, offer);
            }, false, token);
        }

        private void UpdateReceptionHint()
        {
            if (closing) return;
            string mode = reception.LanReady && reception.AccountReady ? "局域网传输、无线传输"
                : reception.LanReady ? "局域网传输" : reception.AccountReady ? "无线传输" : null;
            string hint = !reception.Enabled ? "接收已暂停"
                : operation != null ? "文件互传进行中"
                : !reception.CanWaitForFiles ? "打开文件互传后开始接收"
                : mode == null ? "正在准备接收…" : "接收已开启 · " + mode;
            receiverHint.Text = hint;
        }

        private async void SendSelected()
        {
            if (!CanUseTransfer) return;
            var device = SelectedDevice;
            if (device == null) return;
            if (pickupLink != null && FileTransferP2p.HasFixedLocalPort && device.Network == "无线传输")
            {
                status.Text = "固定 UDP 端口正在用于取件连接，请先断开。";
                return;
            }
            string path = ChooseFile();
            if (path == null) return;
            await RunAsync("正在连接 " + device.Name + "…", async token =>
            {
                using (var connection = await device.ConnectAsync(token)) await SendAsync(connection, path, token);
            });
        }

        private async void SendPickupCode()
        {
            if (!CanUseTransfer) return;
            if (pickupLink is PickupLink existing)
            {
                if (!existing.Sender) return;
                if (existing.Connection == null) CopyPickupCode(existing.Code);
                else await SendPickupFileAsync(existing);
                return;
            }
            string path = ChooseFile();
            if (path == null || !CanUseTransfer || operation != null || pickupLink != null) return;
            string code = FileTransferP2p.CreateCode();
            var pending = ConnectPickupAsync(code, true, path);
            if (pickupLink?.Code == code && pickupLink.Connection == null) CopyPickupCode(code);
            await pending;
        }

        private void CopyPickupCode(string code)
        {
            try { Clipboard.SetText(code); status.Text = "取件码已复制，请发给对方。"; }
            catch (ExternalException) { status.Text = "剪贴板正忙，请再点一次「复制取件码」。"; }
        }

        private async void ReceivePickupCode()
        {
            if (!CanUseTransfer) return;
            string text;
            try { text = Clipboard.GetText(); }
            catch (ExternalException) { status.Text = "剪贴板正忙，请再点一次「粘贴取件码」。"; return; }
            if (!FileTransferInvite.TryRead(text, out string code))
            {
                status.Text = ReceiveCodeHint;
                return;
            }
            if (pickupLink != null)
            {
                status.Text = pickupLink.Code == code ? "取件码已使用，当前连接仍可继续接收。" : "请先断开当前取件连接。";
                return;
            }
            if (lastUsedPickupCode == code)
            {
                status.Text = "此取件码已使用，请获取新的取件码。";
                return;
            }
            await ConnectPickupAsync(code, false);
        }

        private async Task ConnectPickupAsync(string code, bool sender, string initialPath = null)
        {
            if (operation != null || pickupLink != null || !CanUseTransfer || (sender && string.IsNullOrEmpty(initialPath))) return;
            var link = new PickupLink(code, sender, windowStop.Token, Path.GetFileName(initialPath));
            pickupLink = link;
            UpdateReceptionSuspension();
            bool connected = await RunAsync(sender ? "等待对方粘贴取件码…" : "正在连接发送方…", async token =>
            {
                using (token.Register(link.Cancel))
                {
                    var wire = await (sender
                        ? FileTransferP2p.ConnectInvitationAsync(code, link.Token)
                        : FileTransferP2p.AcceptAsync(code, link.Token));
                    try
                    {
                        var channel = await EncryptedFileTransfer.OpenPickupAsync(wire, code, sender, link.Token);
                        token.ThrowIfCancellationRequested();
                        link.Attach(channel.Connection, channel.CanReuse, FileTransferP2p.WaitForClosure(wire));
                    }
                    catch { wire.Dispose(); throw; }
                    if (sender)
                    {
                        suspendingReception = true;
                        UpdateReceptionSuspension();
                        UpdateButtons();
                        await SendAsync(link.Connection, initialPath, token);
                    }
                }
            }, false);
            if (!connected || closing)
            {
                ClosePickupLink(link);
                return;
            }

            if (sender)
            {
                if (!link.CanReuse || link.Closed.IsCompleted)
                {
                    ClosePickupLink(link);
                    return;
                }
                link.BeginIdle();
                _ = WatchPickupSenderAsync(link);
            }
            else
            {
                lastUsedPickupCode = code;
                status.Text = "取件已连接，等待对方发送。";
                _ = ReceivePickupFilesAsync(link);
            }
            UpdateButtons();
        }

        private async Task SendPickupFileAsync(PickupLink link)
        {
            link.SendingFile = true;
            link.PauseIdle();
            try
            {
                string path = ChooseFile();
                if (path == null) return;
                bool completed = await RunAsync("正在通过取件连接发送…",
                    token => SendAsync(link.Connection, path, token), true, link.Token);
                if (!completed || !link.CanReuse) ClosePickupLink(link);
            }
            finally
            {
                link.SendingFile = false;
                if (pickupLink == link)
                {
                    if (link.Closed.IsCompleted) ClosePickupLink(link, "取件连接已断开。");
                    else link.BeginIdle();
                }
            }
        }

        private async Task WatchPickupSenderAsync(PickupLink link)
        {
            await link.Closed;
            if (!link.SendingFile)
                ClosePickupLink(link, link.Token.IsCancellationRequested ? "取件连接已空闲超时。" : "取件连接已断开。");
        }

        private async Task ReceivePickupFilesAsync(PickupLink link)
        {
            string ended = null;
            try
            {
                do
                {
                    var offer = await FileTransferProtocol.ReadOfferAsync(link.Connection, link.Token,
                        link.CanReuse ? PickupIdleSeconds : 30);
                    if (closing || pickupLink != link) return;
                    if (choosingFile || operation != null)
                    {
                        await FileTransferProtocol.ReceiveAsync(link.Connection, offer, _ => null, null, link.Token);
                        continue;
                    }
                    tools.Hide();
                    transfer.Show();
                    SetReceiveMode(true);
                    if (!await RunAsync("请确认接收文件。", token =>
                    {
                        ShowOrActivate();
                        return ReceiveAsync(link.Connection, token, offer);
                    }, true, link.Token)) break;
                } while (link.CanReuse);
            }
            catch (OperationCanceledException) when (link.Token.IsCancellationRequested) { }
            catch (TimeoutException) { ended = "取件连接已空闲超时。"; }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                System.Diagnostics.Debug.WriteLine(error);
                ended = "取件连接已断开。";
            }
            finally { ClosePickupLink(link, ended); }
        }

        private void DisconnectPickup()
        {
            if (pickupLink != null) ClosePickupLink(pickupLink, "已断开取件连接。");
        }

        private void ClosePickupLink(PickupLink link, string message = null)
        {
            if (pickupLink != link) return;
            pickupLink = null;
            link.Dispose();
            if (closing) return;
            if (message != null && operation == null) status.Text = message;
            UpdateReceptionSuspension();
            UpdateButtons();
        }

        private void UpdateReceptionSuspension()
        {
            reception.SetSuspended(choosingFile || suspendingReception || (pickupLink != null && FileTransferP2p.HasFixedLocalPort));
        }

        private string DirectDiagnostic => directDiagnostic ?? (receiving ? reception.LastDirectDiagnostic : null);

        private void CopyDirectDiagnostic()
        {
            string diagnostic = DirectDiagnostic;
            if (string.IsNullOrEmpty(diagnostic)) return;
            try { Clipboard.SetText(diagnostic); status.Text = "连接诊断已复制。"; }
            catch (ExternalException) { status.Text = "剪贴板正忙，请再点一次「复制诊断」。"; }
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

        private async Task ReceiveAsync(IFileTransferConnection connection, CancellationToken token, IncomingTransfer offer = null)
        {
            status.Text = offer == null ? "已连接，等待文件信息…" : "请确认接收文件。";
            var feedback = new Progress<long>(count => UpdateProgress(count, "接收中"));
            bool saved = offer == null
                ? await FileTransferProtocol.ReceiveAsync(connection, SelectDestination, feedback, token)
                : await FileTransferProtocol.ReceiveAsync(connection, offer, SelectDestination, feedback, token);
            if (saved) UpdateProgress(fileLength, "接收完成");
            status.Text = saved ? "文件已保存。" : "已拒绝接收。";
        }

        private string SelectDestination(IncomingTransfer offer)
        {
            fileLength = offer.Length;
            string sender = new string(Array.FindAll(offer.Sender.ToCharArray(), c => !char.IsControl(c)));
            if (sender.Length == 0) sender = "对方设备";
            using (var dialog = new FolderBrowserDialog
            {
                Description = $"{sender} 发来「{offer.Name}」（{FormatBytes(fileLength)}）。请选择保存文件夹。",
                ShowNewFolderButton = true
            })
            {
                while (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    string destination = UnoccupiedDestination(dialog.SelectedPath, offer.Name);
                    if (destination != null) return destination;
                    if (MessageBox.Show(this, "此文件夹不可用或已有同名项，请选择其他位置。", "文件互传",
                        MessageBoxButtons.RetryCancel, MessageBoxIcon.Information) != DialogResult.Retry) break;
                }
            }
            return null;
        }

        private static string UnoccupiedDestination(string directory, string fileName)
        {
            if (!Directory.Exists(directory)) return null;
            string destination = Path.Combine(directory, fileName);
            return File.Exists(destination) || Directory.Exists(destination) ? null : destination;
        }

        private string ChooseFile()
        {
            if (!CanUseTransfer) return null;
            choosingFile = true;
            UpdateReceptionSuspension();
            try
            {
                using (var dialog = new OpenFileDialog { Title = "选择要发送的文件", CheckFileExists = true })
                    return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
            finally
            {
                choosingFile = false;
                if (!closing && IsHandleCreated) BeginInvoke((Action)(() =>
                {
                    if (!closing) UpdateReceptionSuspension();
                }));
            }
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

        private async Task<bool> RunAsync(string message, Func<CancellationToken, Task> task, bool pauseReception = true,
            CancellationToken token = default)
        {
            if (operation != null || !CanUseTransfer || token.IsCancellationRequested) return false;
            using (var current = CancellationTokenSource.CreateLinkedTokenSource(windowStop.Token, token))
            {
                operation = current;
                if (pauseReception)
                {
                    suspendingReception = true;
                    UpdateReceptionSuspension();
                }
                progress.Visible = false;
                directDiagnostic = null;
                status.Text = message;
                UpdateButtons();
                UpdateReceptionHint();
                try
                {
                    await task(current.Token);
                    return true;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    if (!IsDisposed && !Disposing)
                    {
                        bool wasCanceled = current.IsCancellationRequested;
                        if (!wasCanceled && ex is FileTransferDirectTimeoutException directFailure)
                            directDiagnostic = directFailure.Diagnostic;
                        status.Text = wasCanceled ? "已取消。" : ex.Message;
                    }
                    return false;
                }
                finally
                {
                    operation = null;
                    if (!closing)
                    {
                        bool wasSuspending = suspendingReception;
                        suspendingReception = false;
                        if (wasSuspending) UpdateReceptionSuspension();
                        UpdateButtons();
                        UpdateReceptionHint();
                    }
                }
            }
        }

        private void UpdateButtons()
        {
            bool busy = operation != null;
            bool pendingCode = pickupLink?.Sender == true && pickupLink.Connection == null;
            bool sendingWithCode = pickupLink?.Sender == true && pickupLink.Connection != null;
            back.Enabled = sendTab.Enabled = receiveTab.Enabled = !busy;
            refresh.Enabled = (!busy || pendingCode) && !refreshing;
            devices.Enabled = startReceive.Enabled = pastePickupCode.Enabled = !busy;
            cancel.Visible = busy;
            createPickupCode.Visible = !receiving && (!busy || pendingCode);
            createPickupCode.Enabled = pickupLink == null || pickupLink.Sender;
            createPickupCode.Text = pendingCode ? "复制取件码" : sendingWithCode ? "继续发送" : "生成取件码";
            pickupCodeCard.Visible = !receiving && pendingCode;
            pickupCodeText.Text = pendingCode ? pickupLink.Code : "";
            pickupFileName.Text = pendingCode ? "文件 · " + pickupLink.FirstFileName : "";
            chooseFile.Visible = !receiving && !busy;
            chooseFile.Enabled = SelectedDevice != null;
            startReceive.Visible = pastePickupCode.Visible = receiving && !busy;
            startReceive.Text = reception.Enabled ? "停止接收" : "开始接收";
            disconnectPickup.Visible = !busy && pickupLink?.Connection != null;
            copyDiagnostics.Visible = !busy && !disconnectPickup.Visible && !string.IsNullOrEmpty(DirectDiagnostic);
        }

        private void DrawDevice(Graphics graphics, Rectangle bounds, TransferDevice device, int selectorLeft)
        {
            using (var pen = new Pen(theme.Divider)) graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(theme.Icon, Math.Max(1, Px(1))))
            {
                int x = bounds.X + Px(13), y = bounds.Y + (bounds.Height - Px(15)) / 2;
                graphics.DrawRectangle(pen, x, y, Px(17), Px(11));
                graphics.DrawLine(pen, x + Px(8), y + Px(12), x + Px(8), y + Px(15));
                graphics.DrawLine(pen, x + Px(4), y + Px(15), x + Px(12), y + Px(15));
            }
            const TextFormatFlags textFlags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int left = bounds.X + Px(44);
            int right = selectorLeft - Px(8);
            int gap = Px(10);
            int networkWidth = TextRenderer.MeasureText(graphics, device.Network, bodyFont, Size.Empty, textFlags).Width;
            int nameWidth = Math.Min(TextRenderer.MeasureText(graphics, device.Name, bodyFont, Size.Empty, textFlags).Width,
                Math.Max(1, right - left - gap - networkWidth));
            var nameBounds = new Rectangle(left, bounds.Y, Math.Max(1, nameWidth), bounds.Height);
            TextRenderer.DrawText(graphics, device.Name, bodyFont, nameBounds, theme.Text, textFlags | TextFormatFlags.EndEllipsis);
            int networkLeft = nameBounds.Right + gap;
            var networkBounds = new Rectangle(networkLeft, bounds.Y, Math.Max(1, right - networkLeft), bounds.Height);
            TextRenderer.DrawText(graphics, device.Network, bodyFont, networkBounds, theme.Icon, textFlags | TextFormatFlags.EndEllipsis);
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

        private void PaintRoundedCard(Graphics graphics, Rectangle bounds, int radius)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = Rounded(bounds, radius))
            using (var paper = new SolidBrush(theme.Body))
            using (var outline = new Pen(theme.Divider))
            {
                graphics.FillPath(paper, shape);
                graphics.DrawPath(outline, shape);
            }
        }

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
            if (disposing && !closing)
            {
                closing = true;
                discoveryTimer.Dispose();
                transferHelp?.Dispose();
                windowStop.Cancel();
                DisconnectPickup();
                windowStop.Dispose();
                reception.Changed -= UpdateReception;
                reception.SetToolActive(false);
                reception.SetSuspended(false);
            }
            base.Dispose(disposing);
            if (disposing) { appIcon?.Dispose(); bodyFont?.Dispose(); headingFont?.Dispose(); pickupCodeFont?.Dispose(); }
        }

        private sealed class PickupLink : IDisposable
        {
            private readonly CancellationTokenSource stop;
            internal string Code { get; }
            internal string FirstFileName { get; }
            internal bool Sender { get; }
            internal CancellationToken Token { get; }
            internal IFileTransferConnection Connection { get; private set; }
            internal Task Closed { get; private set; }
            internal bool CanReuse { get; private set; }
            internal bool SendingFile { get; set; }

            internal PickupLink(string code, bool sender, CancellationToken windowToken, string firstFileName)
            {
                Code = code;
                FirstFileName = firstFileName;
                Sender = sender;
                stop = CancellationTokenSource.CreateLinkedTokenSource(windowToken);
                Token = stop.Token;
            }

            internal void Attach(IFileTransferConnection channel, bool canReuse, Task closed)
            {
                Connection = channel;
                CanReuse = canReuse;
                Closed = closed;
            }

            internal void BeginIdle() => stop.CancelAfter(TimeSpan.FromSeconds(CanReuse ? PickupIdleSeconds : 25));
            internal void PauseIdle() => stop.CancelAfter(Timeout.Infinite);
            internal void Cancel() { if (!Token.IsCancellationRequested) stop.Cancel(); }

            public void Dispose()
            {
                Cancel();
                Connection?.Dispose();
                stop.Dispose();
            }
        }

        private sealed class TransferButton : Button
        {
            private readonly NoteTheme theme;
            private readonly Color accent;
            private readonly int radius;
            private bool hot;
            internal bool Primary;
            internal bool ShowSelectedWhenDisabled;
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
                bool selected = Primary && (Enabled || ShowSelectedWhenDisabled);
                var fill = selected ? (hot && Enabled ? Mix(accent, Color.Black, .08) : accent) : hot && Enabled ? theme.Body : surface;
                using (var shape = Rounded(rect, radius))
                using (var brush = new SolidBrush(fill))
                using (var outline = new Pen(selected ? fill : theme.Divider))
                {
                    g.FillPath(brush, shape);
                    if (!LinkStyle) g.DrawPath(outline, shape);
                }
                if (DrawContent == null) TextRenderer.DrawText(g, Text, Font, rect,
                    selected ? Color.White : !Enabled ? Color.Gray : LinkStyle ? accent : theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                else DrawContent(g, rect);
                if (Focused && ShowFocusCues) { rect.Inflate(-4, -4); ControlPaint.DrawFocusRectangle(g, rect); }
            }
        }

        private sealed class RoundedHelpPopup : ToolStripDropDown
        {
            private readonly int radius;

            internal RoundedHelpPopup(Color border, int radius)
            {
                this.radius = radius;
                AutoClose = true;
                BackColor = Color.White;
                DropShadowEnabled = false;
                Renderer = new HelpFrameRenderer(border, radius);
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                if (radius < 1 || ClientSize.Width < 2 || ClientSize.Height < 2) return;
                using (var silhouette = Rounded(ClientRectangle, radius))
                {
                    var previous = Region;
                    Region = new Region(silhouette);
                    previous?.Dispose();
                }
            }
        }

        private sealed class HelpFrameRenderer : ToolStripRenderer
        {
            private readonly Color border;
            private readonly int radius;

            internal HelpFrameRenderer(Color border, int radius)
            {
                this.border = border;
                this.radius = radius;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.FillRectangle(Brushes.White, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                var bounds = Rectangle.Inflate(e.ToolStrip.ClientRectangle, -1, -1);
                if (bounds.Width < 2 || bounds.Height < 2) return;
                bounds.Width--;
                bounds.Height--;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var outline = Rounded(bounds, radius - 1))
                using (var pen = new Pen(border))
                    e.Graphics.DrawPath(pen, outline);
            }
        }

        private class BufferedPanel : Panel
        {
            internal BufferedPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }

        private sealed class DeviceFrame : BufferedPanel
        {
            private readonly Color border;
            private readonly int radius;

            internal DeviceFrame(Color border, int radius)
            {
                this.border = border;
                this.radius = radius;
                BackColor = Color.White;
                // Native child controls are rectangular. Keep them out of the rounded corners.
                Padding = new Padding(2, radius + 1, 2, radius + 1);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
                if (bounds.Width < 2 || bounds.Height < 2) return;
                bounds.Width--;
                bounds.Height--;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var outline = Rounded(bounds, radius))
                using (var pen = new Pen(border))
                    e.Graphics.DrawPath(pen, outline);
            }
        }

        private sealed class TransferDevice
        {
            private readonly LanTransferDevice local;
            private readonly AccountTransferDevice account;
            internal string Name => local?.Name ?? account.Name;
            internal string Network => local != null ? "局域网传输" : "无线传输";
            internal string Key { get; }

            internal TransferDevice(LanTransferDevice device)
            {
                local = device;
                Key = "lan:" + Convert.ToBase64String(device.Fingerprint);
            }
            internal TransferDevice(AccountTransferDevice device)
            {
                account = device;
                Key = "account:" + device.Id;
            }
            internal Task<IFileTransferConnection> ConnectAsync(CancellationToken token) => local != null
                ? FileTransferLan.ConnectAsync(local, token) : FileTransferAccount.ConnectAsync(account, token);
            public override string ToString() => Name + " · " + Network;
        }
    }
}
