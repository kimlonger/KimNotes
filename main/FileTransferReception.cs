using KimNotes.utils;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    // Owned by the application and called from its UI thread.
    internal sealed class FileTransferReception : IDisposable
    {
        private readonly CloudSync cloud;
        private readonly Func<IFileTransferConnection, IncomingTransfer, CancellationToken, Task> present;
        private CancellationTokenSource listening;
        private CancellationToken observedSession;
        private bool started, suspended, disposed;
        private bool backgroundEnabled, toolActive, receivingFile;
        private string lastDirectFailure;

        internal bool Enabled { get; private set; } = true;
        internal bool IsSignedIn => !disposed && cloud?.CanUseAccountTransfer == true;
        internal CancellationToken TransferLifetime => IsSignedIn
            ? cloud.AccountTransferLifetime(cloud.Session) : new CancellationToken(true);
        internal bool CanWaitForFiles => backgroundEnabled || toolActive;
        internal bool LanReady { get; private set; }
        internal bool AccountReady { get; private set; }
        internal string LastDirectDiagnostic => IsSignedIn && CanWaitForFiles ? lastDirectFailure : null;
        internal event Action Changed;

        internal FileTransferReception(CloudSync cloud,
            Func<IFileTransferConnection, IncomingTransfer, CancellationToken, Task> present)
        {
            this.cloud = cloud;
            this.present = present;
            observedSession = TransferLifetime;
            if (cloud != null) cloud.Changed += AccountChanged;
        }

        internal void Start()
        {
            started = true;
            UpdateListening();
        }

        internal void SetEnabled(bool enabled)
        {
            if (Enabled == enabled || disposed) return;
            Enabled = enabled;
            if (!enabled) lastDirectFailure = null;
            UpdateListening();
            Changed?.Invoke();
        }

        internal void SetSuspended(bool value)
        {
            if (suspended == value || disposed) return;
            suspended = value;
            UpdateListening();
        }

        internal void SetBackgroundEnabled(bool enabled)
        {
            if (backgroundEnabled == enabled || disposed) return;
            backgroundEnabled = enabled;
            UpdateListening();
            Changed?.Invoke();
        }

        internal void SetToolActive(bool active)
        {
            if (toolActive == active || disposed) return;
            toolActive = active;
            UpdateListening();
            Changed?.Invoke();
        }

        private void UpdateListening()
        {
            if (!started || !IsSignedIn || !Enabled || suspended || (!CanWaitForFiles && !receivingFile))
            {
                listening?.Cancel();
                return;
            }
            if (listening != null) return;
            listening = CancellationTokenSource.CreateLinkedTokenSource(TransferLifetime);
            _ = ListenAsync(listening);
        }

        private async Task ListenAsync(CancellationTokenSource current)
        {
            try
            {
                using (var lanStop = CancellationTokenSource.CreateLinkedTokenSource(current.Token))
                using (var accountStop = CancellationTokenSource.CreateLinkedTokenSource(current.Token))
                {
                    var lan = WaitForLanAsync(lanStop.Token);
                    var account = WaitForAccountAsync(accountStop.Token);
                    var winner = await Task.WhenAny(lan, account);
                    try
                    {
                        using (var accepted = await winner)
                        {
                            var offer = await FileTransferProtocol.ReadOfferAsync(accepted, current.Token);
                            current.Token.ThrowIfCancellationRequested();
                            if (winner == lan) accountStop.Cancel(); else lanStop.Cancel();
                            receivingFile = true;
                            UpdateDirectDiagnostic(null);
                            await present(accepted, offer, current.Token);
                        }
                    }
                    finally
                    {
                        if (winner != lan) { lanStop.Cancel(); await CloseUnselectedAsync(lan); }
                        if (winner != account) { accountStop.Cancel(); await CloseUnselectedAsync(account); }
                    }
                }
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception error) when (!(error is OutOfMemoryException)) { Debug.WriteLine(error); }
            finally
            {
                receivingFile = false;
                listening = null;
                current.Dispose();
                UpdateListening();
            }
        }

        private async Task<IFileTransferConnection> WaitForLanAsync(CancellationToken token)
        {
            int delay = 2;
            while (true)
            {
                try
                {
                    using (var receiver = await Task.Run(() => LanTransferReceiver.Start(), token))
                    {
                        token.ThrowIfCancellationRequested();
                        delay = 2;
                        SetLanReady(true);
                        return await receiver.AcceptAsync(token);
                    }
                }
                catch (TimeoutException) when (!token.IsCancellationRequested) { continue; }
                catch (Exception error) when (!token.IsCancellationRequested && !(error is OutOfMemoryException))
                { Debug.WriteLine(error); }
                finally { SetLanReady(false); }
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
                delay = Math.Min(delay * 2, 60);
            }
        }

        private async Task<IFileTransferConnection> WaitForAccountAsync(CancellationToken token)
        {
            int delay = 5;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string sessionId = cloud.Session?.SessionId;
                try
                {
                    return await FileTransferAccount.AcceptAsync(null, token, () =>
                    {
                        if (!token.IsCancellationRequested) SetAccountReady(true);
                    });
                }
                catch (FileTransferDirectTimeoutException error) when (!token.IsCancellationRequested)
                {
                    if (IsSignedIn && cloud.Session?.SessionId == sessionId)
                        UpdateDirectDiagnostic(error.Diagnostic);
                    delay = 5;
                    continue;
                }
                catch (TimeoutException) when (!token.IsCancellationRequested && AccountReady)
                {
                    delay = 5;
                    continue;
                }
                catch (Exception error) when (!token.IsCancellationRequested && !(error is OutOfMemoryException))
                { Debug.WriteLine(error); }
                finally { SetAccountReady(false); }
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
                delay = Math.Min(delay * 2, 120);
            }
        }

        private static async Task CloseUnselectedAsync(Task<IFileTransferConnection> pending)
        {
            try { (await pending).Dispose(); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (!(error is OutOfMemoryException)) { Debug.WriteLine(error); }
        }

        private void SetLanReady(bool value)
        {
            if (LanReady == value) return;
            LanReady = value;
            Changed?.Invoke();
        }

        private void SetAccountReady(bool value)
        {
            if (AccountReady == value) return;
            AccountReady = value;
            Changed?.Invoke();
        }

        private void AccountChanged()
        {
            var session = TransferLifetime;
            if (observedSession == session) return;
            observedSession = session;
            lastDirectFailure = null;
            UpdateListening();
            Changed?.Invoke();
        }

        private void UpdateDirectDiagnostic(string diagnostic)
        {
            if (lastDirectFailure == diagnostic) return;
            lastDirectFailure = diagnostic;
            Changed?.Invoke();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (cloud != null) cloud.Changed -= AccountChanged;
            Changed = null;
            listening?.Cancel();
        }
    }
}
