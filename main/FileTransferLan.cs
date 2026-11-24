using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    internal sealed class LanTransferDevice
    {
        internal string Name { get; }
        internal IPEndPoint Endpoint { get; }
        internal byte[] Fingerprint { get; }

        internal LanTransferDevice(string name, IPEndPoint endpoint, byte[] fingerprint)
        {
            Name = name;
            Endpoint = endpoint;
            Fingerprint = fingerprint;
        }

        public override string ToString() => $"{Name} · {Endpoint.Address}";
    }

    internal static class FileTransferLan
    {
        internal const int DiscoveryPort = 45932;
        internal static readonly byte[] ReadyMessage = Encoding.ASCII.GetBytes("KNLAN/1 ready");
        private const int NonceBytes = 16, FingerprintBytes = 32, MaxNameBytes = 120;
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("KNLAN/1");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static int RequestBytes => Marker.Length + NonceBytes;
        private static int ResponseHeaderBytes => RequestBytes + 2 + FingerprintBytes;

        internal static async Task<IReadOnlyList<LanTransferDevice>> DiscoverAsync(CancellationToken token, bool excludeThisComputer = false)
        {
            token.ThrowIfCancellationRequested();
            byte[] request = new byte[RequestBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(request);
            Buffer.BlockCopy(Marker, 0, request, 0, Marker.Length);
            var networks = LocalNetworks();
            var devices = new Dictionary<string, LanTransferDevice>();
            using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (deadline.Token.Register(udp.Close))
            {
                udp.EnableBroadcast = true;
                deadline.CancelAfter(TimeSpan.FromMilliseconds(1500));
                try
                {
                    var targets = networks.Select(network => network.Broadcast).Concat(new[] { IPAddress.Broadcast });
                    if (!excludeThisComputer) targets = targets.Concat(new[] { IPAddress.Loopback });
                    foreach (var address in targets.Distinct())
                    {
                        try { await udp.SendAsync(request, request.Length, new IPEndPoint(address, DiscoveryPort)).ConfigureAwait(false); }
                        catch (SocketException) when (!deadline.IsCancellationRequested) { /* Try the remaining interfaces. */ }
                    }
                    while (devices.Count < 64)
                    {
                        UdpReceiveResult response;
                        try { response = await udp.ReceiveAsync().ConfigureAwait(false); }
                        catch (SocketException e) when (!deadline.IsCancellationRequested && e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                        var address = response.RemoteEndPoint.Address;
                        if (!IsLocal(address, networks) || excludeThisComputer &&
                            (IPAddress.IsLoopback(address) || networks.Any(network => network.Address.Equals(address)))) continue;
                        var device = ParseDevice(response.Buffer, request, response.RemoteEndPoint.Address);
                        if (device == null) continue;
                        var id = Convert.ToBase64String(device.Fingerprint);
                        if (!devices.ContainsKey(id)) devices.Add(id, device);
                    }
                }
                catch (ObjectDisposedException) when (deadline.IsCancellationRequested) { }
                catch (SocketException) when (deadline.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            return devices.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        internal static async Task<IFileTransferConnection> ConnectAsync(LanTransferDevice device, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            SslStream tls = null;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (deadline.Token.Register(tcp.Close))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await tcp.ConnectAsync(device.Endpoint.Address, device.Endpoint.Port).ConfigureAwait(false);
                    tls = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) =>
                        certificate != null && Hash(certificate.GetRawCertData()).SequenceEqual(device.Fingerprint));
                    await tls.AuthenticateAsClientAsync("KimNotes File Transfer", null, SslProtocols.Tls12, false).ConfigureAwait(false);
                    var connection = new LanTransferConnection(tcp, tls);
                    await connection.WriteAsync(ReadyMessage, deadline.Token).ConfigureAwait(false);
                    return connection;
                }
                catch (Exception) when (deadline.IsCancellationRequested)
                {
                    tcp.Close();
                    tls?.Dispose();
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("连接附近设备超时，请确认对方仍在等待接收。");
                }
                catch
                {
                    tcp.Close();
                    tls?.Dispose();
                    throw;
                }
            }
        }

        internal static bool IsDiscoveryRequest(byte[] data) => data.Length == RequestBytes && StartsWith(data, Marker);

        internal static byte[] CreateResponse(byte[] request, int port, byte[] fingerprint)
        {
            var name = Utf8.GetBytes(new string(Environment.MachineName.Where(c => !char.IsControl(c)).Take(30).ToArray()));
            var response = new byte[ResponseHeaderBytes + name.Length];
            Buffer.BlockCopy(request, 0, response, 0, request.Length);
            response[RequestBytes] = (byte)(port >> 8);
            response[RequestBytes + 1] = (byte)port;
            Buffer.BlockCopy(fingerprint, 0, response, RequestBytes + 2, FingerprintBytes);
            Buffer.BlockCopy(name, 0, response, ResponseHeaderBytes, name.Length);
            return response;
        }

        private static LanTransferDevice ParseDevice(byte[] data, byte[] request, IPAddress address)
        {
            int nameLength = data.Length - ResponseHeaderBytes;
            if (nameLength < 1 || nameLength > MaxNameBytes || !StartsWith(data, request)) return null;
            int port = (data[RequestBytes] << 8) | data[RequestBytes + 1];
            if (port == 0) return null;
            string name;
            try { name = Utf8.GetString(data, ResponseHeaderBytes, nameLength).Trim(); }
            catch (DecoderFallbackException) { return null; }
            if (name.Length == 0 || name.Any(char.IsControl)) return null;
            var fingerprint = new byte[FingerprintBytes];
            Buffer.BlockCopy(data, RequestBytes + 2, fingerprint, 0, fingerprint.Length);
            return new LanTransferDevice(name, new IPEndPoint(address, port), fingerprint);
        }

        private static bool StartsWith(byte[] value, byte[] prefix)
        {
            for (int i = 0; i < prefix.Length; i++)
                if (value[i] != prefix[i]) return false;
            return true;
        }

        internal static byte[] Hash(byte[] value)
        {
            using (var sha256 = SHA256.Create()) return sha256.ComputeHash(value);
        }

        internal static LanNetwork[] LocalNetworks()
        {
            var networks = new List<LanNetwork>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties properties;
                try { properties = adapter.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }
                foreach (var unicast in properties.UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask != null && !unicast.IPv4Mask.Equals(IPAddress.Any))
                        networks.Add(new LanNetwork(unicast.Address, unicast.IPv4Mask));
            }
            return networks.ToArray();
        }

        internal static bool IsLocal(IPAddress address, LanNetwork[] networks) =>
            address.AddressFamily == AddressFamily.InterNetwork &&
            (IPAddress.IsLoopback(address) || networks.Any(network => network.Contains(address)));

        internal sealed class LanNetwork
        {
            private readonly uint network, mask;
            internal IPAddress Address { get; }
            internal IPAddress Broadcast { get; }

            internal LanNetwork(IPAddress address, IPAddress mask)
            {
                Address = address;
                this.mask = ToNumber(mask);
                network = ToNumber(address) & this.mask;
                Broadcast = new IPAddress(BitConverter.GetBytes(network | ~this.mask));
            }

            internal bool Contains(IPAddress address) => (ToNumber(address) & mask) == network;
            private static uint ToNumber(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        }
    }

    internal sealed class LanTransferReceiver : IDisposable
    {
        private readonly TcpListener tcp;
        private readonly UdpClient udp;
        private readonly X509Certificate2 certificate;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        private readonly CancellationTokenRegistration stopOnExpiry;
        private readonly FileTransferLan.LanNetwork[] networks;
        private readonly byte[] fingerprint;
        private readonly object certificateLock = new object();
        private int disposed, stopped;

        private LanTransferReceiver(TcpListener tcp, UdpClient udp, X509Certificate2 certificate)
        {
            this.tcp = tcp;
            this.udp = udp;
            this.certificate = certificate;
            fingerprint = FileTransferLan.Hash(certificate.RawData);
            networks = FileTransferLan.LocalNetworks();
            stopOnExpiry = lifetime.Token.Register(StopListening);
            _ = RespondAsync(((IPEndPoint)tcp.LocalEndpoint).Port, lifetime.Token);
        }

        internal static LanTransferReceiver Start()
        {
            var tcp = new TcpListener(IPAddress.Any, 0) { ExclusiveAddressUse = true };
            X509Certificate2 certificate = null;
            UdpClient udp = null;
            try
            {
                using (var rsa = RSA.Create(2048))
                {
                    var request = new CertificateRequest("CN=KimNotes File Transfer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var now = DateTimeOffset.UtcNow;
                    using (var ephemeral = request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1)))
                    {
                        // Schannel needs a user key container for the server handshake. The temporary import releases it on disposal.
                        certificate = new X509Certificate2(ephemeral.Export(X509ContentType.Pfx), (string)null, X509KeyStorageFlags.UserKeySet);
                    }
                }
                udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, FileTransferLan.DiscoveryPort));
                tcp.Start(4);
                return new LanTransferReceiver(tcp, udp, certificate);
            }
            catch
            {
                udp?.Close();
                tcp.Stop();
                certificate?.Dispose();
                throw;
            }
        }

        private async Task RespondAsync(int port, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult request;
                    try { request = await udp.ReceiveAsync().ConfigureAwait(false); }
                    catch (SocketException e) when (!token.IsCancellationRequested && e.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    if (!FileTransferLan.IsLocal(request.RemoteEndPoint.Address, networks) || !FileTransferLan.IsDiscoveryRequest(request.Buffer)) continue;
                    var response = FileTransferLan.CreateResponse(request.Buffer, port, fingerprint);
                    try { await udp.SendAsync(response, response.Length, request.RemoteEndPoint).ConfigureAwait(false); }
                    catch (SocketException) when (!token.IsCancellationRequested) { /* The sender may have left the network. */ }
                }
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref stopped) != 0) { }
            catch (SocketException) when (Volatile.Read(ref stopped) != 0) { }
        }

        internal async Task<IFileTransferConnection> AcceptAsync(CancellationToken token)
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
            using (wait.Token.Register(StopListening))
            {
                try
                {
                    while (!wait.IsCancellationRequested)
                    {
                        var client = await tcp.AcceptTcpClientAsync().ConfigureAwait(false);
                        if (!FileTransferLan.IsLocal(((IPEndPoint)client.Client.RemoteEndPoint).Address, networks))
                        {
                            client.Close();
                            continue;
                        }
                        var connection = await AuthenticateAsync(client, wait.Token).ConfigureAwait(false);
                        if (connection != null)
                        {
                            StopListening();
                            return connection;
                        }
                    }
                }
                catch (Exception) when (wait.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("等待接收超时，可以重新开启接收。");
        }

        private async Task<LanTransferConnection> AuthenticateAsync(TcpClient client, CancellationToken token)
        {
            SslStream tls = null;
            X509Certificate2 copy = null;
            bool handedOff = false;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (handshake.Token.Register(client.Close))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    lock (certificateLock)
                    {
                        token.ThrowIfCancellationRequested();
                        copy = new X509Certificate2(certificate);
                    }
                    client.NoDelay = true;
                    tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(copy, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                    var connection = new LanTransferConnection(client, tls, copy);
                    var ready = await connection.ReadAsync(handshake.Token).ConfigureAwait(false);
                    if (!ready.SequenceEqual(FileTransferLan.ReadyMessage)) return null;
                    handedOff = true;
                    return connection;
                }
                catch (Exception) when (handshake.IsCancellationRequested) { return null; }
                catch (AuthenticationException) { return null; }
                catch (InvalidDataException) { return null; }
                catch (IOException) { return null; }
                catch (SocketException) { return null; }
                finally
                {
                    if (!handedOff)
                    {
                        client.Close();
                        tls?.Dispose();
                        copy?.Dispose();
                    }
                }
            }
        }

        private void StopListening()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            udp.Close();
            tcp.Stop();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            stopOnExpiry.Dispose();
            lock (certificateLock) certificate.Dispose();
            lifetime.Dispose();
        }
    }

    internal sealed class LanTransferConnection : IFileTransferConnection
    {
        private readonly TcpClient client;
        private readonly SslStream stream;
        private readonly X509Certificate2 certificate;

        internal LanTransferConnection(TcpClient client, SslStream stream, X509Certificate2 certificate = null)
        {
            this.client = client;
            this.stream = stream;
            this.certificate = certificate;
        }

        public async Task<byte[]> ReadAsync(CancellationToken token)
        {
            var prefix = new byte[4];
            using (token.Register(client.Close))
            {
                await ReadExactlyAsync(prefix, token).ConfigureAwait(false);
                int size = (prefix[0] << 24) | (prefix[1] << 16) | (prefix[2] << 8) | prefix[3];
                ValidateSize(size);
                var data = new byte[size];
                await ReadExactlyAsync(data, token).ConfigureAwait(false);
                return data;
            }
        }

        public async Task WriteAsync(byte[] data, CancellationToken token)
        {
            int size = data.Length;
            ValidateSize(size);
            var frame = new byte[4 + size];
            frame[0] = (byte)(size >> 24);
            frame[1] = (byte)(size >> 16);
            frame[2] = (byte)(size >> 8);
            frame[3] = (byte)size;
            Buffer.BlockCopy(data, 0, frame, 4, size);
            using (token.Register(client.Close))
            {
                try { await stream.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false); }
                catch (Exception) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); }
            }
        }

        private async Task ReadExactlyAsync(byte[] data, CancellationToken token)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int read = 0;
                try { read = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false); }
                catch (Exception) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); }
                if (read == 0) throw new EndOfStreamException("对方已断开连接。");
                offset += read;
            }
        }

        private static void ValidateSize(int size)
        {
            if (size < 1 || size > FileTransferProtocol.MaxFrameBytes) throw new InvalidDataException("传输数据格式无效。");
        }

        public void Dispose()
        {
            try { stream.Dispose(); }
            finally
            {
                client.Close();
                certificate?.Dispose();
            }
        }
    }
}
