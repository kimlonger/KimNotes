using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    internal interface IFileTransferConnection : IDisposable
    {
        Task<byte[]> ReadAsync(CancellationToken token);
        Task WriteAsync(byte[] data, CancellationToken token);
    }

    internal sealed class IncomingTransfer
    {
        internal string Name { get; }
        internal string Sender { get; }
        internal long Length { get; }

        internal IncomingTransfer(string name, string sender, long length)
        {
            Name = name;
            Sender = sender;
            Length = length;
        }
    }

    internal static class FileTransferProtocol
    {
        internal const int MaxFrameBytes = 65536;
        private const int ChunkBytes = 32 * 1024;
        private const int BatchBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static async Task<bool> SendAsync(IFileTransferConnection connection, string path,
            IProgress<long> progress, CancellationToken token)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, true))
            using (var hash = SHA256.Create())
            {
                long length = input.Length;
                await connection.WriteAsync(EncodeOffer(Path.GetFileName(path), Environment.MachineName, length), token).ConfigureAwait(false);
                if (!await DecisionAsync(connection, token, 120).ConfigureAwait(false)) return false;

                long total = 0;
                int batch = 0;
                while (total < length)
                {
                    int count = (int)Math.Min(ChunkBytes, length - total);
                    var chunk = new byte[count];
                    for (int offset = 0; offset < count; )
                    {
                        int read = await input.ReadAsync(chunk, offset, count - offset, token).ConfigureAwait(false);
                        if (read == 0) throw new IOException("发送期间文件内容发生了变化。");
                        offset += read;
                    }
                    hash.TransformBlock(chunk, 0, count, chunk, 0);
                    await connection.WriteAsync(chunk, token).ConfigureAwait(false);
                    total += count;
                    batch += count;
                    if (batch == BatchBytes || total == length)
                    {
                        await ConfirmAsync(connection, token).ConfigureAwait(false);
                        progress.Report(total);
                        batch = 0;
                    }
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                await connection.WriteAsync(hash.Hash, token).ConfigureAwait(false);
                await ConfirmAsync(connection, token).ConfigureAwait(false);
                return true;
            }
        }

        internal static async Task<bool> ReceiveAsync(IFileTransferConnection connection, Func<IncomingTransfer, string> choosePath,
            IProgress<long> progress, CancellationToken token)
        {
            var offer = await ReadOfferAsync(connection, token);
            return await ReceiveAsync(connection, offer, choosePath, progress, token);
        }

        internal static async Task<IncomingTransfer> ReadOfferAsync(
            IFileTransferConnection connection, CancellationToken token, int seconds = 30) =>
            DecodeOffer(await ReadAsync(connection, token, seconds).ConfigureAwait(false));

        internal static async Task<bool> ReceiveAsync(IFileTransferConnection connection, IncomingTransfer offer,
            Func<IncomingTransfer, string> choosePath, IProgress<long> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string destination = choosePath(offer);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(destination))
            {
                await connection.WriteAsync(new byte[] { 0 }, token);
                return false;
            }
            return await Task.Run(() => SaveAsync(connection, offer.Length, destination, progress, token), token);
        }

        private static async Task<bool> SaveAsync(IFileTransferConnection connection, long length, string destination,
            IProgress<long> progress, CancellationToken token)
        {
            string target = Path.GetFullPath(destination);
            string temp = Path.Combine(Path.GetDirectoryName(target), ".kimnotes-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, true))
                using (var hash = SHA256.Create())
                {
                    await connection.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                    long total = 0;
                    int batch = 0;
                    while (total < length)
                    {
                        byte[] chunk = await ReadAsync(connection, token, 45).ConfigureAwait(false);
                        if (chunk.Length != Math.Min(ChunkBytes, length - total)) throw new InvalidDataException("收到的文件块大小无效。");
                        await output.WriteAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                        hash.TransformBlock(chunk, 0, chunk.Length, chunk, 0);
                        total += chunk.Length;
                        batch += chunk.Length;
                        if (batch == BatchBytes || total == length)
                        {
                            await connection.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                            progress.Report(total);
                            batch = 0;
                        }
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (!FixedEquals(hash.Hash, await ReadAsync(connection, token, 45).ConfigureAwait(false)))
                        throw new InvalidDataException("文件校验失败，请重新发送。");
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                // Move is atomic within the selected directory and fails if a file already exists.
                try { File.Move(temp, target); }
                catch (IOException error) when (File.Exists(target) || Directory.Exists(target))
                { throw new IOException("保存位置已出现同名文件或文件夹，请重新接收并选择其他位置。", error); }
                await connection.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                return true;
            }
            finally
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task ConfirmAsync(IFileTransferConnection connection, CancellationToken token)
        {
            if (!await DecisionAsync(connection, token, 45).ConfigureAwait(false)) throw new IOException("接收端取消了传输。");
        }

        private static async Task<bool> DecisionAsync(IFileTransferConnection connection, CancellationToken token, int seconds)
        {
            byte[] frame = await ReadAsync(connection, token, seconds).ConfigureAwait(false);
            if (frame.Length != 1 || frame[0] > 1) throw new InvalidDataException("对方返回了无效的传输状态。");
            return frame[0] == 1;
        }

        internal static async Task<byte[]> ReadAsync(IFileTransferConnection connection, CancellationToken token, int seconds)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                try { return await connection.ReadAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("等待对方响应超时。"); }
            }
        }

        private static byte[] EncodeOffer(string name, string sender, long length)
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Utf8))
            {
                writer.Write((byte)1);
                writer.Write(length);
                writer.Write(name);
                writer.Write(sender);
                writer.Flush();
                return output.ToArray();
            }
        }

        private static IncomingTransfer DecodeOffer(byte[] frame)
        {
            if (frame.Length > 2048) throw new InvalidDataException("文件信息过长。");
            using (var reader = new BinaryReader(new MemoryStream(frame, false), Utf8))
            {
                if (reader.ReadByte() != 1) throw new InvalidDataException("对方的文件传输版本不兼容。");
                long length = reader.ReadInt64();
                string name = reader.ReadString();
                string sender = reader.ReadString();
                if (length < 0 || name.Length == 0 || name.Length > 255 || name == "." || name == ".." ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".", StringComparison.Ordinal) ||
                    name.EndsWith(" ", StringComparison.Ordinal) || sender.Length > 100 || reader.BaseStream.Position != frame.Length)
                    throw new InvalidDataException("收到的文件信息无效。");
                return new IncomingTransfer(name, sender, length);
            }
        }

        internal static bool FixedEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
    }
}
