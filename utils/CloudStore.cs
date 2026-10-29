using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KimNotes.utils
{
    internal sealed class CloudSession
    {
        public string UserId, Email, Token;
        public DateTime ExpiresUtc;
        public string RefreshToken, SessionId, DeviceId, PendingRefreshRequestId, PendingRefreshToken, PendingUpgradeRequestId;
        public DateTime RefreshExpiresUtc;
        public int ProtocolVersion;
        public bool ReauthenticationRequired;

        internal CloudSession Copy() => (CloudSession)MemberwiseClone();
        internal static CloudSession FromResponse(JObject response, string deviceId = null)
        {
            var user = response["user"];
            var session = new CloudSession {
                UserId = user?.Value<string>("id"), Email = user?.Value<string>("email"), Token = response.Value<string>("token"),
                RefreshToken = response.Value<string>("refreshToken"), SessionId = response.Value<string>("sessionId"),
                ProtocolVersion = response.Value<int>("protocolVersion"), DeviceId = deviceId ?? Guid.NewGuid().ToString()
            };
            int seconds = response.Value<int>("expiresIn"), refreshSeconds = response.Value<int>("refreshExpiresIn");
            if (!Guid.TryParse(session.UserId, out _) || session.Token?.Length != 43 || seconds <= 0)
                throw new InvalidDataException("登录响应无效，请稍后重试。");
            // A legacy response must not silently downgrade a v2 desktop installation.
            if (session.ProtocolVersion != 2 || session.RefreshToken?.Length != 43 || !Guid.TryParse(session.SessionId, out _)
                || seconds > 3600 || refreshSeconds <= 0 || refreshSeconds > 366 * 86400)
                throw new InvalidDataException("服务器尚未完成桌面登录升级，请稍后重试。");
            session.ExpiresUtc = DateTime.UtcNow.AddSeconds(seconds);
            session.RefreshExpiresUtc = DateTime.UtcNow.AddSeconds(refreshSeconds);
            return session;
        }
    }
    internal sealed class CloudRecord
    {
        public string RemoteId = Guid.NewGuid().ToString(), LocalHash = "", ConflictOf;
        public long Revision;
        public bool Deleted, DeleteRequested;
        public JObject Pending;
        public string PendingHash;
        public string LocalStamp, LastError, ErrorStamp;
        // Older clients did not classify failures; null permits one automatic
        // retry after upgrading, then the actual error supplies the policy.
        public bool? RetryableError;
        public bool LocalOnlyEmpty;
        // Durable identities of the two valid stages of a remote file install:
        // native document written, then its theme sidecar written.
        public string IncomingHash, IncomingFinalHash;
    }
    internal sealed class CloudState
    {
        public string Cursor = "0";
        public bool BootstrapComplete;
        public string BootstrapCursor, BootstrapAfter;
        public Dictionary<string, CloudRecord> Notes = new Dictionary<string, CloudRecord>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Images = new Dictionary<string, string>();
        // Keep remote changes durable when one local note cannot yet be merged.
        public Dictionary<string, JObject> Deferred = new Dictionary<string, JObject>();
    }
    internal static class CloudStore
    {
        internal static string Root => Path.Combine(Storage.Root, "cloud");
        private static string SessionFile => Path.Combine(Root, "session.dat");
        internal static string UserId;
        private static Dictionary<string, string> owners;
        private static string ownersRoot;
        internal static CloudSession ReadSession()
        {
            if (!File.Exists(SessionFile) || Storage.Incognito) return null;
            var data = ProtectedData.Unprotect(File.ReadAllBytes(SessionFile), null, DataProtectionScope.CurrentUser);
            var session = JsonConvert.DeserializeObject<CloudSession>(Encoding.UTF8.GetString(data));
            if (session == null || !Guid.TryParse(session.UserId, out _) ||
                (!session.ReauthenticationRequired && (session.ProtocolVersion == 2 ? session.RefreshToken?.Length != 43 : session.Token?.Length != 43)))
                throw new InvalidDataException("账号凭证损坏，请重新登录。");
            // v2 access credentials never survive a process restart.
            if (session.ProtocolVersion == 2) session.Token = null;
            return session;
        }
        internal static void SaveSession(CloudSession session)
        {
            var value = JObject.FromObject(session);
            if (session.ProtocolVersion == 2 || session.ReauthenticationRequired) value.Remove("Token");
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.ToString(Formatting.None)), null, DataProtectionScope.CurrentUser);
            Storage.Write(SessionFile, temp => File.WriteAllBytes(temp, data));
        }
        internal static void ClearSession()
        {
            foreach (var path in new[] { SessionFile, SessionFile + ".bak" }) if (File.Exists(path)) File.Delete(path);
        }
        internal static CloudState ReadState(string id) => Storage.ReadJson(StateFile(id), () => new CloudState());
        internal static void SaveState(string id, CloudState state) => Storage.WriteText(StateFile(id), JsonConvert.SerializeObject(state));
        private static string StateFile(string id) => Path.Combine(Root, Guid.Parse(id).ToString(), "sync.json");
        private static Dictionary<string, string> Owners
        {
            get {
                if (owners == null || ownersRoot != Root) {
                    owners = Storage.ReadJson(Path.Combine(Root, "owners.json"), () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    ownersRoot = Root;
                }
                return owners;
            }
        }
        internal static bool CanShow(string path)
        {
            try { return !Owners.TryGetValue(Path.GetFileName(path), out var owner) || owner == UserId; }
            catch (Exception e) { Program.LogError(e); return false; }
        }
        internal static bool BelongsTo(string name, string user) => !Owners.TryGetValue(Path.GetFileName(name), out var owner) || owner == user;
        internal static void Claim(string name, string user)
        {
            name = Path.GetFileName(name);
            if (!BelongsTo(name, user)) throw new InvalidOperationException("此便签属于另一个账号，不能同步到当前账号。");
            if (Owners.ContainsKey(name)) return;
            var next = new Dictionary<string, string>(Owners, StringComparer.OrdinalIgnoreCase) { [name] = user };
            Storage.WriteText(Path.Combine(Root, "owners.json"), JsonConvert.SerializeObject(next));
            owners = next;
        }
        internal static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
        private static readonly object hashLock=new object();
        private static readonly Dictionary<string,Tuple<string,string>> hashes=new Dictionary<string,Tuple<string,string>>(StringComparer.OrdinalIgnoreCase);
        internal static void InvalidateHash(string path) {if(path!=null)lock(hashLock)hashes.Remove(Path.GetFullPath(path));}
        internal static string FileHash(string path)
        {
            path=Path.GetFullPath(path);
            string before=QuickStamp(path);
            lock(hashLock)if(hashes.TryGetValue(path,out var cached) && cached.Item1==before)return cached.Item2;
            string hash;
            using(var sha=SHA256.Create())
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if(before!=QuickStamp(path))throw new IOException("便签正在保存，稍后继续同步。");
            hash+=":"+Storage.Metadata(path).ThemeId;
            lock(hashLock) {if(hashes.Count>1024)hashes.Clear();hashes[path]=Tuple.Create(before,hash);}
            return hash;
        }
        internal static string QuickStamp(string path)
        {
            var info=new FileInfo(path);
            return info.Exists ? info.Length+":"+info.LastWriteTimeUtc.Ticks+":"+Storage.Metadata(path).ThemeId : "";
        }
    }
}
