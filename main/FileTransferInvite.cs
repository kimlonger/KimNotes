using System;

namespace KimNotes
{
    internal static class FileTransferInvite
    {
        private const string Prefix = "https://note.kimlulu.com/transfer/#";
        private const int EncodedCodeLength = 43;

        internal static bool TryRead(string input, out string code)
        {
            code = null;
            string candidate = input?.Trim();
            if (candidate?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                candidate = candidate.Substring(Prefix.Length);
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
