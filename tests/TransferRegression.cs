using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KimNotes;
using KimNotes.utils;
using KimNotes.Windows.Transfer;

internal static class TransferRegression
{
    private sealed class Wire : IFileTransferConnection
    {
        internal readonly BlockingCollection<byte[]> Incoming = new BlockingCollection<byte[]>();
        internal Action<byte[]> OnWrite;

        public Task<byte[]> ReadAsync(CancellationToken token)
        {
            return Task.Run(() => Incoming.Take(token), token);
        }

        public Task WriteAsync(byte[] data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OnWrite(data);
            return Task.FromResult(true);
        }

        public void Dispose() { Incoming.Dispose(); }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    private static async Task ReceiveWithDroppedReceipt(string root)
    {
        string target = Path.Combine(root, "received.bin");
        byte[] bytes = { 17, 29, 41 };
        int writes = 0;
        using (var wire = new Wire())
        using (var hash = SHA256.Create())
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            wire.Incoming.Add(bytes);
            wire.Incoming.Add(hash.ComputeHash(bytes));
            wire.OnWrite = frame =>
            {
                writes++;
                if (File.Exists(target)) throw new EndOfStreamException("Peer closed after publication.");
            };
            var offer = new IncomingTransfer("received.bin", "test", bytes.Length);
            bool saved = await FileTransferProtocol.ReceiveAsync(wire, offer, item => target, new Progress<long>(), stop.Token);
            Check(saved && writes == 3 && File.ReadAllBytes(target).SequenceEqual(bytes), "Saved file or final receipt missing.");
        }
    }

