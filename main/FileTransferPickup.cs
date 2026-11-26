using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KimNotes.utils;
using Newtonsoft.Json.Linq;

namespace KimNotes
{
    internal sealed class FileTransferPickup : IDisposable
    {
        private const string Path = "/transfers/pickup";
        private const int PublicKeyLength = 64;
        private static readonly byte[] PublicKeyHeader = { 0x45, 0x43, 0x4b, 0x31, 32, 0, 0, 0 };
        private readonly CloudSync cloud;
        private readonly CloudSession session;
        private readonly ECDiffieHellmanCng exchange;
        private readonly string publicKey;

        internal FileTransferPickup()
        {
            cloud = CloudSync.Current;
            if (cloud?.CanUseAccountTransfer != true)
                throw new InvalidOperationException("请先登录小羊便签。");
            session = cloud.Session;
            exchange = new ECDiffieHellmanCng(256) { HashAlgorithm = CngAlgorithm.Sha256 };
            try
            {
                var blob = exchange.Key.Export(CngKeyBlobFormat.EccPublicBlob);
                publicKey = Encode(blob, PublicKeyHeader.Length, PublicKeyLength);
            }
            catch
            {
                exchange.Dispose();
                throw;
            }
        }

        internal async Task<(string Code, int ExpiresIn)> RegisterAsync(Guid id, CancellationToken token)
        {
            var response = await PostPairingAsync(Path, new { id = id.ToString("D"), publicKey }, token);
            int expiresIn = response.Value<int>("expiresIn");
            if (ReadId(response) != id || expiresIn <= 0 || expiresIn > FileTransferP2p.PickupCodeWindowMinutes * 60 ||
                !FileTransferInvite.TryRead(response.Value<string>("code"), out string code) || !FileTransferInvite.IsShortCode(code))
                throw new InvalidDataException("无法读取取件码，请重试。");
            return (code, expiresIn);
        }

        internal async Task<string> WaitForReceiverAsync(Guid id, int expiresIn, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(expiresIn));
                try
                {
                    while (true)
                    {
                        var response = await RequestAsync(HttpMethod.Get, Path + "/" + id.ToString("D"), null, deadline.Token);
                        string key = response.Value<string>("publicKey");
                        if (!string.IsNullOrEmpty(key)) return Derive(id, key);
                        await Task.Delay(1500, deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("取件码已过期，请重新生成。");
                }
            }
        }

        internal async Task<string> ClaimAsync(string code, CancellationToken token)
        {
            var response = await PostPairingAsync(Path + "/claim", new { code, publicKey }, token);
            return Derive(ReadId(response), response.Value<string>("publicKey"));
        }

        internal async Task CancelAsync(Guid id)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await RequestAsync(HttpMethod.Delete, Path + "/" + id.ToString("D"), null, timeout.Token); }
                catch (Exception e) when (e is HttpRequestException || e is IOException || e is OperationCanceledException
                    || e is CloudApiException || e is InvalidOperationException || e is Newtonsoft.Json.JsonException)
                { System.Diagnostics.Debug.WriteLine(e.GetType().Name); }
            }
        }

        private async Task<JObject> PostPairingAsync(string path, object body, CancellationToken token)
        {
            try { return await RequestAsync(HttpMethod.Post, path, body, token); }
            catch (HttpRequestException) when (!token.IsCancellationRequested)
            {
                // The same offer ID and ephemeral key let the broker replay a successful response.
                await Task.Delay(350, token);
                return await RequestAsync(HttpMethod.Post, path, body, token);
            }
        }

        private async Task<JObject> RequestAsync(HttpMethod method, string path, object body, CancellationToken token)
        {
            try { return await cloud.AccountTransferRequest(method, path, session, body, token); }
            catch (CloudApiException e) when (e.Code == "pickup_unavailable")
            { throw new IOException(method == HttpMethod.Get ? "取件码已过期，请重新生成。" : "取件码无效、已使用或已过期。"); }
            catch (CloudApiException e) when (e.Status == 404 || e.Status == 405 || e.Status == 501)
            { throw new IOException("当前服务器还未开通 8 位取件码。"); }
            catch (CloudApiException e) when (e.Status == 429)
            { throw new IOException("操作太频繁，请稍后再试。"); }
            catch (CloudApiException e) when (e.Code == "pickup_busy")
            { throw new IOException("还有一个取件码在等对方接收，请稍后再试。"); }
        }

        private static Guid ReadId(JObject response)
        {
            if (!Guid.TryParseExact(response.Value<string>("id"), "D", out var id) || id == Guid.Empty)
                throw new InvalidDataException("配对服务返回的传输编号有误。");
            return id;
        }

        private string Derive(Guid id, string encoded)
        {
            if (encoded?.Length != 86) throw new InvalidDataException("配对公钥有误。");
            try
            {
                var coordinates = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + "==");
                if (coordinates.Length != PublicKeyLength || Encode(coordinates, 0, coordinates.Length) != encoded)
                    throw new InvalidDataException("配对公钥有误。");
                var blob = new byte[PublicKeyHeader.Length + PublicKeyLength];
                Buffer.BlockCopy(PublicKeyHeader, 0, blob, 0, PublicKeyHeader.Length);
                Buffer.BlockCopy(coordinates, 0, blob, PublicKeyHeader.Length, coordinates.Length);
                using (var peer = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                {
                    exchange.SecretPrepend = Encoding.ASCII.GetBytes("KimNotes pickup v2 " + id.ToString("D"));
                    var secret = exchange.DeriveKeyMaterial(peer);
                    try { return Encode(secret, 0, secret.Length); }
                    finally { Array.Clear(secret, 0, secret.Length); }
                }
            }
            catch (Exception e) when (e is FormatException || e is CryptographicException)
            { throw new InvalidDataException("配对公钥有误。", e); }
        }

        private static string Encode(byte[] data, int offset, int length) =>
            Convert.ToBase64String(data, offset, length).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public void Dispose() => exchange.Dispose();
    }
}
