using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KimNotes
{
    // The UDP rendezvous sees only the hash of the invite. Peers use the invite and
    // fresh nonces to derive independent encryption and authentication keys per direction.
    internal sealed class EncryptedFileTransfer : IFileTransferConnection
    {
        private const int IvBytes = 16;
        private const int TagBytes = 32;
        private readonly IFileTransferConnection connection;
        private readonly byte[] sendCipher, sendMac, readCipher, readMac;
        private ulong sent, received;

        private EncryptedFileTransfer(IFileTransferConnection connection, byte[] sessionKey, bool sender)
        {
            this.connection = connection;
            byte sendDirection = sender ? (byte)1 : (byte)2;
            byte readDirection = sender ? (byte)2 : (byte)1;
            using (var hmac = new HMACSHA256(sessionKey))
            {
                sendCipher = hmac.ComputeHash(new[] { sendDirection, (byte)1 });
                sendMac = hmac.ComputeHash(new[] { sendDirection, (byte)2 });
                readCipher = hmac.ComputeHash(new[] { readDirection, (byte)1 });
                readMac = hmac.ComputeHash(new[] { readDirection, (byte)2 });
            }
        }

        internal static async Task<IFileTransferConnection> OpenAsync(IFileTransferConnection connection, string code, bool sender, CancellationToken token)
        {
            var local = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(local);
            await connection.WriteAsync(local, token).ConfigureAwait(false);
            byte[] remote = await FileTransferProtocol.ReadAsync(connection, token, 15).ConfigureAwait(false);
            if (remote.Length != local.Length) throw new InvalidDataException("对方的配对响应无效。");
            var salt = new byte[65];
            salt[0] = 1;
            Buffer.BlockCopy(sender ? local : remote, 0, salt, 1, 32);
            Buffer.BlockCopy(sender ? remote : local, 0, salt, 33, 32);
            byte[] secret = FileTransferP2p.DecodeCode(code);
            try
            {
                using (var hmac = new HMACSHA256(secret))
                    return new EncryptedFileTransfer(connection, hmac.ComputeHash(salt), sender);
            }
            finally { Array.Clear(secret, 0, secret.Length); }
        }

        public async Task WriteAsync(byte[] data, CancellationToken token)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = sendCipher;
                aes.GenerateIV();
                using (var cipher = aes.CreateEncryptor())
                {
                    byte[] encrypted = cipher.TransformFinalBlock(data, 0, data.Length);
                    var frame = new byte[IvBytes + encrypted.Length + TagBytes];
                    Buffer.BlockCopy(aes.IV, 0, frame, 0, IvBytes);
                    Buffer.BlockCopy(encrypted, 0, frame, IvBytes, encrypted.Length);
                    byte[] tag = Authenticate(sendMac, sent++, frame);
                    Buffer.BlockCopy(tag, 0, frame, frame.Length - TagBytes, TagBytes);
                    await connection.WriteAsync(frame, token).ConfigureAwait(false);
                }
            }
        }

        public async Task<byte[]> ReadAsync(CancellationToken token)
        {
            byte[] frame = await connection.ReadAsync(token).ConfigureAwait(false);
            if (frame.Length < 64 || (frame.Length - IvBytes - TagBytes) % IvBytes != 0)
                throw new InvalidDataException("收到无效的加密数据。");
            var actual = new byte[TagBytes];
            Buffer.BlockCopy(frame, frame.Length - TagBytes, actual, 0, TagBytes);
            if (!FileTransferProtocol.FixedEquals(Authenticate(readMac, received, frame), actual))
                throw new InvalidDataException("配对校验失败，请重新配对。");
            received++;
            var iv = new byte[IvBytes];
            Buffer.BlockCopy(frame, 0, iv, 0, IvBytes);
            using (var aes = Aes.Create())
            using (var cipher = aes.CreateDecryptor(readCipher, iv))
                return cipher.TransformFinalBlock(frame, IvBytes, frame.Length - IvBytes - TagBytes);
        }

        private static byte[] Authenticate(byte[] key, ulong sequence, byte[] frame)
        {
            var prefix = BitConverter.GetBytes(sequence);
            using (var hmac = new HMACSHA256(key))
            {
                hmac.TransformBlock(prefix, 0, prefix.Length, prefix, 0);
                hmac.TransformFinalBlock(frame, 0, frame.Length - TagBytes);
                return hmac.Hash;
            }
        }

        public void Dispose() => connection.Dispose();
    }
}
