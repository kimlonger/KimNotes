using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    internal sealed partial class FileTransferForm
    {
        private Control receiveEntry, receiveOfferCard, receiveCompleteCard;
        private TransferProgressView receiveProgressView;
        private Label receiveSender, receiveOfferName, receiveOfferSize, receiveOfferFeedback, receiveSavedFile, receiveSavedLocation;
        private TransferButton receiveUseCode, receiveSecondary;
        private TableLayoutPanel receiveActions;
        private TextBox receiveCodeInput;
        private TaskCompletionSource<string> receiveDecision;
        private CancellationToken receiveDecisionToken;
        private string receiveOfferedFileName;
        private FileTransferBundle receiveOfferedBundle;
        private bool receiveOfferPending => receiveDecision != null;
        private bool receiveProgressVisible => receiving && operation != null && progressActive && !receiveOfferPending;

        private void BuildReceiveView()
        {
            var surface = TransferRows(-1, 42);
            receiveView.Controls.Add(surface);
            var content = new BufferedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, Px(12), 0, Px(10)) };
            surface.Controls.Add(content, 0, 0);

            receiveEntry = new BufferedPanel { Name = "receive-code-entry", Dock = DockStyle.Fill };
            var entry = TransferRows(20, 38, 62, -1);
            var hint = TransferLabel("输入对方给你的取件码", theme.Icon, ContentAlignment.MiddleCenter);
            hint.Name = "receive-code-hint";
            entry.Controls.Add(hint, 0, 1);
            var inputFrame = new BufferedPanel
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, Px(5), 0, 0),
                Padding = new Padding(Px(12), Px(8), Px(12), Px(4)), Cursor = Cursors.IBeam
            };
            receiveCodeInput = new TextBox
            {
                Name = "receive-code-input", AccessibleName = "输入 8 位取件码", MaxLength = 2048,
                Dock = DockStyle.Top, BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center,
                BackColor = Color.White, ForeColor = theme.Text, Font = pickupCodeFont
            };
            receiveCodeInput.GotFocus += (s, e) => inputFrame.Invalidate();
            receiveCodeInput.LostFocus += (s, e) => inputFrame.Invalidate();
            receiveCodeInput.TextChanged += (s, e) => UpdateReceiveInput();
            receiveCodeInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter || operation != null || receiveCodeInput.TextLength == 0) return;
                e.SuppressKeyPress = true;
                ReceivePickupCode();
            };
            inputFrame.Paint += (s, e) => PaintCard(e.Graphics, inputFrame.ClientRectangle, Color.White,
                receiveCodeInput.Focused ? accent : theme.Divider, Px(9));
            inputFrame.Click += (s, e) => receiveCodeInput.Focus();
            inputFrame.Controls.Add(receiveCodeInput);
            entry.Controls.Add(inputFrame, 0, 2);
            receiveEntry.Controls.Add(entry);
            content.Controls.Add(receiveEntry);

            receiveOfferCard = new BufferedPanel { Name = "receive-offer", Dock = DockStyle.Fill };
            var offerRows = TransferRows(37, 26, 68, 30, -1);
            var offerTitle = TransferLabel("确认接收", theme.Text);
            offerTitle.Font = headingFont;
            offerRows.Controls.Add(offerTitle, 0, 0);
            receiveSender = TransferLabel("", theme.Icon);
            offerRows.Controls.Add(receiveSender, 0, 1);
            var fileCard = new BufferedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, Px(8), 0, 0), Padding = new Padding(Px(12), Px(5), Px(12), Px(5)) };
            var fill = Mix(theme.Body, Color.White, .46);
            fileCard.Paint += (s, e) => PaintCard(e.Graphics, fileCard.ClientRectangle, fill, theme.Divider, Px(9));
            var fileRows = TransferRows(27, -1);
            fileRows.BackColor = fill;
            receiveOfferName = TransferLabel("", theme.Text);
            receiveOfferName.Name = "receive-file-name";
            receiveOfferSize = TransferLabel("", theme.Icon);
            fileRows.Controls.Add(receiveOfferName, 0, 0);
            fileRows.Controls.Add(receiveOfferSize, 0, 1);
            fileCard.Controls.Add(fileRows);
            offerRows.Controls.Add(fileCard, 0, 2);
            receiveOfferFeedback = TransferLabel("", theme.Icon);
            receiveOfferFeedback.Name = "receive-offer-feedback";
            offerRows.Controls.Add(receiveOfferFeedback, 0, 3);
            receiveOfferCard.Controls.Add(offerRows);
            content.Controls.Add(receiveOfferCard);

            receiveProgressView = new TransferProgressView(this) { Name = "receive-progress" };
            content.Controls.Add(receiveProgressView);
            receiveCompleteCard = BuildTransferComplete(out receiveSavedFile, out receiveSavedLocation);
            receiveCompleteCard.Name = "receive-complete";
            content.Controls.Add(receiveCompleteCard);

            receiveUseCode = Button("粘贴取件码", OnReceivePrimary, 1, 1, true);
            receiveUseCode.Name = "receive-use-code";
            receiveSecondary = Button("拒绝", OnReceiveSecondary, 1, 1);
            receiveSecondary.Name = "receive-secondary";
            receiveActions = TransferActionRow(receiveSecondary, receiveUseCode);
            surface.Controls.Add(receiveActions, 0, 1);

            var toolTips = new ToolTip();
            Disposed += (s, e) => toolTips.Dispose();
            foreach (var label in new[] { receiveSender, receiveOfferName, receiveOfferFeedback, receiveSavedFile, receiveSavedLocation })
                label.TextChanged += (s, e) => toolTips.SetToolTip(label, label.Text);
        }

        private void OnReceivePrimary()
        {
            if (receiveOfferPending) ChooseReceiveFolder();
            else if (CanBackgroundCurrentTransfer) BackgroundCurrentTransfer();
            else if (operation != null) operation.Cancel();
            else if (savedFolder != null) OpenSavedFolder();
            else SubmitReceiveCode();
        }

        private void OnReceiveSecondary()
        {
            if (receiveOfferPending) receiveDecision.TrySetResult(null);
            else if (CanBackgroundCurrentTransfer) operation?.Cancel();
            else ClearReceiveResult();
        }

        private void UpdateReceiveView()
        {
            bool busy = operation != null;
            bool offer = receiveOfferPending;
            bool inProgress = receiveProgressVisible;
            bool background = inProgress && CanBackgroundCurrentTransfer;
            bool complete = !busy && savedFolder != null;
            receiveEntry.Visible = !offer && !inProgress && !complete;
            receiveOfferCard.Visible = offer;
            receiveProgressView.Visible = inProgress;
            receiveCompleteCard.Visible = complete;
            receiveSecondary.Visible = offer || complete || background;
            receiveActions.ColumnStyles[0].Width = offer || complete || background ? 45 : 0;
            receiveSecondary.Text = offer ? "拒绝" : background ? "取消传输" : "完成";
            UpdateReceiveInput();
            if (inProgress)
                receiveProgressView.UpdateTransfer(activeBundle, activePickupRoute, activeTransferredBytes, fileLength, percent, activeFileName);
            if (complete)
            {
                receiveSavedFile.Text = activeBundle == null ? "已收到 " + CleanTransferLabel(savedFileName) : "已收到 " + BundleCounts(activeBundle);
                receiveSavedLocation.Text = savedFolder;
            }
        }

        private void UpdateReceiveInput()
        {
            bool busy = operation != null;
            bool idle = !busy && savedFolder == null;
            bool background = CanBackgroundCurrentTransfer;
            receiveCodeInput.Enabled = CanUseTransfer && idle;
            Font inputFont = receiveCodeInput.TextLength > FileTransferInvite.ShortCodeLength ? bodyFont : pickupCodeFont;
            if (receiveCodeInput.Font != inputFont) receiveCodeInput.Font = inputFont;
            receiveUseCode.Enabled = CanUseTransfer;
            receiveUseCode.Primary = !busy || receiveOfferPending || background;
            receiveUseCode.Text = receiveOfferPending ? "选择保存位置"
                : background ? "后台接收"
                : busy ? "取消"
                : !idle ? "打开文件夹"
                : FileTransferInvite.TryRead(receiveCodeInput.Text, out _) ? "接收" : "粘贴取件码";
            receiveUseCode.Invalidate();
        }

        private void SubmitReceiveCode()
        {
            if (!CanUseTransfer || operation != null) return;
            if (!FileTransferInvite.TryRead(receiveCodeInput.Text, out _))
            {
                string clipboard;
                try { clipboard = Clipboard.GetText(TextDataFormat.UnicodeText); }
                catch (ExternalException)
                {
                    status.Text = "剪贴板正忙，请稍后再试。";
                    return;
                }
                if (!FileTransferInvite.TryRead(clipboard, out var code))
                {
                    status.Text = string.IsNullOrWhiteSpace(clipboard) ? "请先复制对方发来的取件码。" : "剪贴板里的取件码不正确。";
                    return;
                }
                receiveCodeInput.Text = code;
            }
            ReceivePickupCode();
        }

        private Task<string> ChooseReceiveDestinationAsync(IncomingTransfer offer, CancellationToken token)
        {
            string extension = CleanTransferLabel(Path.GetExtension(offer.Name));
            string details = extension.Length > 1 && extension.Length <= 12
                ? extension.Substring(1).ToUpperInvariant() + "  ·  " + FormatBytes(offer.Length)
                : FormatBytes(offer.Length);
            return ChooseReceiveDestinationAsync(offer.Sender, offer.Name, details, null, token);
        }

        private Task<string> ChooseReceiveDestinationAsync(FileTransferBundle offer, CancellationToken token) =>
            ChooseReceiveDestinationAsync(offer.Sender, offer.Name, BundleCounts(offer) + "  ·  " + FormatBytes(offer.Length), offer, token);

        private async Task<string> ChooseReceiveDestinationAsync(string sender, string name, string details,
            FileTransferBundle bundle, CancellationToken token)
        {
            if (token.IsCancellationRequested) return null;
            var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiveDecision = decision;
            receiveDecisionToken = token;
            receiveOfferedFileName = name;
            receiveOfferedBundle = bundle;
            var safeSender = CleanTransferLabel(sender).Trim();
            receiveSender.Text = safeSender.Length == 0 ? "对方发来的内容" : "来自 " + safeSender;
            receiveOfferName.Text = CleanTransferLabel(name);
            receiveOfferSize.Text = details;
            receiveOfferFeedback.Text = "";
            try
            {
                using (token.Register(() => decision.TrySetResult(null)))
                {
                    UpdateButtons();
                    string destination = await decision.Task;
                    return token.IsCancellationRequested ? null : destination;
                }
            }
            finally
            {
                receiveDecision = null;
                receiveDecisionToken = default;
                receiveOfferedFileName = null;
                receiveOfferedBundle = null;
                if (!closing) UpdateButtons();
            }
        }

        private void ChooseReceiveFolder()
        {
            var decision = receiveDecision;
            var token = receiveDecisionToken;
            if (decision == null || decision.Task.IsCompleted || token.IsCancellationRequested) return;
            string folder;
            try { folder = TransferFolderPicker.Choose(this, TransferFolderPicker.DownloadsFolder(), token); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is COMException)
            {
                if (!closing && !token.IsCancellationRequested && receiveDecision == decision)
                    receiveOfferFeedback.Text = "无法打开文件夹选择器，请重试。";
                return;
            }
            if (folder != null && !token.IsCancellationRequested && receiveDecision == decision)
                ConfirmReceiveFolder(folder);
        }

        private void ConfirmReceiveFolder(string folder)
        {
            var decision = receiveDecision;
            if (decision == null || decision.Task.IsCompleted || receiveDecisionToken.IsCancellationRequested) return;
            if (!Directory.Exists(folder))
            {
                receiveOfferFeedback.Text = "此文件夹不可用，请选择其他位置。";
                return;
            }
            if (receiveOfferedBundle != null)
            {
                try { receiveOfferedBundle.ValidateDestination(folder); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                {
                    receiveOfferFeedback.Text = error.Message;
                    return;
                }
                decision.TrySetResult(folder);
                return;
            }
            var destination = Path.Combine(folder, receiveOfferedFileName);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                receiveOfferFeedback.Text = "已有同名文件或文件夹，请选择其他位置。";
                return;
            }
            decision.TrySetResult(destination);
        }

        private static string CleanTransferLabel(string text) => new string((text ?? "").Where(character =>
            !char.IsControl(character) && char.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray());
    }
}
