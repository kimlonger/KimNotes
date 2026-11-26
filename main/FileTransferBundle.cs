using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    // Short-code sessions use this manifest and a single decision for the entire selection.
    // Legacy invitations keep the single-file protocol.
    internal sealed class FileTransferBundle
    {
        private const int MaxEntries = 10000;
        private const int MaxDepth = 64;
        private const int MaxRelativePath = 4096;
        private const int MaxManifestBytes = 4 * 1024 * 1024;
        private const int ChunkBytes = 32 * 1024;
        private const int BatchBytes = 1024 * 1024;
        private static readonly byte[] HeaderMagic = { (byte)'K', (byte)'N', (byte)'B', 1 };
        private static readonly byte[] Accept = { 1 };
        private static readonly byte[] Refuse = { 0 };
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly char[] UnsafeNameChars = { '"', '<', '>', '|', ':', '*', '?', '\\', '/' };

        private readonly bool outgoing;
        internal string Sender { get; }
        internal string Name => Roots.Count == 1 ? Roots[0] : $"{Roots[0]} 等 {Roots.Count} 项";
        internal long Length { get; }
        internal int FileCount { get; }
        internal int FolderCount => Entries.Count - FileCount;
        internal bool IsSingleFile => Entries.Count == 1 && FileCount == 1;
        internal IReadOnlyList<string> Roots { get; }
        internal IReadOnlyList<Entry> Entries { get; }

        internal sealed class Entry
        {
            internal string Path { get; }
            internal bool IsDirectory { get; }
            internal long Length { get; }
            internal string Source { get; }

            internal Entry(string path, bool isDirectory, long length, string source = null)
            {
                Path = path;
                IsDirectory = isDirectory;
                Length = length;
                Source = source;
            }
        }

        private FileTransferBundle(string sender, List<Entry> entries, bool outgoing)
        {
            if (string.IsNullOrWhiteSpace(sender) || sender.Length > 100 || sender.Any(char.IsControl) ||
                entries.Count < 1 || entries.Count > MaxEntries)
                throw new InvalidDataException("文件清单无效。");

            this.outgoing = outgoing;
            Sender = sender;
            Entries = new ReadOnlyCollection<Entry>(entries);
            var names = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<string>();
            foreach (var entry in entries)
            {
                var parts = entry.Path.Split('/');
                if (entry.Path.Length > MaxRelativePath || parts.Length > MaxDepth || parts.Any(part => !ValidSegment(part)) ||
                    entry.Length < 0 || entry.Length > long.MaxValue - Length || (entry.IsDirectory && entry.Length != 0))
                    throw new InvalidDataException("文件清单包含无效名称或大小。");
                if (names.ContainsKey(entry.Path))
                    throw new InvalidDataException("选中的项目存在同名文件或文件夹，请分开发送。");

                int separator = entry.Path.LastIndexOf('/');
                if (separator < 0) roots.Add(entry.Path);
                else if (!names.TryGetValue(entry.Path.Substring(0, separator), out var parent) || !parent.IsDirectory ||
                         !string.Equals(parent.Path, entry.Path.Substring(0, separator), StringComparison.Ordinal))
                    throw new InvalidDataException("文件清单中的目录结构无效。");

                names.Add(entry.Path, entry);
                Length += entry.Length;
                if (!entry.IsDirectory) FileCount++;
            }
            Roots = new ReadOnlyCollection<string>(roots);
        }

        internal static FileTransferBundle Create(IEnumerable<string> paths, CancellationToken token = default)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            var entries = new List<Entry>();
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(path))
                    throw new IOException("请选择要发送的文件或文件夹。");
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(full);
                string parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent) || !ValidSegment(name))
                    throw new IOException("请选择磁盘中的文件或文件夹。");
                CheckDirectoryChain(parent);
                AddSelection(full, name, 1, entries, token);
            }
            if (entries.Count == 0) throw new IOException("请选择要发送的文件或文件夹。");
            return new FileTransferBundle(Environment.MachineName, entries, true);
        }

        private static void AddSelection(string full, string relative, int depth, List<Entry> entries, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= MaxEntries || depth > MaxDepth || relative.Length > MaxRelativePath)
                throw new IOException("选中的目录层级或文件数量过多，请分开发送。");

            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("选中内容包含链接或未下载的文件，请先在本机准备完整文件。");
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            entries.Add(new Entry(relative, isDirectory, isDirectory ? 0 : new FileInfo(full).Length, full));
            if (!isDirectory) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(full))
            {
                string name = Path.GetFileName(child);
                if (!ValidSegment(name)) throw new IOException("目录中含有无法传输的文件名。");
                AddSelection(child, relative + "/" + name, depth + 1, entries, token);
            }
        }

        internal void ValidateDestination(string directory)
        {
            string selected = Path.GetFullPath(directory);
            CheckDirectoryChain(selected);
            if (Roots.Count == 1 && Exists(Path.Combine(selected, Roots[0])))
                throw new IOException("保存位置已有同名文件或文件夹，请选择其他位置。");
        }

        internal static async Task<FileTransferBundle> ReadOfferAsync(IFileTransferConnection connection, CancellationToken token, int seconds = 30)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                try
                {
                    byte[] header = await connection.ReadAsync(deadline.Token).ConfigureAwait(false);
                    if (header.Length != 8 || !HeaderMagic.SequenceEqual(header.Take(4)))
                        throw new InvalidDataException("对方的文件传输版本不兼容。");
                    int length = BitConverter.ToInt32(header, 4);
                    if (length < 1 || length > MaxManifestBytes) throw new InvalidDataException("文件清单过大。");
                    var body = new byte[length];
                    int position = 0;
                    while (position < length)
                    {
                        byte[] frame = await connection.ReadAsync(deadline.Token).ConfigureAwait(false);
                        int count = Math.Min(ChunkBytes, length - position);
                        if (frame.Length != count) throw new InvalidDataException("文件清单不完整。");
                        Buffer.BlockCopy(frame, 0, body, position, count);
                        position += count;
                        if (position < length && position % BatchBytes == 0)
                            await connection.WriteAsync(Accept, deadline.Token).ConfigureAwait(false);
                    }
                    return ParseOffer(body);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("等待对方发送文件清单超时。");
                }
            }
        }

        internal static async Task<bool> SendAsync(IFileTransferConnection connection, FileTransferBundle bundle,
            IProgress<long> progress, CancellationToken token)
        {
            if (!bundle.outgoing) throw new InvalidOperationException("请选择本机文件。");
            token.ThrowIfCancellationRequested();
            byte[] body = bundle.EncodeOffer();
            byte[] header = new byte[8];
            Buffer.BlockCopy(HeaderMagic, 0, header, 0, HeaderMagic.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, header, HeaderMagic.Length, sizeof(int));
            await connection.WriteAsync(header, token).ConfigureAwait(false);
            for (int position = 0; position < body.Length; position += ChunkBytes)
            {
                int count = Math.Min(ChunkBytes, body.Length - position);
                var frame = new byte[count];
                Buffer.BlockCopy(body, position, frame, 0, count);
                await connection.WriteAsync(frame, token).ConfigureAwait(false);
                int sentBytes = position + count;
                if (sentBytes < body.Length && sentBytes % BatchBytes == 0)
                    await ConfirmAsync(connection, token).ConfigureAwait(false);
            }
            if (!await ReadDecisionAsync(connection, token, FileTransferProtocol.OfferDecisionTimeoutSeconds).ConfigureAwait(false))
                return false;
            progress?.Report(0);

            long sent = 0;
            foreach (var entry in bundle.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;
                CheckDirectoryChain(Path.GetDirectoryName(entry.Source));
                var attributes = File.GetAttributes(entry.Source);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("发送前文件发生了变化，请重新选择。");
                using (var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, true))
                {
                    if (input.Length != entry.Length) throw new IOException("发送前文件大小发生了变化，请重新选择。");
                    await SendFileAsync(connection, input, entry.Length, sent, progress, token).ConfigureAwait(false);
                }
                sent += entry.Length;
            }
            await ConfirmAsync(connection, token).ConfigureAwait(false);
            return true;
        }

        internal static async Task<string> ReceiveAsync(IFileTransferConnection connection, FileTransferBundle offer,
            string directory, IProgress<long> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(directory))
            {
                await connection.WriteAsync(Refuse, token).ConfigureAwait(false);
                return null;
            }

            string selected = Path.GetFullPath(directory);
            offer.ValidateDestination(selected);
            string stage = Path.Combine(selected, ".kimnotes-" + Guid.NewGuid().ToString("N") + ".part");
            if (Exists(stage)) throw new IOException("无法创建临时接收目录，请重试。");
            Directory.CreateDirectory(stage);
            var created = new List<(string Path, bool IsDirectory)>();
            string saved = null;
            try
            {
                await connection.WriteAsync(Accept, token).ConfigureAwait(false);
                progress?.Report(0);
                long received = 0;
                foreach (var entry in offer.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string target = Path.Combine(stage, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    CheckDirectoryChain(Path.GetDirectoryName(target));
                    if (entry.IsDirectory)
                    {
                        if (Exists(target)) throw new IOException("接收文件夹中有名称冲突，请重新选择。");
                        Directory.CreateDirectory(target);
                        created.Add((target, true));
                        continue;
                    }

                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, true))
                    {
                        created.Add((target, false));
                        await ReceiveFileAsync(connection, output, entry.Length, received, progress, token).ConfigureAwait(false);
                    }
                    received += entry.Length;
                    await connection.WriteAsync(Accept, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                offer.ValidateDestination(selected);
                CheckDirectoryChain(stage);
                saved = offer.Publish(stage, selected);
                // The complete selection is already saved. A dropped receipt must not hide it.
                try { await connection.WriteAsync(Accept, token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException) { }
                return saved;
            }
            finally
            {
                if (saved == null) RemoveTemporaryFiles(stage, created);
                else if (offer.Roots.Count == 1) RemoveTemporaryFiles(stage, Array.Empty<(string, bool)>());
            }
        }

        private static async Task SendFileAsync(IFileTransferConnection connection, FileStream input, long length,
            long previous, IProgress<long> progress, CancellationToken token)
        {
            using (var hash = SHA256.Create())
            {
                long sent = 0;
                int batch = 0;
                while (sent < length)
                {
                    int count = (int)Math.Min(ChunkBytes, length - sent);
                    var chunk = new byte[count];
                    for (int offset = 0; offset < count;)
                    {
                        int read = await input.ReadAsync(chunk, offset, count - offset, token).ConfigureAwait(false);
                        if (read == 0) throw new IOException("发送期间文件内容发生了变化。");
                        offset += read;
                    }
                    hash.TransformBlock(chunk, 0, count, chunk, 0);
                    await connection.WriteAsync(chunk, token).ConfigureAwait(false);
                    sent += count;
                    batch += count;
                    if (batch == BatchBytes || sent == length)
                    {
                        await ConfirmAsync(connection, token).ConfigureAwait(false);
                        progress?.Report(previous + sent);
                        batch = 0;
                    }
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                await connection.WriteAsync(hash.Hash, token).ConfigureAwait(false);
                await ConfirmAsync(connection, token).ConfigureAwait(false);
            }
        }

        private static async Task ReceiveFileAsync(IFileTransferConnection connection, FileStream output, long length,
            long previous, IProgress<long> progress, CancellationToken token)
        {
            using (var hash = SHA256.Create())
            {
                long received = 0;
                int batch = 0;
                while (received < length)
                {
                    byte[] chunk = await FileTransferProtocol.ReadAsync(connection, token, 45).ConfigureAwait(false);
                    if (chunk.Length != Math.Min(ChunkBytes, length - received))
                        throw new InvalidDataException("收到的文件块大小无效。");
                    await output.WriteAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                    hash.TransformBlock(chunk, 0, chunk.Length, chunk, 0);
                    received += chunk.Length;
                    batch += chunk.Length;
                    if (batch == BatchBytes || received == length)
                    {
                        await connection.WriteAsync(Accept, token).ConfigureAwait(false);
                        progress?.Report(previous + received);
                        batch = 0;
                    }
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                byte[] checksum = await FileTransferProtocol.ReadAsync(connection, token, 45).ConfigureAwait(false);
                if (!FileTransferProtocol.FixedEquals(hash.Hash, checksum))
                    throw new InvalidDataException("文件校验失败，请重新发送。");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task ConfirmAsync(IFileTransferConnection connection, CancellationToken token)
        {
            if (!await ReadDecisionAsync(connection, token, 45).ConfigureAwait(false))
                throw new IOException("接收端取消了传输。");
        }

        private static async Task<bool> ReadDecisionAsync(IFileTransferConnection connection, CancellationToken token, int seconds)
        {
            byte[] frame = await FileTransferProtocol.ReadAsync(connection, token, seconds).ConfigureAwait(false);
            if (frame.Length != 1 || frame[0] > 1) throw new InvalidDataException("对方返回了无效的传输状态。");
            return frame[0] == 1;
        }

        private byte[] EncodeOffer()
        {
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Utf8))
            {
                writer.Write(Sender);
                writer.Write(Entries.Count);
                foreach (var entry in Entries)
                {
                    writer.Write(entry.Path);
                    writer.Write(entry.IsDirectory ? (byte)1 : (byte)0);
                    writer.Write(entry.Length);
                    if (output.Length > MaxManifestBytes) throw new IOException("文件清单过大，请分开发送。");
                }
                writer.Flush();
                return output.ToArray();
            }
        }

        private static FileTransferBundle ParseOffer(byte[] body)
        {
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(body, false), Utf8))
                {
                    string sender = reader.ReadString();
                    int count = reader.ReadInt32();
                    if (count < 1 || count > MaxEntries) throw new InvalidDataException("文件清单中的项目数量无效。");
                    var entries = new List<Entry>(count);
                    for (int i = 0; i < count; i++)
                    {
                        string path = reader.ReadString();
                        byte kind = reader.ReadByte();
                        if (kind > 1) throw new InvalidDataException("收到无法识别的文件类型。");
                        entries.Add(new Entry(path, kind == 1, reader.ReadInt64()));
                    }
                    if (reader.BaseStream.Position != body.Length) throw new InvalidDataException("文件清单无效。");
                    return new FileTransferBundle(sender, entries, false);
                }
            }
            catch (Exception error) when (error is EndOfStreamException || error is FormatException || error is DecoderFallbackException)
            {
                throw new InvalidDataException("文件清单无效。", error);
            }
        }

        private string Publish(string stage, string selected)
        {
            if (Roots.Count == 1)
            {
                string source = Path.Combine(stage, Roots[0]);
                string target = Path.Combine(selected, Roots[0]);
                try
                {
                    if (IsSingleFile) File.Move(source, target);
                    else Directory.Move(source, target);
                }
                catch (IOException error) when (Exists(target))
                {
                    throw new IOException("保存位置已有同名文件或文件夹，请重新接收。", error);
                }
                return IsSingleFile ? selected : target;
            }

            for (int suffix = 1; suffix <= 1000; suffix++)
            {
                string target = Path.Combine(selected, suffix == 1 ? "小羊互传" : $"小羊互传 ({suffix})");
                if (Exists(target)) continue;
                try
                {
                    Directory.Move(stage, target);
                    return target;
                }
                catch (IOException) when (Exists(target)) { }
            }
            throw new IOException("保存位置的同名文件夹过多，请选择其他目录。");
        }

        private static bool ValidSegment(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name == "." || name == ".." ||
                name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) ||
                name.IndexOfAny(UnsafeNameChars) >= 0 || name.Any(char.IsControl))
                return false;
            int dot = name.IndexOf('.');
            string stem = (dot < 0 ? name : name.Substring(0, dot)).TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                stem == "CONIN$" || stem == "CONOUT$") return false;
            return stem.Length != 4 || !(stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) ||
                "123456789¹²³".IndexOf(stem[3]) < 0;
        }

        private static void CheckDirectoryChain(string path)
        {
            var chain = new Stack<string>();
            for (string current = path; current != null; current = Path.GetDirectoryName(current)) chain.Push(current);
            foreach (string current in chain)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("文件路径经过链接或挂载点，请选择实际目录。");
            }
        }

        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static void RemoveTemporaryFiles(string stage, IReadOnlyList<(string Path, bool IsDirectory)> created)
        {
            for (int i = created.Count - 1; i >= 0; i--)
            {
                var item = created[i];
                try
                {
                    CheckDirectoryChain(Path.GetDirectoryName(item.Path));
                    if (item.IsDirectory) Directory.Delete(item.Path);
                    else File.Delete(item.Path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try
            {
                CheckDirectoryChain(stage);
                Directory.Delete(stage);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
