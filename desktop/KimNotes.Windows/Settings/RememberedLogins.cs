using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using KimNotes.utils;
using Newtonsoft.Json;

namespace KimNotes.Windows.Settings
{
    internal sealed class RememberedLogin
    {
        public string Email { get; }
        public string Password { get; }

        [JsonConstructor]
        internal RememberedLogin(string email, string password)
        {
            Email = email;
            Password = password;
        }
    }

    internal static class RememberedLogins
    {
        private const int MaxEntries = 5;
        private static readonly object Gate = new object();
        private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("KimNotes.RememberedLogins.v1");
        private static readonly StringComparer EmailComparer = StringComparer.OrdinalIgnoreCase;
        private static string PathName => Path.Combine(Storage.Root, "cloud", "remembered-logins.dat");

        internal static IReadOnlyList<RememberedLogin> Read()
        {
            lock (Gate)
            {
                if (Storage.Incognito || !TryReadEntries(PathName, out var entries)) return Array.Empty<RememberedLogin>();
                return entries;
            }
        }

        internal static void Save(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)) return;
            email = email.Trim();
            lock (Gate)
            {
                if (Storage.Incognito) return;
                string path = PathName;
                if (!TryReadEntries(path, out var entries)) return;
                entries.RemoveAll(entry => EmailComparer.Equals(entry.Email, email));
                entries.Insert(0, new RememberedLogin(email, password));
                WriteEntries(path, entries.Take(MaxEntries));
            }
        }

        internal static void Remove(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return;
            email = email.Trim();
            lock (Gate)
            {
                if (Storage.Incognito) return;
                string path = PathName;
                if (!TryReadEntries(path, out var entries)) return;
                if (entries.RemoveAll(entry => EmailComparer.Equals(entry.Email, email)) != 0)
                    WriteEntries(path, entries);
            }
        }

        private static bool TryReadEntries(string path, out List<RememberedLogin> entries)
        {
            entries = new List<RememberedLogin>();
            byte[] decrypted = null;
            try
            {
                decrypted = ProtectedData.Unprotect(File.ReadAllBytes(path), Purpose, DataProtectionScope.CurrentUser);
                var storedEntries = JsonConvert.DeserializeObject<List<RememberedLogin>>(Encoding.UTF8.GetString(decrypted));
                entries = (storedEntries ?? Enumerable.Empty<RememberedLogin>())
                    .Where(entry => !string.IsNullOrWhiteSpace(entry?.Email) && !string.IsNullOrEmpty(entry.Password))
                    .GroupBy(entry => entry.Email, EmailComparer)
                    .Take(MaxEntries)
                    .Select(entriesForEmail => entriesForEmail.First())
                    .ToList();
                return true;
            }
            catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException ||
                error is CryptographicException || error is JsonException)
            {
                // A missing or unreadable DPAPI payload can start a new history on the next saved login.
                return true;
            }
            catch (Exception error) when (IsStorageError(error))
            {
                // A transient access failure should not allow a write to replace the existing history.
                return false;
            }
            finally
            {
                if (decrypted != null) Array.Clear(decrypted, 0, decrypted.Length);
            }
        }

        private static void WriteEntries(string path, IEnumerable<RememberedLogin> entries)
        {
            byte[] plaintext = null;
            try
            {
                plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entries));
                byte[] encrypted = ProtectedData.Protect(plaintext, Purpose, DataProtectionScope.CurrentUser);
                Storage.Write(path, temp => File.WriteAllBytes(temp, encrypted), keepBackup: false);
            }
            catch (Exception error) when (IsStorageError(error))
            {
                // Remembering a login is optional, and storage errors can carry sensitive data.
            }
            finally
            {
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        private static bool IsStorageError(Exception error) => error is IOException ||
            error is UnauthorizedAccessException || error is SecurityException ||
            error is CryptographicException || error is JsonException;
    }
}
