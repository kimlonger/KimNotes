using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    internal static class FileTransferP2p
    {
        private const string DefaultServer = "note.kimlulu.com:43763";
        private const int CodeBytes = 32;
        private const int PeerWaitTimeoutMs = 120000;
        private const int AccountMatchTimeoutMs = 25000;

        internal static string CreateCode()
        {
            var code = new byte[CodeBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(code);
            return EncodeCode(code);
        }

        internal static Task<IFileTransferConnection> AcceptAsync(string code, CancellationToken token)
        {
            return OpenAsync(code, false, PeerWaitTimeoutMs, token);
        }

        internal static Task<IFileTransferConnection> ConnectAsync(string code, CancellationToken token)
        {
            return OpenAsync(code, true, AccountMatchTimeoutMs, token);
        }

        internal static Task<IFileTransferConnection> ConnectInvitationAsync(string code, CancellationToken token)
        {
            return OpenAsync(code, true, PeerWaitTimeoutMs, token);
        }

        private static async Task<IFileTransferConnection> OpenAsync(string code, bool sender, int matchTimeoutMs, CancellationToken token)
        {
            var secret = DecodeCode(code);
            string room;
            string connectionKey;
            try
            {
                using (var hash = SHA256.Create())
                    room = BitConverter.ToString(hash.ComputeHash(secret)).Replace("-", "").ToLowerInvariant();
                using (var auth = new HMACSHA256(secret))
                    connectionKey = EncodeCode(auth.ComputeHash(Encoding.ASCII.GetBytes("KimNotes file transfer UDP connect v1")));
            }
            finally { Array.Clear(secret, 0, secret.Length); }

            var server = await ResolveServerAsync(token).ConfigureAwait(false);
            var connection = new UdpConnection(server, room, connectionKey, sender, matchTimeoutMs);
            await connection.StartAsync(token).ConfigureAwait(false);
            return connection;
        }

        private static string EncodeCode(byte[] code)
        {
            return Convert.ToBase64String(code).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static byte[] DecodeCode(string code)
        {
            code = code?.Trim();
            if (code?.Length != 43)
                throw new ArgumentException("请输入另一台电脑生成的完整配对码。", nameof(code));

            byte[] bytes;
            try { bytes = Convert.FromBase64String(code.Replace('-', '+').Replace('_', '/') + "="); }
            catch (FormatException ex) { throw new ArgumentException("配对码格式不正确。", nameof(code), ex); }
            if (EncodeCode(bytes) != code)
            {
                Array.Clear(bytes, 0, bytes.Length);
                throw new ArgumentException("配对码格式不正确。", nameof(code));
            }
            return bytes;
        }

        private static async Task<IPEndPoint> ResolveServerAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var config = Environment.GetEnvironmentVariable("KIMNOTES_P2P_SERVER");
            var address = string.IsNullOrWhiteSpace(config) ? DefaultServer : config.Trim();
            int separator = address.LastIndexOf(':');
            if (separator <= 0 || !TryPort(address.Substring(separator + 1), out int port))
                throw new IOException("跨网配对服务器地址格式不正确。");

            var host = address.Substring(0, separator);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var lookup = Dns.GetHostAddressesAsync(host);
                    if (await Task.WhenAny(lookup, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false) != lookup)
                    {
                        _ = lookup.ContinueWith(task => { _ = task.Exception; },
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("连接跨网配对服务器超时。");
                    }
                    var addresses = await lookup.ConfigureAwait(false);
                    timeout.Cancel();
                    token.ThrowIfCancellationRequested();
                    var ipv4 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
                    if (ipv4 == null) throw new IOException("跨网配对服务器需要 IPv4 地址。");
                    return new IPEndPoint(ipv4, port);
                }
            }
            catch (SocketException ex)
            {
                throw new IOException("无法找到跨网配对服务器。", ex);
            }
        }

        private static bool TryPort(string value, out int port)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && port >= 1 && port <= 65535;
        }

        private sealed class UdpConnection : IFileTransferConnection
        {
            private static readonly byte[] PunchProbe = Encoding.ASCII.GetBytes("KNFT1 punch");
            private const int ReceiveQueueLimit = 64;
            private const int FramesInFlightLimit = 8;
            private const int DirectConnectTimeoutMs = 17000;
            private readonly object gate = new object();
            private readonly Queue<byte[]> incoming = new Queue<byte[]>();
            private readonly SemaphoreSlim receiveReady = new SemaphoreSlim(0);
            private readonly SemaphoreSlim writeOrder = new SemaphoreSlim(1);
            private readonly SemaphoreSlim pumpReady = new SemaphoreSlim(0, 1);
            private readonly TaskCompletionSource<bool> connected = NewSignal();
            private readonly Stopwatch clock = new Stopwatch();
            private readonly IPEndPoint server;
            private readonly string room;
            private readonly string connectionKey;
            private readonly bool sender;
            private readonly int matchTimeoutMs;
            private readonly NetManager manager;
            private byte[] registration;
            private IPEndPoint target;
            private NetPeer peer;
            private PendingFrame pendingWrite;
            private Exception failure;
            private Exception transportFailure;
            private int inFlight;
            private int closeRequested;
            private long nextRegistration;
            private long nextProbe;
            private long pairStart;
            private bool attemptedConnect;

            internal UdpConnection(IPEndPoint server, string room, string connectionKey, bool sender, int matchTimeoutMs)
            {
                this.server = server;
                this.room = room;
                this.connectionKey = connectionKey;
                this.sender = sender;
                this.matchTimeoutMs = matchTimeoutMs;
                registration = Registration("-");

                var listener = new EventBasedNetListener();
                manager = new NetManager(listener)
                {
                    IPv6Enabled = false,
                    UnconnectedMessagesEnabled = true,
                    AutoRecycle = true,
                    DisconnectTimeout = 15000,
                    ReconnectDelay = 300,
                    MaxConnectAttempts = 48
                };
                listener.NetworkReceiveUnconnectedEvent += OnRendezvousMessage;
                listener.ConnectionRequestEvent += OnConnectionRequest;
                listener.PeerConnectedEvent += OnPeerConnected;
                listener.PeerDisconnectedEvent += OnPeerDisconnected;
                listener.NetworkReceiveEvent += OnReceive;
                listener.DeliveryEvent += OnDelivered;
            }

            internal Task StartAsync(CancellationToken token)
            {
                _ = Task.Run(() => PumpAsync(token));
                return connected.Task;
            }

            private async Task PumpAsync(CancellationToken token)
            {
                Exception reason = new ObjectDisposedException(nameof(UdpConnection));
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!manager.StartInManualMode(0))
                        throw new IOException("无法打开跨网传输的 UDP 端口。");

                    clock.Start();
                    long last = 0;
                    long closeAt = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        manager.PollEvents(512);
                        if (transportFailure != null) throw transportFailure;
                        long now = clock.ElapsedMilliseconds;
                        manager.ManualUpdate(Math.Max(1, now - last));
                        last = now;

                        if (Volatile.Read(ref closeRequested) != 0)
                        {
                            // Let a final receipt reach the other device before closing the connection.
                            if (closeAt == 0) closeAt = now + 1800;
                            if (peer == null || inFlight == 0 || now >= closeAt) break;
                        }
                        else if (peer == null) TryConnect(now);
                        else SendPendingFrame();

                        await pumpReady.WaitAsync(8, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { reason = ex; }
                finally
                {
                    peer = null;
                    try { manager.Stop(); }
                    finally { Finish(reason); }
                }
            }

            private void TryConnect(long now)
            {
                if (target == null)
                {
                    if (now >= matchTimeoutMs)
                        throw new TimeoutException("跨网配对超时，请确认另一台电脑已打开文件互传。");
                    if (now >= nextRegistration)
                    {
                        manager.SendUnconnectedMessage(registration, server);
                        nextRegistration = now + 800;
                    }
                    return;
                }

                if (now - pairStart >= DirectConnectTimeoutMs)
                    throw new TimeoutException("当前网络无法建立直连。两台电脑连到同一局域网后，可使用局域网互传。");
                if (now >= nextProbe)
                {
                    manager.SendUnconnectedMessage(PunchProbe, target);
                    nextProbe = now + 200;
                }
                if (sender && !attemptedConnect && now - pairStart >= 250)
                {
                    manager.Connect(target, connectionKey);
                    attemptedConnect = true;
                }
            }

            private byte[] Registration(string cookie)
            {
                return Encoding.ASCII.GetBytes($"KNFT1 {(sender ? "s" : "r")} {room} {cookie}");
            }

            private void OnRendezvousMessage(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
            {
                if (target != null || !server.Equals(remote) || type != UnconnectedMessageType.BasicMessage
                    || reader.AvailableBytes > 200) return;

                var parts = Encoding.ASCII.GetString(reader.GetRemainingBytes()).Split(' ');
                if (parts.Length < 3 || parts[0] != "KNFT1") return;
                if (parts.Length == 3 && parts[1] == "challenge" && ValidCookie(parts[2]))
                {
                    registration = Registration(parts[2]);
                    nextRegistration = 0;
                    return;
                }
                if (parts.Length != 5 || parts[1] != "peer" || parts[2] != room
                    || !IPAddress.TryParse(parts[3], out var address) || address.AddressFamily != AddressFamily.InterNetwork
                    || !TryPort(parts[4], out var port)) return;

                var remotePeer = new IPEndPoint(address, port);
                if (server.Equals(remotePeer)) return;
                target = remotePeer;
                pairStart = clock.ElapsedMilliseconds;
            }

            private static bool ValidCookie(string value)
            {
                return value.Length >= 16 && value.Length <= 96
                    && value.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                        || (c >= '0' && c <= '9') || c == '-' || c == '_');
            }

            private void OnConnectionRequest(ConnectionRequest request)
            {
                if (!sender && peer == null && target != null && target.Equals(request.RemoteEndPoint))
                    request.AcceptIfKey(connectionKey);
                else
                    request.Reject();
            }

            private void OnPeerConnected(NetPeer remote)
            {
                if (peer != null || target == null || !target.Equals(remote))
                {
                    manager.DisconnectPeer(remote);
                    return;
                }
                peer = remote;
                connected.TrySetResult(true);
            }

            private void OnPeerDisconnected(NetPeer remote, DisconnectInfo info)
            {
                if (ReferenceEquals(remote, peer) && transportFailure == null)
                    transportFailure = new EndOfStreamException("另一台电脑已断开连接。");
            }

            private void OnReceive(NetPeer remote, NetPacketReader reader, byte channel, DeliveryMethod method)
            {
                if (!ReferenceEquals(remote, peer) || transportFailure != null) return;
                if (channel != 0 || method != DeliveryMethod.ReliableOrdered || reader.AvailableBytes <= 0
                    || reader.AvailableBytes > FileTransferProtocol.MaxFrameBytes)
                {
                    transportFailure = new IOException("收到无法识别的传输数据。");
                    return;
                }

                lock (gate)
                {
                    if (incoming.Count >= ReceiveQueueLimit)
                    {
                        transportFailure = new IOException("接收速度跟不上发送速度，传输已停止。");
                        return;
                    }
                    incoming.Enqueue(reader.GetRemainingBytes());
                    receiveReady.Release();
                }
            }

            private void OnDelivered(NetPeer remote, object frame)
            {
                if (ReferenceEquals(remote, peer) && frame is PendingFrame) inFlight--;
            }

            private void SendPendingFrame()
            {
                if (inFlight >= FramesInFlightLimit) return;
                lock (gate)
                {
                    var request = pendingWrite;
                    pendingWrite = null;
                    if (request == null || request.Ready.Task.IsCompleted) return;
                    try
                    {
                        peer.SendWithDeliveryEvent(request.Bytes, 0, DeliveryMethod.ReliableOrdered, request);
                        inFlight++;
                        request.Ready.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        request.Ready.TrySetException(ex);
                        throw;
                    }
                }
            }

            public async Task<byte[]> ReadAsync(CancellationToken token)
            {
                lock (gate)
                    if (incoming.Count == 0 && failure != null) throw failure;
                await receiveReady.WaitAsync(token).ConfigureAwait(false);
                lock (gate)
                {
                    if (incoming.Count != 0) return incoming.Dequeue();
                    throw failure ?? new EndOfStreamException();
                }
            }

            public async Task WriteAsync(byte[] data, CancellationToken token)
            {
                if (data == null) throw new ArgumentNullException(nameof(data));
                if (data.Length == 0 || data.Length > FileTransferProtocol.MaxFrameBytes)
                    throw new ArgumentOutOfRangeException(nameof(data));
                await writeOrder.WaitAsync(token).ConfigureAwait(false);
                var request = new PendingFrame(data);
                try
                {
                    lock (gate)
                    {
                        if (failure != null) throw failure;
                        if (Volatile.Read(ref closeRequested) != 0) throw new ObjectDisposedException(nameof(UdpConnection));
                        pendingWrite = request;
                    }
                    WakePump();
                    using (token.Register(() => request.Ready.TrySetCanceled()))
                        await request.Ready.Task.ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                        if (ReferenceEquals(pendingWrite, request)) pendingWrite = null;
                    writeOrder.Release();
                }
            }

            private void Finish(Exception reason)
            {
                if (reason is OperationCanceledException) connected.TrySetCanceled();
                else connected.TrySetException(reason);
                lock (gate)
                {
                    failure = reason;
                    pendingWrite?.Ready.TrySetException(reason);
                    pendingWrite = null;
                    receiveReady.Release();
                }
            }

            private void WakePump()
            {
                try { pumpReady.Release(); }
                catch (SemaphoreFullException) { }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref closeRequested, 1) == 0) WakePump();
            }

            private sealed class PendingFrame
            {
                internal readonly byte[] Bytes;
                internal readonly TaskCompletionSource<bool> Ready = NewSignal();

                internal PendingFrame(byte[] bytes) { Bytes = bytes; }
            }

            private static TaskCompletionSource<bool> NewSignal()
            {
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
