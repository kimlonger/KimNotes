using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KimNotes.utils;

namespace KimNotes
{
    internal sealed class FileTransferDirectTimeoutException : TimeoutException
    {
        internal const string UserMessage = "已配对，无线直连未建立。";
        internal string Diagnostic { get; }

        internal FileTransferDirectTimeoutException(string diagnostic) : base(UserMessage)
        {
            Diagnostic = diagnostic;
        }
    }

    internal static class FileTransferP2p
    {
        private const int CodeBytes = 32;
        private const int PeerWaitTimeoutMs = 120000;
        private const int AccountMatchTimeoutMs = 25000;
        internal const int PickupCodeWindowMinutes = 5;
        internal static bool HasFixedLocalPort => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KIMNOTES_P2P_PORT"));

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
            return OpenAsync(code, true, PickupCodeWindowMinutes * 60000, token);
        }

        internal static Task WaitForClosure(IFileTransferConnection wire)
        {
            if (wire is UdpConnection direct) return direct.Closed;
            throw new ArgumentException("Expected a direct transfer connection.", nameof(wire));
        }

        internal static string TransferRoute(IFileTransferConnection wire) => (wire as UdpConnection)?.Route;

        private static async Task<IFileTransferConnection> OpenAsync(string code, bool sender, int matchTimeoutMs, CancellationToken token)
        {
            var secret = DecodeCode(code);
            string room;
            string connectionKey;
            byte[] probe, expectedProbe;
            try
            {
                using (var hash = SHA256.Create())
                    room = BitConverter.ToString(hash.ComputeHash(secret)).Replace("-", "").ToLowerInvariant();
                using (var auth = new HMACSHA256(secret))
                {
                    connectionKey = EncodeCode(auth.ComputeHash(Encoding.ASCII.GetBytes("KimNotes file transfer UDP connect v1")));
                    probe = AuthenticatedProbe(auth, sender);
                    expectedProbe = AuthenticatedProbe(auth, !sender);
                }
            }
            finally { Array.Clear(secret, 0, secret.Length); }

            int localPort = ReadBindPort();
            var server = await ResolveServerAsync(token).ConfigureAwait(false);
            var connection = new UdpConnection(server, room, connectionKey, probe, expectedProbe, sender, matchTimeoutMs, localPort);
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
                throw new ArgumentException("请输入另一台电脑生成的完整取件码。", nameof(code));

            byte[] bytes;
            try { bytes = Convert.FromBase64String(code.Replace('-', '+').Replace('_', '/') + "="); }
            catch (FormatException ex) { throw new ArgumentException("取件码格式不正确。", nameof(code), ex); }
            if (EncodeCode(bytes) != code)
            {
                Array.Clear(bytes, 0, bytes.Length);
                throw new ArgumentException("取件码格式不正确。", nameof(code));
            }
            return bytes;
        }

        private static async Task<IPEndPoint> ResolveServerAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var config = Environment.GetEnvironmentVariable("KIMNOTES_P2P_SERVER");
            var address = string.IsNullOrWhiteSpace(config) ? DesktopEndpoints.RendezvousEndpoint : config.Trim();
            if (address == null) throw new IOException("本构建未配置跨网配对服务器。");
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

        private static int ReadBindPort()
        {
            string value = Environment.GetEnvironmentVariable("KIMNOTES_P2P_PORT");
            if (string.IsNullOrWhiteSpace(value)) return 0;
            if (!TryPort(value.Trim(), out int port))
                throw new IOException("KIMNOTES_P2P_PORT 需填写 1 到 65535 之间的端口号。");
            return port;
        }

        private static byte[] AuthenticatedProbe(HMACSHA256 auth, bool sender)
        {
            var label = "KimNotes file transfer UDP probe v1 " + (sender ? "s" : "r");
            var proof = EncodeCode(auth.ComputeHash(Encoding.ASCII.GetBytes(label)));
            return Encoding.ASCII.GetBytes("KNFT1 punch " + proof);
        }

