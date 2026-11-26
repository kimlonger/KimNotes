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
    internal static class ProfileBrowserHandoff
    {
        private const string LoginPath = "login/?desktop=profile";
        private const int MaxHeaderBytes = 16384;
        private static readonly Regex Token = new Regex(@"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant);

        internal static async Task Open(Uri website, Func<string, string> openWebsite,
            Func<string, CancellationToken, Task<string>> issueCode, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (website == null) throw new InvalidOperationException(utils.DesktopEndpoints.MissingMessage);
            var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                listener.Start();
                try
                {
                    using (timeout.Token.Register(listener.Stop))
                    {
                        string port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
                        string state = NewState();
                        string error = openWebsite(LoginPath + "#port=" + port + "&state=" + state);
                        if (error != null) throw new InvalidOperationException(error);
                        while (true)
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            using (var client = await listener.AcceptTcpClientAsync())
                            using (timeout.Token.Register(client.Close))
                            {
                                string challenge;
                                using (var read = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
                                {
                                    // A speculative browser connection must not hold the real callback indefinitely.
                                    read.CancelAfter(TimeSpan.FromSeconds(2));
                                    using (read.Token.Register(client.Close))
                                    {
                                        try { challenge = await ReadChallenge(client.GetStream(), port, state, read.Token); }
                                        catch (Exception e) when (IsConnectionError(e)) { continue; }
                                    }
                                }
                                if (challenge == null)
                                {
                                    await Reply(client, "400 Bad Request", "无效的资料编辑请求。");
                                    continue;
                                }
                                string code;
                                try
                                {
                                    code = await issueCode(challenge, timeout.Token);
                                    timeout.Token.ThrowIfCancellationRequested();
                                    if (code == null || !Token.IsMatch(code)) throw new InvalidDataException("网页登录响应无效。");
                                }
                                catch
                                {
                                    await Reply(client, "503 Service Unavailable", "暂时无法打开资料页，请回到小羊便签重试。");
                                    throw;
                                }
                                string destination = new Uri(website, LoginPath + "#code=" + code).AbsoluteUri;
                                await Reply(client, "302 Found", "", destination);
                                return;
                            }
                        }
                    }
                }
                catch (Exception e) when (timeout.IsCancellationRequested && (IsConnectionError(e) || e is InvalidOperationException))
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new TimeoutException("网页登录已超时，请再点一次编辑资料。");
                }
                finally { listener.Stop(); }
            }
        }

        private static string NewState()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool IsConnectionError(Exception error) => error is IOException || error is SocketException ||
            error is ObjectDisposedException || error is OperationCanceledException;

        private static async Task<string> ReadChallenge(NetworkStream stream, string port, string state, CancellationToken cancel)
        {
            var buffer = new byte[MaxHeaderBytes];
            int used = 0;
            while (used < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, used, buffer.Length - used, cancel);
                if (read == 0) return null;
                used += read;
                string headers = Encoding.ASCII.GetString(buffer, 0, used);
                int end = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                string[] lines = headers.Substring(0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                string prefix = "/profile/" + state + "?challenge=";
                if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" ||
                    !request[1].StartsWith(prefix, StringComparison.Ordinal)) return null;
                string challenge = request[1].Substring(prefix.Length);
                if (!Token.IsMatch(challenge)) return null;
                bool hasHost = false;
                for (int i = 1; i < lines.Length; i++)
                {
                    if (!lines[i].StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (hasHost || lines[i].Substring(5).Trim() != "127.0.0.1:" + port) return null;
                    hasHost = true;
                }
                return hasHost ? challenge : null;
            }
            return null;
        }

        private static async Task Reply(TcpClient client, string status, string message, string destination = null)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            string headers = "HTTP/1.1 " + status + "\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                (destination == null ? "" : "Location: " + destination + "\r\n") + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(headers);
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(bytes, 0, bytes.Length);
                if (body.Length != 0) await stream.WriteAsync(body, 0, body.Length);
            }
            catch (Exception e) when (destination == null && IsConnectionError(e)) { }
        }
    }
}
