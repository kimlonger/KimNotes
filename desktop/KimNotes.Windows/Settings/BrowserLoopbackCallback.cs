using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes.Windows.Settings
{
    internal sealed class BrowserLoopbackCallback : IDisposable
    {
        private const int MaxHeaderBytes = 16384;
        private static readonly Regex Token = new Regex(@"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant);
        private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(2);
        private readonly TcpClient client;
        internal string Value { get; }
        internal bool HasResultPreference { get; }

        private BrowserLoopbackCallback(TcpClient client, string value, bool hasResultPreference)
        {
            this.client = client;
            Value = value;
            HasResultPreference = hasResultPreference;
        }

        internal static async Task<BrowserLoopbackCallback> Wait(string path, string parameter,
            Func<string, string> openWebsite, TimeSpan duration, string invalidMessage, string expiredMessage, CancellationToken cancel,
            string resultPreference = null)
        {
            cancel.ThrowIfCancellationRequested();
            var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(duration);
                listener.Start();
                try
                {
                    using (timeout.Token.Register(listener.Stop))
                    {
                        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        if (port < 1024) throw new InvalidOperationException("无法打开本机登录回调，请稍后重试。");
                        string hostPort = port.ToString(CultureInfo.InvariantCulture);
                        string error = openWebsite(hostPort);
                        if (error != null) throw new InvalidOperationException(error);
                        while (true)
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            var client = await listener.AcceptTcpClientAsync();
                            bool accepted = false;
                            try
                            {
                                var request = await Read(client, hostPort, path + "?" + parameter + "=", resultPreference, timeout.Token);
                                if (request.Value == null)
                                {
                                    await Reply(client, "400 Bad Request", invalidMessage);
                                    continue;
                                }
                                timeout.Token.ThrowIfCancellationRequested();
                                accepted = true;
                                return new BrowserLoopbackCallback(client, request.Value, request.HasResultPreference);
                            }
                            finally
                            {
                                if (!accepted) client.Dispose();
                            }
                        }
                    }
                }
                catch (Exception error) when (timeout.IsCancellationRequested &&
                    (IsConnectionError(error) || error is InvalidOperationException))
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new TimeoutException(expiredMessage);
                }
                finally { listener.Stop(); }
            }
        }

        internal static string NewToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Encode(bytes);
        }

        internal static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static bool IsToken(string value) => value != null && Token.IsMatch(value);

        private static bool IsConnectionError(Exception error) => error is IOException || error is SocketException ||
            error is ObjectDisposedException || error is OperationCanceledException;

        private static async Task<(string Value, bool HasResultPreference)> Read(TcpClient client, string port, string prefix,
            string resultPreference, CancellationToken cancel)
        {
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                read.CancelAfter(ConnectionTimeout);
                using (read.Token.Register(client.Close))
                {
                    try { return await ReadValue(client.GetStream(), port, prefix, resultPreference, read.Token); }
                    catch (Exception error) when (IsConnectionError(error)) { return default; }
                }
            }
        }

        private static async Task<(string Value, bool HasResultPreference)> ReadValue(NetworkStream stream, string port, string prefix,
            string resultPreference, CancellationToken cancel)
        {
            var buffer = new byte[MaxHeaderBytes];
            int used = 0;
            while (used < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, used, buffer.Length - used, cancel);
                if (read == 0) return default;
                used += read;
                string headers = Encoding.ASCII.GetString(buffer, 0, used);
                int end = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                string[] lines = headers.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" ||
                    !request[1].StartsWith(prefix, StringComparison.Ordinal)) return default;
                string value = request[1].Substring(prefix.Length);
                bool hasResultPreference = resultPreference != null && value.EndsWith(resultPreference, StringComparison.Ordinal);
                if (hasResultPreference) value = value.Substring(0, value.Length - resultPreference.Length);
                if (!IsToken(value)) return default;
                bool hasHost = false;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (!lines[i].StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (hasHost || lines[i].Substring(5).Trim() != "127.0.0.1:" + port) return default;
                    hasHost = true;
                }
                return hasHost ? (value, hasResultPreference) : default;
            }
            return default;
        }

        internal Task Redirect(Uri destination) => Reply(client, "302 Found", "", destination: destination.AbsoluteUri);

        internal Task RedirectResult(Uri destination) =>
            Reply(client, "303 See Other", "", destination: destination.AbsoluteUri, ignoreDisconnected: true);

        internal Task Text(string status, string message) => Reply(client, status, message);

        internal Task Html(string status, string message) => Reply(client, status, message, "text/html");

        private static async Task Reply(TcpClient client, string status, string message, string contentType = "text/plain",
            string destination = null, bool ignoreDisconnected = false)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            string headers = "HTTP/1.1 " + status + "\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n" +
                "X-Content-Type-Options: nosniff\r\nContent-Type: " + contentType + "; charset=utf-8\r\n" +
                "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                (destination == null ? "" : "Location: " + destination + "\r\n") + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(headers);
            using (var write = new CancellationTokenSource(ConnectionTimeout))
            using (write.Token.Register(client.Close))
            {
                try
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length, write.Token);
                    if (body.Length != 0) await stream.WriteAsync(body, 0, body.Length, write.Token);
                }
                catch (Exception error) when ((destination == null || ignoreDisconnected) && IsConnectionError(error)) { }
            }
        }

        public void Dispose() => client.Dispose();
    }
}
