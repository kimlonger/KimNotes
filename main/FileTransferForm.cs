using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    internal sealed partial class FileTransferForm : AppWindow
    {
        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);

        private const string ReceiveCodeHint = "请粘贴有效的取件码。";
        private const int PickupIdleSeconds = 120;
        private readonly float scale = UiDpi.Factor;
        private readonly NoteTheme theme = NoteTheme.Current();
        private readonly Font bodyFont, headingFont, pickupCodeFont, completeFont;
        private readonly Icon appIcon;
        private readonly Bitmap fileTransferIcon;
        private readonly FlowLayoutPanel tools;
        private readonly Panel transfer, sendView, receiveView;
        private readonly Label status;
        private readonly RowStyle statusSpace;
        private readonly TransferButton back, sendTab, receiveTab, copyDiagnostics, disconnectPickup;
        private readonly Color accent;
        private readonly CancellationTokenSource windowStop;
        private readonly System.Windows.Forms.Timer attentionTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly FileTransferReception reception;
        private CancellationTokenSource operation;
        private PickupLink pickupLink;
        private string lastUsedPickupCode, directDiagnostic;
        private string savedFolder, savedFileName, activeFileName, activePickupRoute;
        private string shortPickupCode;
        private FileTransferBundle activeBundle;
        private long activeTransferredBytes;
        private bool sendComplete;
        private int percent;
        private bool receiving, choosingFile, suspendingReception, closing, progressActive, transferPageOpen;
        private long fileLength;
        private bool CanUseTransfer => !closing && reception.IsSignedIn && !windowStop.IsCancellationRequested;
        internal bool HasActiveTransfer => operation != null && !operation.IsCancellationRequested;
        private bool CanBackgroundCurrentTransfer =>
            CanUseTransfer && !DesktopSession.Exiting && HasActiveTransfer && progressActive && !receiveOfferPending;

        internal FileTransferForm(FileTransferReception reception)
        {
            this.reception = reception;
            windowStop = CancellationTokenSource.CreateLinkedTokenSource(reception.TransferLifetime);
            Text = "桌面工具";
            AccessibleName = Text;
            appIcon = Icon.ExtractAssociatedIcon(typeof(FileTransferForm).Assembly.Location);
            if (appIcon != null) Icon = appIcon;
            using (var stream = typeof(FileTransferForm).Assembly.GetManifestResourceStream("KimNotes.file-transfer.png"))
            using (var image = new Bitmap(stream))
                fileTransferIcon = new Bitmap(image);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            var active = ActiveForm;
            var area = (active == null ? Screen.FromPoint(Cursor.Position) : Screen.FromControl(active)).WorkingArea;
            ClientSize = CompactLayout.DefaultNoteSize(scale, area.Size);
            MinimumSize = new Size(ClientSize.Width, Math.Min(Px(360), ClientSize.Height));
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
            headingFont = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            pickupCodeFont = new Font("Consolas", 25f, FontStyle.Bold);
            completeFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);
            Font = bodyFont;
            accent = Mix(theme.IconHover, Color.Black, .15);

            var content = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.White };
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
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(44)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(45)));
            statusSpace = new RowStyle(SizeType.Absolute, 0);
            layout.RowStyles.Add(statusSpace);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            transfer.Controls.Add(layout);

            back = Button("返回", () => ShowTools(), Px(33), Px(28));
            back.LinkStyle = true;
            back.DrawContent = (g, r) => DrawArrow(g, r, theme.Icon, -1);
            var title = new Label { Text = "文件互传", Font = headingFont, ForeColor = theme.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            var titleRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var backSlot = new Panel { Dock = DockStyle.Left, Width = Px(38) };
            back.Dock = DockStyle.Fill;
            backSlot.Controls.Add(back);
            titleRow.Controls.Add(title);
            titleRow.Controls.Add(backSlot);
            layout.Controls.Add(titleRow, 0, 0);

            sendTab = Button("发送", () => SetReceiveMode(false), Px(75), Px(30), true);
            receiveTab = Button("接收", () => SetReceiveMode(true), Px(75), Px(30));
            sendTab.Name = "send-tab";
            receiveTab.Name = "receive-tab";
            foreach (var mode in new[] { sendTab, receiveTab })
            {
                mode.SegmentStyle = true;
                mode.AccessibleRole = AccessibleRole.PageTab;
                mode.PreviewKeyDown += (s, e) => e.IsInputKey = e.KeyCode == Keys.Left || e.KeyCode == Keys.Right;
                mode.KeyDown += SwitchTransferTabOnArrow;
            }
            var railColor = Mix(theme.Body, Color.White, .12);
            var rail = new BufferedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Px(8)), Padding = new Padding(Px(3)) };
            rail.Paint += (s, e) => PaintCard(e.Graphics, rail.ClientRectangle, railColor, theme.Divider, Px(9));
            var tabs = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1, BackColor = railColor };
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sendTab.Dock = receiveTab.Dock = DockStyle.Fill;
            sendTab.Margin = receiveTab.Margin = Padding.Empty;
            tabs.Controls.Add(sendTab, 0, 0);
            tabs.Controls.Add(receiveTab, 1, 0);
            rail.Controls.Add(tabs);
            layout.Controls.Add(rail, 0, 1);

            var workArea = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            sendView = new BufferedPanel { Dock = DockStyle.Fill };
            receiveView = new BufferedPanel { Dock = DockStyle.Fill, Visible = false };
            workArea.Controls.Add(sendView);
            workArea.Controls.Add(receiveView);
            layout.Controls.Add(workArea, 0, 3);

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
            layout.Controls.Add(statusRow, 0, 2);

            BuildSendView();
            BuildReceiveView();
            status.TextChanged += (s, e) => UpdateStatusLayout();
            transfer.VisibleChanged += (s, e) => UpdateToolActive();
            VisibleChanged += (s, e) => UpdateToolActive();
            Resize += (s, e) => UpdateToolActive();
            reception.Changed += UpdateReception;
            UpdateButtons();
        }

        private int Px(int value) => (int)Math.Round(value * scale);

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x0232 && !closing && IsHandleCreated && Visible) // WM_EXITSIZEMOVE
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
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
                var glyph = Rectangle.Inflate(icon, -Px(8), -Px(8));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(fileTransferIcon, glyph);
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
            ShowToolPage(false);
            tools.Controls[0].Focus();
        }

        private void OpenFileTransfer()
        {
            if (!CanUseTransfer) return;
            ShowToolPage(true);
            SetReceiveMode(false);
        }

        private void ShowToolPage(bool showTransfer)
        {
            if (transferPageOpen == showTransfer) return;
            transferPageOpen = showTransfer;
            SuspendLayout();
            try
            {
                tools.Visible = !showTransfer;
                transfer.Visible = showTransfer;
            }
            finally { ResumeLayout(true); }
        }

        private void BackgroundCurrentTransfer()
        {
            if (!CanBackgroundCurrentTransfer) return;
            attentionTimer.Stop();
            TopMost = false;
            Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && CanBackgroundCurrentTransfer)
            {
                e.Cancel = true;
                BackgroundCurrentTransfer();
            }
            base.OnFormClosing(e);
        }

        private void SetReceiveMode(bool receive)
        {
            if (operation != null || choosingFile || !CanUseTransfer || receiving == receive) return;
            receiving = receive;
            sendTab.Primary = !receive;
            receiveTab.Primary = receive;
            sendTab.Invalidate();
            receiveTab.Invalidate();
            receiveView.Visible = receive;
            sendView.Visible = !receive;
            directDiagnostic = null;
            status.Text = receive && pickupLink?.Connection != null && savedFolder == null
                ? "取件已连接，等待对方发送。"
                : "";
            UpdateButtons();
        }

        private void SwitchTransferTabOnArrow(object sender, KeyEventArgs e)
        {
            if (e.Modifiers != Keys.None || (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right)) return;
            e.SuppressKeyPress = true;
            if (operation != null || choosingFile || !CanUseTransfer) return;
            bool receive = e.KeyCode == Keys.Right;
            SetReceiveMode(receive);
            (receive ? receiveTab : sendTab).Focus();
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
            UpdateButtons();
        }

        internal Task ReceiveIncomingAsync(IFileTransferConnection connection, IncomingTransfer offer, CancellationToken token)
        {
            if (!CanUseTransfer || choosingFile || operation != null || token.IsCancellationRequested) return Task.CompletedTask;
            ShowToolPage(true);
            SetReceiveMode(true);
            return RunAsync("请确认接收文件。", cancel =>
            {
                ShowOrActivate();
                return ReceiveAsync(connection, cancel, offer);
            }, false, token);
        }

        private void CopyPickupCode(string code)
        {
            try { Clipboard.SetText(code); status.Text = "取件码已复制。"; }
            catch (ExternalException) { status.Text = "剪贴板正忙，请再点一次「复制取件码」。"; }
            UpdateSendView();
        }

        private async void ReceivePickupCode()
        {
            if (!CanUseTransfer) return;
            if (!FileTransferInvite.TryRead(receiveCodeInput.Text, out string code))
            {
                status.Text = ReceiveCodeHint;
                return;
            }
            if (pickupLink != null)
            {
                status.Text = pickupLink.Code == code ? "取件码已使用，当前连接仍可继续接收。" : "请先断开当前取件连接。";
                return;
            }
            if (FileTransferInvite.IsShortCode(code))
            {
                await ReceiveShortPickupAsync(code);
                return;
            }
            if (lastUsedPickupCode == code)
            {
                status.Text = "此取件码已使用，请获取新的取件码。";
                return;
            }
            await ConnectLegacyPickupAsync(code);
        }

        private async Task ConnectLegacyPickupAsync(string code)
        {
            if (operation != null || pickupLink != null || !CanUseTransfer) return;
            var link = new PickupLink(code, windowStop.Token);
            pickupLink = link;
            UpdateReceptionSuspension();
            bool connected = await RunAsync("正在连接发送方…", async token =>
            {
                using (token.Register(link.Cancel))
                {
                    var wire = await FileTransferP2p.AcceptAsync(code, link.Token);
                    try
                    {
                        var channel = await EncryptedFileTransfer.OpenPickupAsync(wire, code, false, link.Token);
                        token.ThrowIfCancellationRequested();
                        link.Attach(channel.Connection, channel.CanReuse);
                        link.Route = FileTransferP2p.TransferRoute(wire);
                    }
                    catch { wire.Dispose(); throw; }
                }
            }, false);
            if (!connected || closing)
            {
                ClosePickupLink(link);
                return;
            }
            lastUsedPickupCode = code;
            receiveCodeInput.Clear();
            status.Text = "取件已连接，等待对方发送。";
            _ = ReceivePickupFilesAsync(link);
            UpdateButtons();
        }

        private bool CanStartBundleSend => CanUseTransfer && !receiving && !choosingFile && operation == null && pickupLink == null;

        private void ChooseBundle(bool folder)
        {
            if (!CanStartBundleSend) return;
            string[] paths = null;
            choosingFile = true;
            UpdateReceptionSuspension();
            try
            {
                if (folder)
                {
                    string path = TransferFolderPicker.Choose(this, null, windowStop.Token, sending: true);
                    if (path != null) paths = new[] { path };
                }
                else
                {
                    using (var dialog = new OpenFileDialog { Title = "选择要发送的文件", CheckFileExists = true, Multiselect = true })
                        if (dialog.ShowDialog(this) == DialogResult.OK) paths = dialog.FileNames;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is COMException)
            { status.Text = "无法打开选择窗口，请重试。"; }
            finally
            {
                choosingFile = false;
                if (!closing) UpdateReceptionSuspension();
            }
            if (paths != null && CanStartBundleSend) _ = SendBundleAsync(paths);
        }

        private async Task SendBundleAsync(string[] paths)
        {
            if (!CanStartBundleSend || paths.Length == 0) return;
            await RunAsync("正在准备文件…", async token =>
            {
                var selection = await Task.Run(() => FileTransferBundle.Create(paths, token), token);
                token.ThrowIfCancellationRequested();
                activeBundle = selection;
                fileLength = selection.Length;
                var id = Guid.NewGuid();
                using (var pickup = new FileTransferPickup())
                {
                    try
                    {
                        var registration = await pickup.RegisterAsync(id, token);
                        shortPickupCode = registration.Code;
                        status.Text = "";
                        UpdateButtons();
                        string key = await pickup.WaitForReceiverAsync(id, registration.ExpiresIn, token);
                        shortPickupCode = null;
                        status.Text = "正在连接接收方…";
                        UpdateButtons();
                        using (var wire = await FileTransferP2p.ConnectAsync(key, token))
                        using (var connection = await EncryptedFileTransfer.OpenAsync(wire, key, true, token))
                        {
                            token.ThrowIfCancellationRequested();
                            activePickupRoute = FileTransferP2p.TransferRoute(wire);
                            status.Text = "等对方确认文件";
                            UpdateButtons();
                            sendComplete = await FileTransferBundle.SendAsync(connection, selection, TransferFeedback("发送中"), token);
                            status.Text = sendComplete ? "" : "对方没有接收。";
                        }
                    }
                    finally
                    {
                        shortPickupCode = null;
                        await pickup.CancelAsync(id);
                    }
                }
            });
        }

        private async Task ReceiveShortPickupAsync(string code)
        {
            await RunAsync("正在连接发送方…", async token =>
            {
                using (var pickup = new FileTransferPickup())
                {
                    string key = await pickup.ClaimAsync(code, token);
                    receiveCodeInput.Clear();
                    using (var wire = await FileTransferP2p.AcceptAsync(key, token))
                    using (var connection = await EncryptedFileTransfer.OpenAsync(wire, key, false, token))
                    {
                        activePickupRoute = FileTransferP2p.TransferRoute(wire);
                        var offer = await FileTransferBundle.ReadOfferAsync(connection, token);
                        activeBundle = offer;
                        fileLength = offer.Length;
                        status.Text = "";
                        string destination;
                        bool timedOut;
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FileTransferProtocol.OfferDecisionTimeoutSeconds - 5)))
                        using (var selection = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
                        {
                            destination = await ChooseReceiveDestinationAsync(offer, selection.Token);
                            timedOut = deadline.IsCancellationRequested;
                            if (selection.IsCancellationRequested) destination = null;
                        }
                        if (destination != null) UpdateProgress(0, "接收中");
                        savedFolder = await FileTransferBundle.ReceiveAsync(connection, offer, destination, TransferFeedback("接收中"), token);
                        savedFileName = savedFolder == null ? null : offer.Name;
                        status.Text = savedFolder != null ? "" : timedOut ? "确认超时，请让对方重新发送。" : "已拒绝接收。";
                    }
                }
            });
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
                    ShowToolPage(true);
                    SetReceiveMode(true);
                    if (!await RunAsync("请确认接收文件。", token =>
                    {
                        activePickupRoute = link.Route;
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
            if (message != null && operation == null && savedFolder == null) status.Text = message;
            UpdateReceptionSuspension();
            UpdateButtons();
        }

        private void UpdateReceptionSuspension()
        {
            reception.SetSuspended(choosingFile || suspendingReception || (pickupLink != null && FileTransferP2p.HasFixedLocalPort));
        }

        private void CopyDirectDiagnostic()
        {
            string diagnostic = directDiagnostic;
            if (string.IsNullOrEmpty(diagnostic)) return;
            try { Clipboard.SetText(diagnostic); status.Text = "连接诊断已复制。"; }
            catch (ExternalException) { status.Text = "剪贴板正忙，请再点一次「复制诊断」。"; }
        }

        private async Task ReceiveAsync(IFileTransferConnection connection, CancellationToken token, IncomingTransfer offer = null)
        {
            if (offer == null)
            {
                status.Text = "已连接，等待文件信息…";
                offer = await FileTransferProtocol.ReadOfferAsync(connection, token);
            }
            token.ThrowIfCancellationRequested();
            status.Text = "";
            fileLength = offer.Length;
            activeFileName = offer.Name;
            string destination;
            bool timedOut;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FileTransferProtocol.OfferDecisionTimeoutSeconds - 5)))
            using (var selection = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                destination = await ChooseReceiveDestinationAsync(offer, selection.Token);
                timedOut = deadline.IsCancellationRequested;
                if (selection.IsCancellationRequested) destination = null;
            }

            if (destination != null)
            {
                percent = 0;
                progressActive = true;
                status.Text = $"接收中 · {FormatBytes(0)} / {FormatBytes(fileLength)}";
                UpdateButtons();
            }
            bool saved = await FileTransferProtocol.ReceiveAsync(connection, offer, _ => destination, TransferFeedback("接收中"), token);
            if (saved)
            {
                savedFolder = Path.GetDirectoryName(Path.GetFullPath(destination));
                savedFileName = Path.GetFileName(destination);
            }
            status.Text = saved ? "文件已保存。" : timedOut ? "接收请求已超时，请让对方重新发送。" : "已拒绝接收。";
        }

        private void ClearReceiveResult()
        {
            savedFolder = savedFileName = activeFileName = activePickupRoute = null;
            activeBundle = null;
            sendComplete = progressActive = false;
            status.Text = "";
            UpdateButtons();
        }

        private void OpenSavedFolder() => OpenTransferFolder(savedFolder);

        private void OpenTransferFolder(string folder)
        {
            if (folder == null) return;
            try
            {
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception error) when (error is DirectoryNotFoundException || error is System.ComponentModel.Win32Exception)
            {
                if (IsDisposed || !CanUseTransfer) return;
                ShowOrActivate();
                status.Text = error is DirectoryNotFoundException ? "保存文件夹已不可用。" : "无法打开保存文件夹。";
                if (error is DirectoryNotFoundException && savedFolder == folder)
                {
                    savedFolder = null;
                    UpdateButtons();
                }
            }
        }

        private IProgress<long> TransferFeedback(string label)
        {
            var current = operation;
            return new Progress<long>(count =>
            {
                if (ReferenceEquals(operation, current)) UpdateProgress(count, label);
            });
        }

        private void UpdateProgress(long count, string label)
        {
            if (IsDisposed || Disposing || operation == null || operation.IsCancellationRequested) return;
            activeTransferredBytes = count;
            percent = fileLength == 0 ? 100 : (int)Math.Min(100, count * 100d / fileLength);
            progressActive = true;
            status.Text = $"{label} · {FormatBytes(count)} / {FormatBytes(fileLength)}";
            UpdateButtons();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            return bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : $"{bytes / 1024d:0.##} KiB";
        }

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
                directDiagnostic = null;
                savedFolder = savedFileName = activeFileName = activePickupRoute = null;
                activeBundle = null;
                activeTransferredBytes = 0;
                percent = 0;
                sendComplete = progressActive = false;
                status.Text = message;
                UpdateButtons();
                bool failed = false;
                try
                {
                    await task(current.Token);
                    return true;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    failed = !current.IsCancellationRequested;
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
                    progressActive = false;
                    if (!closing)
                    {
                        bool wasSuspending = suspendingReception;
                        suspendingReception = false;
                        if (wasSuspending) UpdateReceptionSuspension();
                        UpdateButtons();
                        NotifyBackgroundResult(failed);
                    }
                }
            }
        }

        private void NotifyBackgroundResult(bool failed)
        {
            if (!CanUseTransfer || DesktopSession.Exiting || Visible && WindowState != FormWindowState.Minimized) return;
            var screen = Screen.FromControl(this);
            var detail = CleanTransferLabel(activeBundle?.Name ?? savedFileName);
            if (receiving && savedFolder != null)
            {
                string folder = savedFolder;
                FileTransferToast.Show(screen, theme, "文件已接收", detail, "打开文件夹", () => OpenTransferFolder(folder));
            }
            else if (!receiving && sendComplete)
                FileTransferToast.Show(screen, theme, "文件已传输", detail);
            else if (failed)
                FileTransferToast.Show(screen, theme, receiving ? "接收失败" : "传输失败", status.Text, "查看", ShowOrActivate);
        }

        private void UpdateButtons()
        {
            bool busy = operation != null;
            back.Enabled = sendTab.Enabled = receiveTab.Enabled = !busy;
            UpdateSendView();
            UpdateReceiveView();
            UpdateStatusLayout();
        }

        private void UpdateStatusLayout()
        {
            bool busy = operation != null;
            bool showConnection = !busy && pickupLink?.Connection != null;
            bool showDiagnostic = !busy && !showConnection && !string.IsNullOrEmpty(directDiagnostic);
            bool showStatus = status.Text.Length != 0 && (!busy ||
                (receiving ? !receiveOfferPending && !receiveProgressVisible : activeBundle == null));
            status.Visible = showStatus;
            disconnectPickup.Visible = showConnection;
            copyDiagnostics.Visible = showDiagnostic;
            statusSpace.Height = showStatus || showConnection || showDiagnostic ? Px(30) : 0;
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

        private static void PaintCard(Graphics graphics, Rectangle bounds, Color fill, Color border, int radius, float borderWidth = 1)
        {
            bounds.Width--;
            bounds.Height--;
            if (bounds.Width < 2 || bounds.Height < 2) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = Rounded(bounds, radius))
            using (var paper = new SolidBrush(fill))
            {
                graphics.FillPath(paper, shape);
                if (!border.IsEmpty)
                {
                    using (var outline = new Pen(border, borderWidth) { Alignment = PenAlignment.Inset })
                        graphics.DrawPath(outline, shape);
                }
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
                windowStop.Cancel();
                DisconnectPickup();
                windowStop.Dispose();
                reception.Changed -= UpdateReception;
                reception.SetToolActive(false);
                reception.SetSuspended(false);
            }
            base.Dispose(disposing);
            if (disposing)
            {
                appIcon?.Dispose();
                fileTransferIcon?.Dispose();
                bodyFont?.Dispose();
                headingFont?.Dispose();
                pickupCodeFont?.Dispose();
                completeFont?.Dispose();
            }
        }

        private sealed class PickupLink : IDisposable
        {
            private readonly CancellationTokenSource stop;
            internal string Code { get; }
            internal string Route { get; set; }
            internal CancellationToken Token { get; }
            internal IFileTransferConnection Connection { get; private set; }
            internal bool CanReuse { get; private set; }

            internal PickupLink(string code, CancellationToken windowToken)
            {
                Code = code;
                stop = CancellationTokenSource.CreateLinkedTokenSource(windowToken);
                Token = stop.Token;
            }

            internal void Attach(IFileTransferConnection channel, bool canReuse)
            {
                Connection = channel;
                CanReuse = canReuse;
            }

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
            internal bool LinkStyle;
            internal bool SegmentStyle;
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
                bool selected = Primary && (Enabled || SegmentStyle);
                var fill = selected ? (hot && Enabled ? Mix(accent, Color.Black, .08) : accent)
                    : hot && Enabled ? Mix(theme.Body, Color.White, .12) : surface;
                using (var shape = Rounded(rect, radius))
                using (var brush = new SolidBrush(fill))
                using (var outline = new Pen(selected ? fill : theme.Divider))
                {
                    g.FillPath(brush, shape);
                    if (!LinkStyle && !SegmentStyle) g.DrawPath(outline, shape);
                }
                if (DrawContent == null) TextRenderer.DrawText(g, Text, Font, rect,
                    selected ? Color.White : !Enabled ? Color.Gray : SegmentStyle ? theme.Icon : LinkStyle ? accent : theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                else DrawContent(g, rect);
                if (Focused && ShowFocusCues) { rect.Inflate(-4, -4); ControlPaint.DrawFocusRectangle(g, rect); }
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

    }
}
