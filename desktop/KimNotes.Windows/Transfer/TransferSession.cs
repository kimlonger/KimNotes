using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes.Windows.Transfer
{
    internal sealed class TransferSession : IDisposable
    {
        private readonly FileTransferReception reception;
        private readonly Func<TransferOffer, CancellationToken, Task<string>> confirm;
        private readonly CancellationTokenSource lifetime;
        private CancellationTokenSource operation;
        private LegacyPickup legacy;
        private string lastLegacyCode;
        private bool disposed;

        internal TransferSession(FileTransferReception reception,
            Func<TransferOffer, CancellationToken, Task<string>> confirm)
        {
            this.reception = reception;
            this.confirm = confirm;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(reception.TransferLifetime);
        }

        internal CancellationToken Token => lifetime.Token;
        internal bool HasEnded => disposed || lifetime.IsCancellationRequested;
        internal bool Busy => operation != null;
        internal bool HasLegacyConnection => legacy?.Connection != null;
        internal bool IsReceiving { get; private set; }
        internal bool InProgress { get; private set; }
        internal bool Failed { get; private set; }
        internal bool Declined { get; private set; }
        internal bool Succeeded { get; private set; }
        internal string Status { get; private set; }
        internal string FileLabel { get; private set; }
        internal string Code { get; private set; }
        internal string SavedFolder { get; private set; }
        internal string Route { get; private set; }
        internal string Diagnostic { get; private set; }
        internal long Length { get; private set; }
        internal long Moved { get; private set; }

        internal event Action Changed;
        internal event Action Finished;

        internal Task SendAsync(string[] paths)
        {
            if (paths == null || paths.Length == 0 || legacy != null) return Task.CompletedTask;
            return RunAsync(false, "正在准备文件…", async token =>
            {
                var bundle = await Task.Run(() => FileTransferBundle.Create(paths, token), token);
                FileLabel = TransferOffer.Clean(bundle.Name);
                Length = bundle.Length;
                var offerId = Guid.NewGuid();
                using (var pickup = new FileTransferPickup())
                {
                    try
                    {
                        var registration = await pickup.RegisterAsync(offerId, token);
                        Code = registration.Code;
                        SetStatus("请把取件码发给接收方。");
                        string key = await pickup.WaitForReceiverAsync(offerId, registration.ExpiresIn, token);
                        Code = null;
                        SetStatus("正在连接接收方…");
                        using (var wire = await FileTransferP2p.ConnectAsync(key, token))
                        using (var connection = await EncryptedFileTransfer.OpenAsync(wire, key, true, token))
                        {
                            Route = FileTransferP2p.TransferRoute(wire);
                            SetStatus("等待对方确认接收。");
                            Succeeded = await FileTransferBundle.SendAsync(connection, bundle, Feedback(), token);
                            Declined = !Succeeded;
                            SetStatus(Succeeded ? "文件已传输。" : "对方没有接收。");
                        }
                    }
                    finally
                    {
                        Code = null;
                        await pickup.CancelAsync(offerId);
                    }
                }
            });
        }

        internal async Task TakeAsync(string input)
        {
            if (Busy || disposed) return;
            if (!FileTransferInvite.TryRead(input, out var code))
            {
                SetStatus("请输入或粘贴有效的取件码。");
                return;
            }
            if (legacy != null)
            {
                SetStatus("请先断开当前取件连接。");
                return;
            }

            if (FileTransferInvite.IsShortCode(code))
                await RunAsync(true, "正在连接发送方…", token => TakeBundleAsync(code, token));
            else if (lastLegacyCode == code)
                SetStatus("此取件码已使用，请获取新的取件码。");
            else
                await ConnectLegacyAsync(code);
        }

        private async Task TakeBundleAsync(string code, CancellationToken token)
        {
            using (var pickup = new FileTransferPickup())
            {
                string key = await pickup.ClaimAsync(code, token);
                using (var wire = await FileTransferP2p.AcceptAsync(key, token))
                using (var connection = await EncryptedFileTransfer.OpenAsync(wire, key, false, token))
                {
                    Route = FileTransferP2p.TransferRoute(wire);
                    var offer = await FileTransferBundle.ReadOfferAsync(connection, token);
                    SetOffer(offer.Name, offer.Length);
                    var request = new TransferOffer(offer.Sender, offer.Name, offer.Length, offer.ValidateDestination, offer);
                    var target = await ConfirmAsync(request, token);
                    if (target.Folder != null) BeginTransfer();
                    SavedFolder = await FileTransferBundle.ReceiveAsync(connection, offer, target.Folder, Feedback(), token);
                    FinishReceive(SavedFolder != null, target.TimedOut);
                }
            }
        }

        internal Task ReceiveIncomingAsync(IFileTransferConnection connection, IncomingTransfer offer, CancellationToken token)
        {
            if (Busy || disposed || !reception.IsSignedIn || token.IsCancellationRequested)
                return FileTransferProtocol.ReceiveAsync(connection, offer, _ => null, null, token);
            return RunAsync(true, "请确认接收文件。", current => ReceiveSingleAsync(connection, offer, current), false, token);
        }

        private async Task ReceiveSingleAsync(IFileTransferConnection connection, IncomingTransfer offer, CancellationToken token)
        {
            SetOffer(offer.Name, offer.Length);
            var request = new TransferOffer(offer.Sender, offer.Name, offer.Length, folder =>
            {
                var path = Path.Combine(folder, offer.Name);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException("已有同名文件或文件夹，请选择其他位置。");
            });

            var target = await ConfirmAsync(request, token);
            if (target.Folder != null) BeginTransfer();
            bool saved = await FileTransferProtocol.ReceiveAsync(connection, offer,
                _ => target.Folder == null ? null : Path.Combine(target.Folder, offer.Name), Feedback(), token);
            if (saved) SavedFolder = target.Folder;
            FinishReceive(saved, target.TimedOut);
        }

        private async Task<(string Folder, bool TimedOut)> ConfirmAsync(TransferOffer offer, CancellationToken token)
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(FileTransferProtocol.OfferDecisionTimeoutSeconds - 5)))
            using (var selection = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                string folder = await confirm(offer, selection.Token);
                return (selection.IsCancellationRequested ? null : folder, deadline.IsCancellationRequested);
            }
        }

        private async Task ConnectLegacyAsync(string code)
        {
            var link = new LegacyPickup(lifetime.Token);
            legacy = link;
            bool connected = await RunAsync(true, "正在连接发送方…", async token =>
            {
                using (token.Register(link.Cancel))
                {
                    var wire = await FileTransferP2p.AcceptAsync(code, link.Token);
                    try
                    {
                        var channel = await EncryptedFileTransfer.OpenPickupAsync(wire, code, false, link.Token);
                        if (token.IsCancellationRequested)
                        {
                            channel.Connection.Dispose();
                            token.ThrowIfCancellationRequested();
                        }
                        link.Connection = channel.Connection;
                        link.CanReuse = channel.CanReuse;
                        link.Route = FileTransferP2p.TransferRoute(wire);
                        token.ThrowIfCancellationRequested();
                    }
                    catch
                    {
                        wire.Dispose();
                        throw;
                    }
                }
                SetStatus("取件已连接，等待对方发送。");
            });

            if (!connected || disposed || legacy != link)
            {
                EndLegacy(link);
                return;
            }

            lastLegacyCode = code;
            await ReceiveLegacyAsync(link);
        }

        private async Task ReceiveLegacyAsync(LegacyPickup link)
        {
            string ended = null;
            try
            {
                do
                {
                    var offer = await FileTransferProtocol.ReadOfferAsync(link.Connection, link.Token, link.CanReuse ? 120 : 30);
                    if (legacy != link || disposed) return;
                    if (Busy)
                    {
                        await FileTransferProtocol.ReceiveAsync(link.Connection, offer, _ => null, null, link.Token);
                        continue;
                    }
                    if (!await RunAsync(true, "请确认接收文件。", token =>
                    {
                        Route = link.Route;
                        return ReceiveSingleAsync(link.Connection, offer, token);
                    }, true, link.Token)) break;
                } while (link.CanReuse);
            }
            catch (Exception error) when (error is OperationCanceledException || error is ObjectDisposedException)
            {
                if (!link.Token.IsCancellationRequested) ended = "取件连接已断开。";
            }
            catch (TimeoutException) { ended = "取件连接已空闲超时。"; }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                System.Diagnostics.Debug.WriteLine(error);
                ended = "取件连接已断开。";
            }
            finally
            {
                if (EndLegacy(link) && ended != null && !Busy && !Succeeded) SetStatus(ended);
            }
        }

        internal void DisconnectLegacy()
        {
            if (EndLegacy(legacy)) SetStatus("已断开取件连接。");
        }

        private bool EndLegacy(LegacyPickup link)
        {
            if (link == null || legacy != link) return false;
            legacy = null;
            link.Dispose();
            if (!disposed)
            {
                reception.SetSuspended(false);
                Changed?.Invoke();
            }
            return true;
        }

        private async Task<bool> RunAsync(bool receive, string status, Func<CancellationToken, Task> action,
            bool suspendListener = true, CancellationToken external = default)
        {
            if (Busy || disposed || !reception.IsSignedIn || external.IsCancellationRequested) return false;
            using (var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, external))
            {
                operation = current;
                IsReceiving = receive;
                Failed = Declined = Succeeded = InProgress = false;
                Code = SavedFolder = FileLabel = Route = Diagnostic = null;
                Length = Moved = 0;
                if (suspendListener) reception.SetSuspended(true);
                SetStatus(status);
                try { await action(current.Token); return true; }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    Failed = !current.IsCancellationRequested;
                    Diagnostic = Failed ? (error as FileTransferDirectTimeoutException)?.Diagnostic : null;
                    if (Diagnostic != null) Program.LogError(new IOException(Diagnostic));
                    SetStatus(Failed ? error.Message : "已取消。");
                    return false;
                }
                finally
                {
                    operation = null;
                    InProgress = false;
                    if (!disposed)
                    {
                        if (suspendListener)
                            reception.SetSuspended(legacy != null && FileTransferP2p.HasFixedLocalPort);
                        Changed?.Invoke();
                        Finished?.Invoke();
                    }
                }
            }
        }

        private void SetOffer(string name, long length)
        {
            FileLabel = TransferOffer.Clean(name);
            Length = length;
            SetStatus("");
        }

        private void BeginTransfer()
        {
            InProgress = true;
            SetStatus("正在接收…");
        }

        private void FinishReceive(bool saved, bool timedOut)
        {
            Succeeded = saved;
            SetStatus(saved ? "文件已保存。" : timedOut ? "确认超时，请让对方重新发送。" : "已拒绝接收。");
        }

        private IProgress<long> Feedback()
        {
            var current = operation;
            return new Progress<long>(bytes =>
            {
                if (operation != current || current == null || current.IsCancellationRequested) return;
                InProgress = true;
                Moved = bytes;
                SetStatus((IsReceiving ? "接收中" : "发送中") + " · " + TransferOffer.Size(bytes) + " / " + TransferOffer.Size(Length));
            });
        }

        internal void SetStatus(string text)
        {
            Status = text ?? "";
            if (!disposed) Changed?.Invoke();
        }

        internal void SetPicking(bool active)
        {
            if (!disposed) reception.SetSuspended(active || (legacy != null && FileTransferP2p.HasFixedLocalPort));
        }

        internal void ResetResult()
        {
            if (Busy) return;
            Failed = Declined = Succeeded = false;
            Code = SavedFolder = FileLabel = Route = Diagnostic = null;
            SetStatus("");
        }

        internal void Cancel() => operation?.Cancel();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            EndLegacy(legacy);
            lifetime.Dispose();
        }

        private sealed class LegacyPickup : IDisposable
        {
            private readonly CancellationTokenSource stop;
            internal CancellationToken Token { get; }
            internal IFileTransferConnection Connection { get; set; }
            internal bool CanReuse { get; set; }
            internal string Route { get; set; }

            internal LegacyPickup(CancellationToken parent)
            {
                stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
                Token = stop.Token;
            }

            internal void Cancel() { if (!Token.IsCancellationRequested) stop.Cancel(); }
            public void Dispose()
            {
                Cancel();
                Connection?.Dispose();
                stop.Dispose();
            }
        }
    }

    internal sealed class TransferOffer
    {
        internal string Sender { get; }
        internal string Name { get; }
        internal string Details { get; }
        internal Action<string> ValidateFolder { get; }

        internal TransferOffer(string sender, string name, long length, Action<string> validateFolder, FileTransferBundle bundle = null)
        {
            Sender = Clean(sender);
            Name = Clean(name);
            Details = bundle == null ? Size(length) : $"{bundle.FileCount} 个文件 · {bundle.FolderCount} 个文件夹 · {Size(length)}";
            ValidateFolder = validateFolder;
        }

        internal static string Clean(string text) => new string((text ?? "").Where(character =>
            !char.IsControl(character) && char.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray());

        internal static string Size(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            return bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : $"{bytes / 1024d:0.##} KiB";
        }
    }
}
