using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using Microsoft.Win32;
using static KimNotes.Windows.Transfer.TransferAppearance;

namespace KimNotes.Windows.Transfer
{
    internal sealed class TransferWindow : Window
    {
        private readonly TransferSession session;
        private readonly Func<string> readClipboard;
        private readonly DispatcherTimer attention = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly StackPanel footer = new StackPanel();
        private readonly StackPanel readCode;
        private readonly StackPanel showCode;
        private readonly StackPanel showProgress;
        private readonly StackPanel showResult;
        private readonly StackPanel chooseDestination;
        private readonly Grid chooseFiles;
        private readonly TextBlock status;
        private readonly TextBlock generatedCode;
        private readonly TextBlock fileLabel;
        private readonly TextBlock progressLabel;
        private readonly TextBlock finalLabel;
        private readonly TextBlock savedPath;
        private readonly TextBlock offerSender;
        private readonly TextBlock offerName;
        private readonly TextBlock offerDetails;
        private readonly TextBlock offerError;
        private readonly TextBox codeInput;
        private readonly ProgressBar progress;
        private readonly Button sendTab;
        private readonly Button receiveTab;
        private TaskCompletionSource<string> decision;
        private CancellationToken decisionToken;
        private bool receiving, forceClose, picking;
        private bool statusForReceiving;
        private Button takeButton;
        private ActionLayout displayedActions;
        private bool displayedWithDiagnostics;

        private enum ActionLayout { None, Confirm, Cancel, PendingCode, Progress, SendResult, ReceiveResult, Disconnect, Send, Receive }

        internal bool IsListening => !forceClose && IsVisible && WindowState != WindowState.Minimized;
        internal bool CanShowIncoming => !forceClose && !picking;

        internal TransferWindow(TransferSession session) : this(session, Clipboard.GetText) { }

        internal TransferWindow(TransferSession session, Func<string> readClipboard)
        {
            this.session = session;
            this.readClipboard = readClipboard;
            Title = "文件互传";
            Width = 301;
            Height = 401;
            MinWidth = 292;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = InitConfig.ShowTaskbarIcons;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 12.5;
            _ = new NoteWindowChrome(this);
            DesktopCardTheme.Ensure();
            attention.Tick += (sender, args) => ReleaseAttention();
            Deactivated += (sender, args) => ReleaseAttention();

            var shell = new Grid();
            shell.SetResourceReference(Panel.BackgroundProperty, DesktopCardTheme.Surface);
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(31) });
            shell.RowDefinitions.Add(new RowDefinition());
            shell.SizeChanged += (sender, args) =>
                shell.Clip = new RectangleGeometry(new Rect(args.NewSize), 8, 8);
            Content = shell;
            var title = new DockPanel { LastChildFill = true };
            title.SetResourceReference(Panel.BackgroundProperty, DesktopCardTheme.Chrome);
            title.MouseLeftButtonDown += (sender, args) => DragMove();
            title.Children.Add(PlaceDocked(new TitleLogo { Width = 18, Height = 18, Margin = new Thickness(10, 0, 7, 0) }, Dock.Left));
            var close = TitleCloseButton.Create(Close);
            close.Margin = new Thickness(0, 0, 7, 0);
            close.SetResourceReference(ForegroundProperty, DesktopCardTheme.Icon);
            title.Children.Add(PlaceDocked(close, Dock.Right));
            title.Children.Add(Label(Title));
            shell.Children.Add(title);

            var frame = new DockPanel { Margin = new Thickness(14, 12, 14, 10), LastChildFill = true };
            Grid.SetRow(frame, 1);
            shell.Children.Add(frame);
            var tabs = new UniformGrid { Rows = 1, Columns = 2 };
            sendTab = ModeButton("发送", () => SetMode(false));
            receiveTab = ModeButton("接收", () => SetMode(true));
            sendTab.Margin = new Thickness(0, 0, 4, 0);
            receiveTab.Margin = new Thickness(4, 0, 0, 0);
            sendTab.PreviewKeyDown += OnTabArrow;
            receiveTab.PreviewKeyDown += OnTabArrow;
            tabs.Children.Add(sendTab);
            tabs.Children.Add(receiveTab);
            frame.Children.Add(PlaceDocked(tabs, Dock.Top));
            status = Label("", 12);
            status.Margin = new Thickness(3, 0, 3, 4);
            footer.Children.Add(status);
            frame.Children.Add(PlaceDocked(footer, Dock.Bottom));
            var body = new Grid();
            frame.Children.Add(body);

