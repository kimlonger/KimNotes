using KimNotes.utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    internal sealed class AccountTransferDevice
    {
        internal string Id { get; }
        internal string Name { get; }
        internal CloudSync Cloud { get; }
        internal CloudSession Login { get; }

        internal AccountTransferDevice(string id, string name, CloudSync cloud, CloudSession login)
        {
            Id = id;
            Name = name;
            Cloud = cloud;
            Login = login;
        }

        public override string ToString() => Name;
    }

    internal static class FileTransferAccount
    {
        private const string Receivers = "/transfers/receivers";

        internal static bool IsAvailable => CloudSync.Current?.CanUseAccountTransfer == true;

        internal static async Task<IReadOnlyList<AccountTransferDevice>> DiscoverAsync(CancellationToken token)
        {
            var cloud = RequireCloud();
            var login = cloud.Session;
            using (var operation = StartOperation(cloud, login, token))
            {
                var response = await Request(cloud, login, HttpMethod.Get, Receivers, null, operation.Token);
                if (!(response["receivers"] is JArray items)) throw new InvalidDataException("接收设备列表格式不正确。");
                var result = new List<AccountTransferDevice>();
                foreach (var item in items)
                {
                    if (!(item is JObject entry)) continue;
                    string id = entry.Value<string>("id");
                    if (!ValidReceiverId(id)) continue;
                    string name = entry.Value<string>("name")?.Trim();
                    result.Add(new AccountTransferDevice(id, string.IsNullOrEmpty(name) ? "Windows 电脑" : name, cloud, login));
                }
                return result;
            }
        }

        internal static async Task<IFileTransferConnection> ConnectAsync(AccountTransferDevice device, CancellationToken token)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var operation = StartOperation(device.Cloud, device.Login, token);
            try
            {
                string code = FileTransferP2p.CreateCode();
                await Request(device.Cloud, device.Login, HttpMethod.Post, ReceiverPath(device.Id) + "/offer",
                    new { code }, operation.Token);
                return await ConnectDirectAsync(code, true, operation);
            }
            catch
            {
                operation.Dispose();
                throw;
            }
        }

        internal static async Task<IFileTransferConnection> AcceptAsync(IProgress<string> progress, CancellationToken token)
        {
            var cloud = RequireCloud();
            var login = cloud.Session;
            var operation = StartOperation(cloud, login, token);
            string receiverPath = null;
            try
            {
                var registration = await Request(cloud, login, HttpMethod.Post, Receivers, new { }, operation.Token);
                receiverPath = ReceiverPath(registration.Value<string>("id"));
                int expiresIn = registration.Value<int>("expiresIn");
                if (expiresIn <= 0 || expiresIn > 600)
                    throw new InvalidDataException("接收设备的有效期格式不正确。");

                progress?.Report("等待登录同一账号的电脑发送文件…");
                string code;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(expiresIn));
                    try { code = await WaitForOfferAsync(cloud, login, receiverPath + "/offer", deadline.Token); }
                    catch (OperationCanceledException) when (!operation.IsCancellationRequested)
                    { throw new TimeoutException("接收等待已超时，请重新开启接收。"); }
                }
                progress?.Report("正在建立跨网直连…");
                return await ConnectDirectAsync(code, false, operation);
            }
            catch
            {
                operation.Dispose();
                throw;
            }
            finally
            {
                if (receiverPath != null) await CloseRegistrationAsync(cloud, login, receiverPath);
            }
        }

        private static async Task<string> WaitForOfferAsync(CloudSync cloud, CloudSession login, string path, CancellationToken token)
        {
            while (true)
            {
                var offer = await Request(cloud, login, HttpMethod.Get, path, null, token);
                if (offer["code"] != null)
                {
                    string code = offer.Value<string>("code");
                    byte[] decoded;
                    try { decoded = FileTransferP2p.DecodeCode(code); }
                    catch (ArgumentException error) { throw new InvalidDataException("同账号配对响应无效。", error); }
                    Array.Clear(decoded, 0, decoded.Length);
                    return code;
                }
                // The service normally waits for an offer before responding.
                await Task.Delay(350, token);
            }
        }

        private static async Task<IFileTransferConnection> ConnectDirectAsync(string code, bool sender, CancellationTokenSource operation)
        {
            var wire = await (sender ? FileTransferP2p.ConnectAsync(code, operation.Token) : FileTransferP2p.AcceptAsync(code, operation.Token));
            try
            {
                var encrypted = await EncryptedFileTransfer.OpenAsync(wire, code, sender, operation.Token);
                return new AccountConnection(encrypted, operation);
            }
            catch
            {
                wire.Dispose();
                throw;
            }
        }

        private static CloudSync RequireCloud()
        {
            var cloud = CloudSync.Current;
            if (cloud?.CanUseAccountTransfer != true)
                throw new InvalidOperationException("请在两台电脑登录同一个小羊账号后重试。");
            return cloud;
        }

        private static CancellationTokenSource StartOperation(CloudSync cloud, CloudSession login, CancellationToken token)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cloud.AccountTransferLifetime(login), token);
        }

        private static async Task<JObject> Request(CloudSync cloud, CloudSession login, HttpMethod method, string path,
            object body, CancellationToken token)
        {
            try { return await cloud.AccountTransferRequest(method, path, login, body, token); }
            catch (CloudApiException error) when ((error.Status == 404 || error.Status == 405 || error.Status == 501) && path == Receivers)
            { throw new IOException("跨网配对服务暂未启用，可以先使用局域网互传。", error); }
            catch (CloudApiException error) when (error.Status == 404 || error.Status == 410)
            { throw new IOException("接收设备已离开，请重新查找。", error); }
            catch (HttpRequestException error)
            { throw new IOException("暂时无法连接跨网配对服务，请检查网络。", error); }
        }

        private static async Task CloseRegistrationAsync(CloudSync cloud, CloudSession login, string path)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await cloud.AccountTransferRequest(HttpMethod.Delete, path, login, null, timeout.Token); }
                catch (Exception error) when (error is OperationCanceledException || error is HttpRequestException
                    || error is CloudApiException || error is InvalidOperationException || error is IOException
                    || error is Newtonsoft.Json.JsonException) { }
            }
        }

        private static string ReceiverPath(string id)
        {
            if (!ValidReceiverId(id)) throw new InvalidDataException("接收设备标识格式不正确。");
            return Receivers + "/" + id;
        }

        private static bool ValidReceiverId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char character in id)
                if (!(character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z'
                    || character >= '0' && character <= '9' || character == '-' || character == '_')) return false;
            return true;
        }

        private sealed class AccountConnection : IFileTransferConnection
        {
            private readonly IFileTransferConnection inner;
            private readonly CancellationTokenSource lifetime;
            private readonly CancellationToken operationToken;
            private int disposed;

            internal AccountConnection(IFileTransferConnection inner, CancellationTokenSource lifetime)
            {
                this.inner = inner;
                this.lifetime = lifetime;
                operationToken = lifetime.Token;
            }

            public async Task<byte[]> ReadAsync(CancellationToken token)
            {
                using (var combined = CancellationTokenSource.CreateLinkedTokenSource(operationToken, token))
                    return await inner.ReadAsync(combined.Token).ConfigureAwait(false);
            }

            public async Task WriteAsync(byte[] data, CancellationToken token)
            {
                using (var combined = CancellationTokenSource.CreateLinkedTokenSource(operationToken, token))
                    await inner.WriteAsync(data, combined.Token).ConfigureAwait(false);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                inner.Dispose();
                lifetime.Dispose();
            }
        }
    }
}
