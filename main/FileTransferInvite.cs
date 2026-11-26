using System;
using KimNotes.utils;

namespace KimNotes
{
    internal static class FileTransferInvite
    {
        private const int EncodedCodeLength = 43;
        private const string ShortCodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        internal const int ShortCodeLength = 8;

        internal static bool TryRead(string input, out string code)
        {
            code = null;
            string candidate = input?.Trim();
            var website = DesktopEndpoints.WebsiteRoot;
            if (website != null)
            {
                string prefix = new Uri(website, "transfer/").AbsoluteUri + "#";
                if (candidate?.StartsWith(prefix, StringComparison.Ordinal) == true)
                    candidate = candidate.Substring(prefix.Length);
            }
            if (candidate?.Length == ShortCodeLength)
            {
                candidate = candidate.ToUpperInvariant();
                foreach (char character in candidate)
                    if (ShortCodeAlphabet.IndexOf(character) < 0) return false;
            }
            else if (!IsCanonicalCode(candidate)) return false;

            code = candidate;
            return true;
        }

        internal static bool IsShortCode(string code) => code?.Length == ShortCodeLength;

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
