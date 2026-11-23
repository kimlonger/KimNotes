using System;

namespace KimNotes
{
    internal static class FileTransferInvite
    {
        private const string Prefix = "https://note.kimlulu.com/transfer/#";
        private const int EncodedCodeLength = 43;

        internal static string Create(string code)
        {
            if (!IsCanonicalCode(code)) throw new ArgumentException("邀请格式不正确。", nameof(code));
            return Prefix + code;
        }

        internal static bool TryRead(string input, out string code)
        {
            code = null;
            string link = input?.Trim();
            if (link?.Length != Prefix.Length + EncodedCodeLength
                || !link.StartsWith(Prefix, StringComparison.Ordinal)) return false;

            string candidate = link.Substring(Prefix.Length);
            if (!IsCanonicalCode(candidate)) return false;

            code = candidate;
            return true;
        }

        private static bool IsCanonicalCode(string code)
        {
            if (code?.Length != EncodedCodeLength) return false;
            try
            {
                byte[] decoded = FileTransferP2p.DecodeCode(code);
                Array.Clear(decoded, 0, decoded.Length);
                return true;
            }
            catch (ArgumentException) { return false; }
        }
    }
}