    private static async Task BundleRoundTrip(string root)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "empty-dir"));
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), new byte[0]);
        var bytes = new byte[1024 * 1024 + 19];
        new Random(8491).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(source, "large.bin"), bytes);
        using (var send = new Wire())
        using (var receive = new Wire())
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            send.OnWrite = receive.Incoming.Add;
            receive.OnWrite = send.Incoming.Add;
            var sending = FileTransferBundle.SendAsync(send, FileTransferBundle.Create(new[] { source }), null, stop.Token);
            var offer = await FileTransferBundle.ReadOfferAsync(receive, stop.Token);
            var saved = await FileTransferBundle.ReceiveAsync(receive, offer, destination, null, stop.Token);
            Check(await sending && saved == Path.Combine(destination, "source"), "Bundle completion was not acknowledged by both peers.");
            Check(File.ReadAllBytes(Path.Combine(saved, "large.bin")).SequenceEqual(bytes) &&
                new FileInfo(Path.Combine(saved, "empty.bin")).Length == 0 && Directory.Exists(Path.Combine(saved, "empty-dir")),
                "Bundle content changed in transit.");
            Check(!Directory.EnumerateFileSystemEntries(destination, "*.part").Any(), "Temporary transfer data was left behind.");
        }
    }

    private static async Task CancelAndRetry(string root)
    {
        string source = Path.Combine(root, "retry.bin");
        string destination = Directory.CreateDirectory(Path.Combine(root, "retry")).FullName;
        byte[] bytes = { 7, 9, 12 };
        File.WriteAllBytes(source, bytes);
        var bundle = FileTransferBundle.Create(new[] { source });
        foreach (bool cancel in new[] { true, false })
        {
            using (var send = new Wire())
            using (var receive = new Wire())
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                send.OnWrite = frame =>
                {
                    receive.Incoming.Add(frame);
                    if (cancel && frame.SequenceEqual(bytes)) stop.Cancel();
                };
                receive.OnWrite = send.Incoming.Add;
                var sending = FileTransferBundle.SendAsync(send, bundle, null, stop.Token);
                var offer = await FileTransferBundle.ReadOfferAsync(receive, stop.Token);
                var receiving = FileTransferBundle.ReceiveAsync(receive, offer, destination, null, stop.Token);
                try { await Task.WhenAll(sending, receiving); }
                catch (OperationCanceledException) { if (!cancel) throw; }
                if (cancel)
                    Check(sending.IsCanceled && receiving.IsCanceled && !Directory.EnumerateFileSystemEntries(destination).Any(),
                        "Cancelling before publication left a transfer or partial file behind.");
                else
                    Check(sending.Result && receiving.Result == destination &&
                        File.ReadAllBytes(Path.Combine(destination, "retry.bin")).SequenceEqual(bytes),
                        "Retry after cancellation did not save the original bytes.");
            }
        }
    }

    private static void ClosePendingOffer()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var reception = new FileTransferReception(null, null))
        using (var session = new TransferSession(reception, null))
        using (var operation = new CancellationTokenSource())
        {
            var window = new TransferWindow(session, () => "");
            var choice = new TaskCompletionSource<string>();
            typeof(TransferWindow).GetField("decision", flags).SetValue(window, choice);
            typeof(TransferSession).GetField("operation", flags).SetValue(session, operation);
            var args = new CancelEventArgs();
            try
            {
                typeof(TransferWindow).GetMethod("OnClosing", flags).Invoke(window, new object[] { args });
                Check(args.Cancel && choice.Task.IsCompleted && choice.Task.Result == null && !operation.IsCancellationRequested,
                    "Closing an incoming offer tore down the connection before it could send a refusal.");
            }
            finally { window.CloseForSession(); }
        }
    }

    private sealed class ImmediatePosts : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object state) { callback(state); }
    }

    private static void LateProgressAfterSuccess()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var reception = new FileTransferReception(null, null))
        using (var session = new TransferSession(reception, null))
        using (var operation = new CancellationTokenSource())
        {
            var window = new TransferWindow(session, () => "");
            var type = typeof(TransferSession);
            var previous = SynchronizationContext.Current;
            try
            {
                type.GetField("operation", flags).SetValue(session, operation);
                SynchronizationContext.SetSynchronizationContext(new ImmediatePosts());
                var queued = (IProgress<long>)type.GetMethod("Feedback", flags).Invoke(session, null);
                queued.Report(1);
                type.GetProperty("Succeeded", flags).SetValue(session, true);
                session.SetStatus("done");
                queued.Report(2);
                var progress = (FrameworkElement)typeof(TransferWindow).GetField("showProgress", flags).GetValue(window);
                var result = (FrameworkElement)typeof(TransferWindow).GetField("showResult", flags).GetValue(window);
                Check(session.Status == "done", "A queued progress callback replaced the final status.");
                Check(progress.Visibility != Visibility.Visible || result.Visibility != Visibility.Visible,
                    "Progress and completion overlap while the pickup registration is closing.");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                window.CloseForSession();
            }
        }
    }

    private static void TabFeedback()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using (var reception = new FileTransferReception(null, null))
        using (var session = new TransferSession(reception, null))
        {
            var window = new TransferWindow(session, () => "invalid");
            var mode = typeof(TransferWindow).GetMethod("SetMode", flags);
            var label = (TextBlock)typeof(TransferWindow).GetField("status", flags).GetValue(window);
            try
            {
                mode.Invoke(window, new object[] { true });
                typeof(TransferWindow).GetMethod("TakeOrPaste", flags).Invoke(window, null);
                Check(label.Visibility == Visibility.Visible && label.Text.Contains("取件码"), "Missing pickup validation.");
                mode.Invoke(window, new object[] { false });
                Check(label.Visibility != Visibility.Visible, "The send page displays an error from the receive page.");
                mode.Invoke(window, new object[] { true });
                Check(label.Visibility == Visibility.Visible && label.Text.Contains("取件码"), "Original receive feedback lost.");
            }
            finally { window.CloseForSession(); }
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            throw new InvalidOperationException("Unexpected HTTP request in the transfer lifetime test.");
        }
    }

    private static void ExpiredReceiver()
    {
        InitConfig.InitSettings();
        using (var cloud = new CloudSync(new CloudApi(new OfflineHandler()), false))
        {
            var login = new CloudSession { UserId = Guid.NewGuid().ToString(), SessionId = Guid.NewGuid().ToString(), ProtocolVersion = 2 };
            typeof(CloudSync).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(cloud, login);
            using (var reception = new FileTransferReception(cloud, null))
            using (var wire = new Wire())
            {
                int prompts = 0, refusals = 0;
                Func<TransferOffer, CancellationToken, Task<string>> decline = (offer, token) =>
                {
                    prompts++;
                    return Task.FromResult<string>(null);
                };
                wire.OnWrite = frame => { if (frame.SequenceEqual(new byte[] { 0 })) refusals++; };
                var incoming = new IncomingTransfer("demo.txt", "receiver", 1);
                using (var expired = new TransferSession(reception, decline))
                {
                    InitConfig.SetConfigValue("checkBox3", "True");
                    cloud.ApplyPrivacyMode();
                    Check(expired.HasEnded && !reception.IsSignedIn, "Privacy mode did not end the receiver lifetime.");
                    InitConfig.SetConfigValue("checkBox3", "False");
                    cloud.ApplyPrivacyMode();
                    Check(reception.IsSignedIn, "Current account could not resume file transfers.");
                    expired.ReceiveIncomingAsync(wire, incoming, CancellationToken.None).GetAwaiter().GetResult();
                    Check(prompts == 0 && refusals == 1, "An expired transfer session consumed an offer without refusing it.");
                    using (var fresh = new TransferSession(reception, decline))
                        fresh.ReceiveIncomingAsync(wire, incoming, CancellationToken.None).GetAwaiter().GetResult();
                    Check(prompts == 1 && refusals == 2, "A fresh receiver could not handle the next offer.");
                }
            }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR", Path.Combine(args[0], "test-data"));
        int failures = 0;
        var checks = new Action[] {
            () => ReceiveWithDroppedReceipt(args[0]).GetAwaiter().GetResult(),
            () => BundleRoundTrip(args[0]).GetAwaiter().GetResult(),
            () => CancelAndRetry(args[0]).GetAwaiter().GetResult(),
            ClosePendingOffer,
            LateProgressAfterSuccess,
            TabFeedback,
            ExpiredReceiver
        };
        string[] names = { "legacy receipt loss after save", "bundle round trip across batch boundary", "cancel and retry a bundle", "dismiss pending offer", "late progress after success", "independent tab feedback", "receiver lifetime across privacy changes" };
        for (int i = 0; i < checks.Length; i++)
        {
            try { checks[i](); Console.WriteLine("PASS " + names[i]); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + names[i] + ": " + error.GetBaseException().Message); }
        }
        return failures == 0 ? 0 : 1;
    }
}