        private static bool IsPrivateV4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || (bytes[0] == 172 && (bytes[1] & 0xf0) == 16) || (bytes[0] == 192 && bytes[1] == 168);
        }

        private static bool IsGlobalV6(IPAddress address)
        {
            return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0
                && (address.GetAddressBytes()[0] & 0xe0) == 0x20;
        }

        private static bool SameSubnet(UnicastIPAddressInformation local, IPAddress remote)
        {
            if (!IsPrivateV4(local.Address) || local.IPv4Mask == null) return false;
            uint networkMask = IPv4Number(local.IPv4Mask);
            uint candidate = IPv4Number(remote);
            if (networkMask == 0 || (IPv4Number(local.Address) & networkMask) != (candidate & networkMask))
                return false;

            uint hostMask = ~networkMask;
            uint host = candidate & hostMask;
            return hostMask <= 1 || (host != 0 && host != hostMask);
        }

        private static uint IPv4Number(IPAddress address)
        {
            return unchecked((uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(address.GetAddressBytes(), 0)));
        }

        private static UnicastIPAddressInformation[] LocalInterfaceAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                        && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .Select(adapter => adapter.GetIPProperties())
                    .OrderByDescending(network => network.GatewayAddresses.Count > 0)
                    .SelectMany(network => network.UnicastAddresses)
                    .Where(item => IsPrivateV4(item.Address) || IsGlobalV6(item.Address))
                    .ToArray();
            }
            catch (NetworkInformationException) { return Array.Empty<UnicastIPAddressInformation>(); }
        }

        private sealed class UdpConnection : IFileTransferConnection
        {
            private const int ReceiveQueueLimit = 64;
            private const int FramesInFlightLimit = 8;
            private const int DirectConnectTimeoutMs = 18000;
            private const int AdvertisedCandidateLimit = 4;
            private const int LearnedPortLimit = 3;
            private const int EndpointAttemptLimit = 3;
            private const int LanHeadStartMs = 350;
            private readonly object gate = new object();
            private readonly Queue<byte[]> incoming = new Queue<byte[]>();
            private readonly SemaphoreSlim receiveReady = new SemaphoreSlim(0);
            private readonly SemaphoreSlim writeOrder = new SemaphoreSlim(1);
            private readonly SemaphoreSlim pumpReady = new SemaphoreSlim(0, 1);
            private readonly TaskCompletionSource<bool> connected = NewSignal();
            private readonly TaskCompletionSource<bool> closed = NewSignal();
            private readonly Stopwatch clock = new Stopwatch();
            private readonly IPEndPoint server;
            private readonly string room;
            private readonly string connectionKey;
            private readonly byte[] expectedConnectionData;
            private readonly byte[] probe, expectedProbe;
            private readonly bool sender;
            private readonly int matchTimeoutMs;
            private readonly int localPort;
            private readonly NetManager manager;
            private readonly List<IPEndPoint> targets = new List<IPEndPoint>();
            private readonly HashSet<IPAddress> introducedAddresses = new HashSet<IPAddress>();
            private readonly HashSet<IPAddress> lanAddresses = new HashSet<IPAddress>();
            private readonly Dictionary<IPEndPoint, ConnectionAttempt> attempts = new Dictionary<IPEndPoint, ConnectionAttempt>();
            private UnicastIPAddressInformation[] localAddresses;
            private string advertisedCandidates;
            private string registrationCookie = "-";
            private bool sendExtendedRegistration = true;
            private bool receivedExtendedPeer;
            private bool receivedAuthenticatedProbe;
            private bool receivedAuthenticatedRequest;
            private int lanCandidates;
            private int ipv6Candidates;
            private IPEndPoint acceptedIncomingEndpoint;
            private NetPeer peer;
            private PendingFrame pendingWrite;
            private Exception failure;
            private Exception transportFailure;
            private int inFlight;
            private int closeRequested;
            private int learnedPorts;
            private long nextRegistration;
            private long nextProbe;
            private long nextConnectionAttempt;
            private long pairStart = -1;
            private long firstLanAttempt = -1;

            internal UdpConnection(IPEndPoint server, string room, string connectionKey, byte[] probe, byte[] expectedProbe,
                bool sender, int matchTimeoutMs, int localPort)
            {
                this.server = server;
                this.room = room;
                this.connectionKey = connectionKey;
                expectedConnectionData = NetDataWriter.FromString(connectionKey).CopyData();
                this.probe = probe;
                this.expectedProbe = expectedProbe;
                this.sender = sender;
                this.matchTimeoutMs = matchTimeoutMs;
                this.localPort = localPort;

                var listener = new EventBasedNetListener();
                manager = new NetManager(listener)
                {
                    IPv6Enabled = Socket.OSSupportsIPv6,
                    UnconnectedMessagesEnabled = true,
                    AutoRecycle = true,
                    DisconnectTimeout = 15000,
                    ReconnectDelay = 300,
                    MaxConnectAttempts = 48
                };
                listener.NetworkReceiveUnconnectedEvent += OnUnconnectedMessage;
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

            internal Task Closed => closed.Task;
            internal string Route { get; private set; }

            private async Task PumpAsync(CancellationToken token)
            {
                Exception reason = new ObjectDisposedException(nameof(UdpConnection));
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!manager.StartInManualMode(localPort))
                        throw new IOException(localPort == 0 ? "无法打开无线传输端口。"
                            : $"无法绑定无线传输端口 {localPort}，请检查端口占用或权限。");

                    localAddresses = LocalInterfaceAddresses();
                    var addresses = localAddresses.Select(item => item.Address).Distinct().ToArray();
                    // Keep at least one IPv4 and IPv6 address when both are available.
                    var preferred = addresses.Where(IsPrivateV4).Take(2).Concat(addresses.Where(IsGlobalV6).Take(2));
                    advertisedCandidates = string.Join(",", preferred.Concat(addresses).Distinct()
                        .Take(AdvertisedCandidateLimit).Select(address => new IPEndPoint(address, manager.LocalPort).ToString()));
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
                if (pairStart < 0)
                {
                    if (now >= matchTimeoutMs)
                        throw new TimeoutException(matchTimeoutMs == PickupCodeWindowMinutes * 60000
                            ? "取件码已过期，请重新生成。"
                            : "无线传输配对超时。请确认另一台电脑已打开文件互传，使用取件码时请确认取件码有效。");
                    Register(now);
                    return;
                }

                if (now - pairStart >= DirectConnectTimeoutMs)
                    throw DirectTimeout();

                if (!receivedExtendedPeer && now - pairStart < 2400) Register(now);
                if (now >= nextProbe)
                {
                    foreach (var target in targets)
                        manager.SendUnconnectedMessage(probe, target);
                    nextProbe = now + 260;
                }
                if (sender && now >= nextConnectionAttempt)
                {
                    var candidate = NextCandidate(now);
                    if (candidate != null) StartConnection(candidate, now);
                }
            }

            private IPEndPoint NextCandidate(long now)
            {
                var lan = targets.FirstOrDefault(target => lanAddresses.Contains(target.Address) && !attempts.ContainsKey(target));
                if (lan != null) return lan;
                // A viable local route gets a short lead. Do not wait for an address that was not offered.
                if (firstLanAttempt >= 0 && now - firstLanAttempt < LanHeadStartMs) return null;

                var ordered = targets.OrderBy(target => lanAddresses.Contains(target.Address) ? 0
                    : target.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 2);
                return ordered.FirstOrDefault(target => !attempts.ContainsKey(target))
                    ?? ordered.FirstOrDefault(target => attempts[target].CanRetry(now));
            }

            private void StartConnection(IPEndPoint candidate, long now)
            {
                if (firstLanAttempt < 0 && lanAddresses.Contains(candidate.Address)) firstLanAttempt = now;
                if (!attempts.TryGetValue(candidate, out var attempt))
                {
                    attempt = new ConnectionAttempt();
                    attempts.Add(candidate, attempt);
                }
                attempt.Count++;
                attempt.RetryAt = now + 650;
                attempt.Peer = manager.Connect(candidate, connectionKey);
                nextConnectionAttempt = now + 110;
            }

            private FileTransferDirectTimeoutException DirectTimeout()
            {
                return new FileTransferDirectTimeoutException(string.Join(Environment.NewLine,
                    "文件互传 UDP 直连诊断",
                    "角色：" + (sender ? "发送" : "接收"),
                    "收到带候选地址的配对响应：" + (receivedExtendedPeer ? "是" : "否"),
                    "可用局域网候选数：" + lanCandidates.ToString(CultureInfo.InvariantCulture),
                    "可用 IPv6 候选数：" + ipv6Candidates.ToString(CultureInfo.InvariantCulture),
                    "收到对方认证探测：" + (receivedAuthenticatedProbe ? "是" : "否"),
                    "收到对方认证连接请求：" + (receivedAuthenticatedRequest ? "是" : "否"),
                    "主动连接尝试次数：" + attempts.Values.Sum(attempt => attempt.Count).ToString(CultureInfo.InvariantCulture)));
            }

            private void Register(long now)
            {
                if (now < nextRegistration) return;
                string message = $"KNFT1 {(sender ? "s" : "r")} {room} {registrationCookie}";
                if (sendExtendedRegistration && !string.IsNullOrEmpty(advertisedCandidates))
                    message += " " + advertisedCandidates;
                // Legacy rendezvous listeners only accept the original four-field message.
                sendExtendedRegistration = !sendExtendedRegistration;
                manager.SendUnconnectedMessage(Encoding.ASCII.GetBytes(message), server);
                nextRegistration = now + 650;
            }

            private void OnUnconnectedMessage(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
            {
                if (peer != null || type != UnconnectedMessageType.BasicMessage || reader.AvailableBytes > 320) return;
                if (server.Equals(remote))
                {
                    OnRendezvousMessage(Encoding.ASCII.GetString(reader.GetRemainingBytes()));
                    return;
                }

                if (pairStart < 0 || !introducedAddresses.Contains(remote.Address)
                    || reader.AvailableBytes != expectedProbe.Length
                    || !FileTransferProtocol.FixedEquals(expectedProbe, reader.GetRemainingBytes())) return;
                receivedAuthenticatedProbe = true;
                // A peer may have a different UDP mapping toward us than toward the rendezvous.
                // Only learn a new port for an introduced address using the shared invite proof.
                if (CanLearnPort(remote)) LearnPort(remote);
            }

            private void OnRendezvousMessage(string message)
            {
                var parts = message.Split(' ');
                if (parts.Length < 3 || parts[0] != "KNFT1") return;
                if (parts.Length == 3 && parts[1] == "challenge" && ValidCookie(parts[2]))
                {
                    registrationCookie = parts[2];
                    sendExtendedRegistration = true;
                    nextRegistration = 0;
                    return;
                }
                if ((parts.Length != 5 && parts.Length != 6) || parts[1] != "peer" || parts[2] != room
                    || !IPAddress.TryParse(parts[3], out var address) || address.AddressFamily != AddressFamily.InterNetwork
                    || !TryPort(parts[4], out var port)) return;

                if (pairStart < 0)
                {
                    pairStart = clock.ElapsedMilliseconds;
                    nextConnectionAttempt = pairStart + 250;
                }
                if (parts.Length == 6 && !receivedExtendedPeer)
                {
                    receivedExtendedPeer = true;
                    foreach (var candidate in parts[5].Split(',').Take(AdvertisedCandidateLimit))
                    {
                        if (!TryParseCandidate(candidate, out var endpoint)) continue;
                        if (endpoint.AddressFamily == AddressFamily.InterNetwork) lanAddresses.Add(endpoint.Address);
                        if (!Introduce(endpoint)) continue;
                        if (endpoint.AddressFamily == AddressFamily.InterNetworkV6) ipv6Candidates++;
                        else lanCandidates++;
                    }
                }
                Introduce(new IPEndPoint(address, port));
            }

            private bool TryParseCandidate(string value, out IPEndPoint endpoint)
            {
                endpoint = null;
                int separator = value.LastIndexOf(':');
                if (separator <= 0 || !TryPort(value.Substring(separator + 1), out var port)
                    || !IPAddress.TryParse(value.Substring(0, separator).Trim('[', ']'), out var address)) return false;
                if (!(IsPrivateV4(address) && localAddresses.Any(local => SameSubnet(local, address)))
                    && !(manager.IPv6Enabled && IsGlobalV6(address) && localAddresses.Any(local => IsGlobalV6(local.Address)))) return false;
                endpoint = new IPEndPoint(address, port);
                return endpoint.ToString() == value;
            }

            private bool Introduce(IPEndPoint endpoint)
            {
                if (endpoint.Port == manager.LocalPort && localAddresses.Any(local => local.Address.Equals(endpoint.Address)))
                    return false;
                if (server.Equals(endpoint) || targets.Contains(endpoint) || targets.Count >= 1 + AdvertisedCandidateLimit + LearnedPortLimit)
                    return false;
                targets.Add(endpoint);
                introducedAddresses.Add(endpoint.Address);
                return true;
            }

            private bool CanLearnPort(IPEndPoint endpoint)
            {
                return learnedPorts < LearnedPortLimit && introducedAddresses.Contains(endpoint.Address)
                    && !server.Equals(endpoint) && !targets.Contains(endpoint);
            }

            private void LearnPort(IPEndPoint endpoint)
            {
                if (Introduce(new IPEndPoint(endpoint.Address, endpoint.Port))) learnedPorts++;
            }

            private static bool ValidCookie(string value)
            {
                return value.Length >= 16 && value.Length <= 96
                    && value.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                        || (c >= '0' && c <= '9') || c == '-' || c == '_');
            }

            private void OnConnectionRequest(ConnectionRequest request)
            {
                var endpoint = request.RemoteEndPoint;
                bool known = targets.Contains(endpoint);
                if (sender || peer != null || (!known && !CanLearnPort(endpoint))
                    || request.Data.AvailableBytes != expectedConnectionData.Length
                    || !FileTransferProtocol.FixedEquals(expectedConnectionData, request.Data.GetRemainingBytes()))
                {
                    request.Reject();
                    return;
                }

                // The authenticated connection request itself can arrive before the probe.
                // Register the verified source before LiteNetLib raises PeerConnected.
                if (!known) LearnPort(endpoint);
                if (!targets.Contains(endpoint)) { request.Reject(); return; }
                receivedAuthenticatedRequest = true;
                acceptedIncomingEndpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
                request.Accept();
            }

            private void OnPeerConnected(NetPeer remote)
            {
                bool expected = sender
                    ? attempts.TryGetValue(remote, out var attempt) && ReferenceEquals(attempt.Peer, remote)
                    : acceptedIncomingEndpoint?.Equals(remote) == true;
                if (peer != null || !expected)
                {
                    manager.DisconnectPeer(remote);
                    return;
                }
                peer = remote;
                Route = lanAddresses.Contains(remote.Address) ? "局域网传输" : "无线传输";
                connected.TrySetResult(true);
            }

            private void OnPeerDisconnected(NetPeer remote, DisconnectInfo info)
            {
                if (ReferenceEquals(remote, peer) && transportFailure == null)
                {
                    transportFailure = new EndOfStreamException("另一台电脑已断开连接。");
                    return;
                }
                if (!sender || peer != null || remote == null || !attempts.TryGetValue(remote, out var attempt)
                    || !ReferenceEquals(attempt.Peer, remote)) return;

                attempt.Peer = null;
                attempt.RetryAt = clock.ElapsedMilliseconds + 650;
                if (info.Reason != DisconnectReason.ConnectionRejected && info.Reason != DisconnectReason.ConnectionFailed)
                    attempt.RetryAt = long.MaxValue;
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
                closed.TrySetResult(true);
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

            private sealed class ConnectionAttempt
            {
                internal int Count;
                internal NetPeer Peer;
                internal long RetryAt;

                internal bool CanRetry(long now) => Peer == null && Count < EndpointAttemptLimit && now >= RetryAt;
            }

            private static TaskCompletionSource<bool> NewSignal()
            {
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