            var choose = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
            var dropGlyph = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8), Child = Glyph("M12,16 V3 M7,8 L12,3 17,8 M4,17 V21 H20 V17", 18)
            };
            dropGlyph.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Tint);
            choose.Children.Add(dropGlyph);
            choose.Children.Add(Label("拖入文件或文件夹", 14, bold: true, centered: true));
            var folderLink = Label("", 11.5, centered: true);
            folderLink.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
            folderLink.Inlines.Add(new Run("或 "));
            folderLink.Inlines.Add(InlineAction("选择文件夹", () => ChooseFiles(true)));
            choose.Children.Add(folderLink);
            chooseFiles = DropCard(choose);
            chooseFiles.Margin = new Thickness(0, 10, 0, 10);
            chooseFiles.AllowDrop = true;
            chooseFiles.DragOver += OnDragOver;
            chooseFiles.Drop += OnDrop;
            body.Children.Add(chooseFiles);

            readCode = ContentGroup(body);
            readCode.Children.Add(Label("输入对方给你的取件码", 13, centered: true));
            codeInput = new TextBox
            {
                Margin = new Thickness(3, 11, 3, 0), Padding = new Thickness(7, 5, 7, 5),
                FontFamily = new FontFamily("Consolas"), FontSize = 20, TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 40, MaxLength = 2048
            };
            StyleCodeInput(codeInput);
            AutomationProperties.SetName(codeInput, $"输入 {FileTransferInvite.ShortCodeLength} 位取件码");
            codeInput.ContextMenu = CreateCodeContextMenu();
            codeInput.TextChanged += (sender, args) => UpdateActions();
            codeInput.KeyDown += (sender, args) =>
            {
                if (args.Key != Key.Enter) return;
                args.Handled = true;
                StartTake();
            };
            readCode.Children.Add(codeInput);

            showCode = ContentGroup(body);
            fileLabel = Label("", 13, bold: true, centered: true);
            showCode.Children.Add(fileLabel);
            showCode.Children.Add(Label("取件码", 12, centered: true));
            generatedCode = Label("", 29, bold: true, centered: true);
            generatedCode.FontFamily = new FontFamily("Consolas");
            showCode.Children.Add(generatedCode);
            showCode.Children.Add(Label("5 分钟内有效，使用一次后失效", 11.5, centered: true));

            chooseDestination = ContentGroup(body);
            chooseDestination.Children.Add(Label("确认接收", 18, bold: true));
            offerSender = Label("");
            chooseDestination.Children.Add(offerSender);
            var offer = new StackPanel();
            offerName = Label("", 13, bold: true);
            offerDetails = Label("");
            offer.Children.Add(offerName);
            offer.Children.Add(offerDetails);
            chooseDestination.Children.Add(Card(offer));
            offerError = Label("", 11.5);
            chooseDestination.Children.Add(offerError);

            showProgress = ContentGroup(body);
            progressLabel = Label("", 16, bold: true, centered: true);
            showProgress.Children.Add(progressLabel);
            progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 7, Margin = new Thickness(1, 8, 1, 7) };
            progress.SetResourceReference(ForegroundProperty, DesktopCardTheme.Accent);
            showProgress.Children.Add(progress);

            showResult = ContentGroup(body);
            showResult.Children.Add(Glyph("M4,12 L9,17 20,6", 39));
            finalLabel = Label("", 16, bold: true, centered: true);
            savedPath = Label("", 11.5, centered: true);
            showResult.Children.Add(finalLabel);
            showResult.Children.Add(savedPath);
            session.Changed += SessionChanged;
            Closed += (sender, args) =>
            {
                attention.Stop();
                session.Changed -= SessionChanged;
            };
            Render();
        }

        internal void Present()
        {
            if (forceClose) return;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Topmost = true;
            if (!IsVisible) Show();
            Activate();
            attention.Stop();
            attention.Start();
        }

        private void ReleaseAttention()
        {
            attention.Stop();
            Topmost = false;
        }

        private void OnTabArrow(object sender, KeyEventArgs args)
        {
            if (Keyboard.Modifiers != ModifierKeys.None ||
                (args.Key != Key.Left && args.Key != Key.Right) || !sendTab.IsEnabled) return;
            bool selectReceive = args.Key == Key.Right;
            SetMode(selectReceive);
            (selectReceive ? receiveTab : sendTab).Focus();
            args.Handled = true;
        }

        internal void CloseForSession()
        {
            forceClose = true;
            decision?.TrySetResult(null);
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (ShouldHideOnClose)
            {
                e.Cancel = true;
                // Let the active connection deliver the refusal before it is released.
                decision?.TrySetResult(null);
                Hide();
            }
            base.OnClosing(e);
        }

        private bool ShouldHideOnClose => !forceClose && (session.Busy || session.HasLegacyConnection);

        internal async Task<string> ConfirmAsync(TransferOffer offer, CancellationToken token)
        {
            if (forceClose || token.IsCancellationRequested) return null;
            receiving = true;
            var current = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            decision = current;
            decisionToken = token;
            offerSender.Text = string.IsNullOrWhiteSpace(offer.Sender) ? "对方发来的内容" : "来自 " + offer.Sender;
            offerName.Text = offer.Name;
            offerDetails.Text = offer.Details;
            offerError.Text = "";
            try
            {
                OfferValidator = offer.ValidateFolder;
                Render();
                Present();
                using (token.Register(() => current.TrySetResult(null)))
                    return await current.Task;
            }
            finally
            {
                decision = null;
                OfferValidator = null;
                if (!forceClose) Render();
            }
        }

        private Action<string> OfferValidator { get; set; }

        private void ConfirmFolder()
        {
            if (decision == null || decision.Task.IsCompleted || picking) return;
            string folder;
            picking = true;
            try
            {
                folder = TransferFolderDialog.Choose(this, TransferFolderDialog.DownloadsFolder(), decisionToken);
                if (folder == null || decisionToken.IsCancellationRequested || decision == null) return;
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("请选一个可用的保存位置。");
                OfferValidator?.Invoke(folder);
                decision.TrySetResult(folder);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is COMException || error is ArgumentException)
            {
                offerError.Text = error is COMException ? "无法打开文件夹选择器，请重试。" : error.Message;
            }
            finally { picking = false; }
        }

        private void SetMode(bool receive)
        {
            if (session.Busy || picking || session.HasLegacyConnection) return;
            receiving = receive;
            Render();
            if (receive) codeInput.Focus();
        }

        private ContextMenu CreateCodeContextMenu()
        {
            var menu = CardActionMenu.Create(codeInput, "取件码编辑操作", anchorBelow: false);
            foreach (var entry in new[]
            {
                (Label: "撤销", Command: ApplicationCommands.Undo),
                (Label: "重做", Command: ApplicationCommands.Redo),
                (Label: "剪切", Command: ApplicationCommands.Cut),
                (Label: "复制", Command: ApplicationCommands.Copy),
                (Label: "粘贴", Command: ApplicationCommands.Paste),
                (Label: "全选", Command: ApplicationCommands.SelectAll)
            })
            {
                var item = entry.Command == ApplicationCommands.Paste
                    ? CardActionMenu.Item(entry.Label, null, () => ApplicationCommands.Paste.Execute(null, codeInput))
                    : CardActionMenu.Item(entry.Label, null, entry.Command);
                // WPF queries the clipboard while evaluating its Paste command.
                // Delay that query until the user opens this menu.
                if (entry.Command == ApplicationCommands.Paste)
                    menu.Opened += (sender, args) => item.IsEnabled = ApplicationCommands.Paste.CanExecute(null, codeInput);
                item.CommandTarget = codeInput;
                menu.Items.Add(item);
            }
            return menu;
        }

        private bool CanReceiveFromCode => receiving && !forceClose && !session.Busy && !picking &&
            !session.HasLegacyConnection && decision == null && !IsResult;

        private void TakeOrPaste()
        {
            if (!CanReceiveFromCode) return;
            if (!string.IsNullOrWhiteSpace(codeInput.Text))
            {
                StartTake();
                return;
            }

            try
            {
                if (!FileTransferInvite.TryRead(readClipboard(), out var code))
                {
                    session.SetStatus("剪贴板里没有有效的取件码。");
                    return;
                }
                codeInput.SelectAll();
                codeInput.SelectedText = code;
                codeInput.CaretIndex = codeInput.Text.Length;
                session.SetStatus("");
            }
            catch (ExternalException) { session.SetStatus("剪贴板正忙，请再试一次。"); }
            finally { codeInput.Focus(); }
        }

        private void StartTake()
        {
            if (!CanReceiveFromCode || string.IsNullOrWhiteSpace(codeInput.Text)) return;
            _ = session.TakeAsync(codeInput.Text);
        }

        private void OnDragOver(object sender, DragEventArgs args)
        {
            args.Effects = !receiving && !picking && !session.Busy && args.Data.GetDataPresent(DataFormats.FileDrop, false)
                ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs args)
        {
            if (!receiving && !picking && !session.Busy &&
                args.Data.GetData(DataFormats.FileDrop, false) is string[] paths)
                _ = session.SendAsync(paths);
            args.Handled = true;
        }

        private void ChooseFiles(bool folder)
        {
            if (session.Busy || picking) return;
            string[] selection = null;
            picking = true;
            session.SetPicking(true);
            try
            {
                if (folder)
                {
                    string result = TransferFolderDialog.Choose(this, null, session.Token, sending: true);
                    if (result != null) selection = new[] { result };
                }
                else
                {
                    var dialog = new OpenFileDialog { Title = "选择要发送的文件", CheckFileExists = true, Multiselect = true };
                    if (dialog.ShowDialog(this) == true) selection = dialog.FileNames;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is COMException)
            {
                session.SetStatus("无法打开选择窗口，请重试。");
            }
            finally
            {
                picking = false;
                session.SetPicking(false);
            }
            if (!forceClose && selection != null) _ = session.SendAsync(selection);
        }

        private void Copy(string text, string message)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); session.SetStatus(message); }
            catch (ExternalException) { session.SetStatus("剪贴板正忙，请再试一次。"); }
        }

        internal static bool OpenDirectory(string path)
        {
            if (!Directory.Exists(path)) return false;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return true; }
            catch (Win32Exception) { return false; }
        }

        private bool IsResult => !session.Busy && session.Succeeded && receiving == session.IsReceiving;

        private void SessionChanged()
        {
            statusForReceiving = session.Busy ? session.IsReceiving : receiving;
            Render();
        }

        private void Render()
        {
            if (forceClose) return;
            if (session.Busy) receiving = session.IsReceiving;
            SetModeAppearance(sendTab, !receiving);
            SetModeAppearance(receiveTab, receiving);
            sendTab.IsEnabled = receiveTab.IsEnabled = !session.Busy && !session.HasLegacyConnection;
            chooseFiles.Visibility = VisibilityFor(!session.Busy && !receiving && !IsResult);
            readCode.Visibility = VisibilityFor(!session.Busy && receiving && !IsResult && !session.HasLegacyConnection);
            showCode.Visibility = VisibilityFor(!receiving && session.Code != null);
            chooseDestination.Visibility = VisibilityFor(decision != null);
            showProgress.Visibility = VisibilityFor(session.Busy && session.InProgress && decision == null);
            showResult.Visibility = VisibilityFor(IsResult);
            fileLabel.Text = session.FileLabel;
            generatedCode.Text = session.Code;
            progressLabel.Text = session.Route == null ? session.FileLabel : session.FileLabel + " · " + session.Route;
            progress.Value = session.Length <= 0 ? 0 : Math.Min(100d, session.Moved * 100d / session.Length);
            finalLabel.Text = receiving ? "文件已接收" : "文件已传输";
            savedPath.Text = receiving ? session.SavedFolder : session.FileLabel;
            status.Text = session.Status;
            status.Visibility = VisibilityFor(receiving == statusForReceiving && !string.IsNullOrEmpty(session.Status));
            if (IsResult && receiving && codeInput.Text.Length != 0) codeInput.Clear();
            UpdateActions();
        }

        private void UpdateActions()
        {
            var actions = GetActionLayout();
            bool withDiagnostics = (actions == ActionLayout.Receive || actions == ActionLayout.Send) &&
                receiving == session.IsReceiving && session.Diagnostic != null;
            if (actions != displayedActions || withDiagnostics != displayedWithDiagnostics)
            {
                displayedActions = actions;
                displayedWithDiagnostics = withDiagnostics;
                if (footer.Children.Count > 1) footer.Children.RemoveAt(1);
                footer.Children.Add(BuildActions(actions, withDiagnostics));
            }
            if (takeButton != null) takeButton.Content = string.IsNullOrWhiteSpace(codeInput.Text) ? "粘贴取件码" : "接收文件";
        }

        private ActionLayout GetActionLayout()
        {
            if (decision != null) return ActionLayout.Confirm;
            if (session.Busy)
            {
                if (session.Code != null) return ActionLayout.PendingCode;
                return session.InProgress ? ActionLayout.Progress : ActionLayout.Cancel;
            }
            if (IsResult) return receiving ? ActionLayout.ReceiveResult : ActionLayout.SendResult;
            if (session.HasLegacyConnection) return ActionLayout.Disconnect;
            return receiving ? ActionLayout.Receive : ActionLayout.Send;
        }

        private FrameworkElement BuildActions(ActionLayout actions, bool withDiagnostics)
        {
            var buttons = new UniformGrid { Rows = 1 };
            takeButton = null;

            switch (actions)
            {
                case ActionLayout.Confirm:
                    buttons.Children.Add(ActionButton("拒绝", () => decision?.TrySetResult(null)));
                    buttons.Children.Add(ActionButton("选择保存位置", ConfirmFolder, true));
                    break;
                case ActionLayout.Cancel:
                case ActionLayout.Progress:
                case ActionLayout.PendingCode:
                    buttons.Children.Add(ActionButton("取消", session.Cancel));
                    buttons.Children.Add(ActionButton(receiving ? "后台接收" : "后台传输", Hide, actions != ActionLayout.PendingCode));
                    if (actions == ActionLayout.PendingCode)
                        buttons.Children.Add(ActionButton("复制取件码", () => Copy(session.Code, "取件码已复制。"), true));
                    break;
                case ActionLayout.ReceiveResult:
                    buttons.Children.Add(ActionButton("完成", session.ResetResult));
                    buttons.Children.Add(ActionButton("打开文件夹", () =>
                    {
                        if (!OpenDirectory(session.SavedFolder)) session.SetStatus("保存文件夹已不可用。");
                    }, true));
                    break;
                case ActionLayout.SendResult:
                    buttons.Children.Add(ActionButton("完成", session.ResetResult, true));
                    break;
                case ActionLayout.Disconnect:
                    buttons.Children.Add(ActionButton("断开连接", session.DisconnectLegacy));
                    buttons.Children.Add(ActionButton("后台接收", Hide, true));
                    break;
                case ActionLayout.Receive:
                    takeButton = ActionButton("粘贴取件码", TakeOrPaste, true);
                    takeButton.FontWeight = FontWeights.SemiBold;
                    buttons.Children.Add(takeButton);
                    break;
                case ActionLayout.Send:
                    var selectFile = ActionButton("选择文件", () => ChooseFiles(false), true);
                    selectFile.FontWeight = FontWeights.SemiBold;
                    buttons.Children.Add(selectFile);
                    break;
            }

            bool alignToFrame = buttons.Children.Count < 3;
            for (int index = 0; index < buttons.Children.Count; index++)
            {
                var button = (Button)buttons.Children[index];
                button.Height = 36;
                button.Margin = new Thickness(alignToFrame && index == 0 ? 0 : 4, 0,
                    alignToFrame && index + 1 == buttons.Children.Count ? 0 : 4, 0);
            }
            if (actions != ActionLayout.Send && actions != ActionLayout.Receive) return buttons;

            var idleFooter = new StackPanel();
            if (withDiagnostics)
            {
                var diagnostic = Label("", 11.5);
                diagnostic.HorizontalAlignment = HorizontalAlignment.Right;
                diagnostic.Inlines.Add(InlineAction("复制诊断", () => Copy(session.Diagnostic, "连接诊断已复制。")));
                idleFooter.Children.Add(diagnostic);
            }
            idleFooter.Children.Add(buttons);
            var hint = PickupHint($"对方输入 {FileTransferInvite.ShortCodeLength} 位取件码即可接收，文件加密传输");
            // Keep the same space below the main action when switching modes.
            hint.Visibility = actions == ActionLayout.Send ? Visibility.Visible : Visibility.Hidden;
            idleFooter.Children.Add(hint);
            return idleFooter;
        }
    }
}
