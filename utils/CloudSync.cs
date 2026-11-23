using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace KimNotes.utils
{
    internal sealed class CloudSync : IDisposable
    {
        internal static CloudSync Current { get; private set; }
        internal CloudSession Session { get; private set; }
        internal byte[] AvatarImage { get; private set; }
        internal string Status { get; private set; } = "未登录";
        internal bool NeedsLogin { get; private set; }
        internal bool Busy { get; private set; }
        internal DateTime? LastSyncUtc { get; private set; }
        internal event Action Changed;
        private CloudState state;
        private readonly CloudApi api;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim authGate = new SemaphoreSlim(1, 1);
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private CancellationTokenSource running;
        private CancellationTokenSource transferLifetime = new CancellationTokenSource();
        private readonly SyncSchedule schedule=new SyncSchedule();
        private bool disposed;
        private DateTime legacyUpgradeRetryUtc;
        private int authAttempt;
        private Exception lastAuthFailure;
        private string activeFile;
        private bool retryTodos;
        private string todoStatus;
        private Guid? avatarVersion;
        private CancellationTokenSource avatarDownload;
        internal string NoteStatus(string name)
        {
            if(Session==null)return null;
            name=Path.GetFileName(name);
            if(!CloudStore.BelongsTo(name,Session.UserId))return null;
            if(NeedsLogin)return "需重新登录";
            if(state==null || !state.Notes.TryGetValue(name,out var record))return "待同步";
            string stamp=CloudStore.QuickStamp(PathOf(name));
            if(activeFile==name)return "同步中";
            if(record.LastError!=null && record.ErrorStamp==stamp)return "同步失败";
            if(record.LocalOnlyEmpty && record.LocalStamp==stamp)return "仅本地";
            return record.Revision>0 && record.Pending==null && !record.DeleteRequested && record.LocalStamp==stamp ? "已同步" : "待同步";
        }
        internal string NoteError(string name)
        {
            name=Path.GetFileName(name);
            return NoteStatus(name)=="同步失败" && state.Notes.TryGetValue(name,out var record) ? record.LastError : null;
        }
        internal string NoteSourceDevice(string name)
        {
            name = Path.GetFileName(name);
            if (Session == null || !CloudStore.BelongsTo(name, Session.UserId) ||
                state == null || !state.Notes.TryGetValue(name, out var record) || record.Revision <= 0) return null;
            return record.SourceDevice;
        }

        private static bool CaptureSourceDevice(CloudRecord record, JObject remote)
        {
            if (record.SourceDevice != null || !(remote["sourceDevice"] is JObject source)) return false;
            string kind = source.Value<string>("kind");
            if (kind == "web") record.SourceDevice = "网页端";
            if (kind == "desktop")
            {
                string name = source.Value<string>("name") ?? "";
                name = new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
                    .Take(120).ToArray()).Trim();
                if (name.Length != 0) record.SourceDevice = name;
            }
            return record.SourceDevice != null;
        }
        internal CloudSync(CloudApi api = null, bool start = true)
        {
            this.api = api ?? new CloudApi(); Current = this;
            this.api.AccessToken = cancel => EnsureAccess(null, cancel);
            this.api.RenewAccessToken = (rejected, cancel) => EnsureAccess(rejected, cancel);
            try {
                Session = CloudStore.ReadSession(); CloudStore.UserId = Session?.UserId;
                if (Session != null) {
                    state = CloudStore.ReadState(Session.UserId); NeedsLogin = Session.ReauthenticationRequired;
                    Status = NeedsLogin ? "登录已失效，本机内容已保留，请重新登录" : "已登录，等待连接云端";
                }
            } catch (Exception e) { Status = e.Message; NeedsLogin = true; }
            TodoStore.Changed += TodoChanged;
            timer.Tick += async (s, e) => {var now=DateTime.UtcNow;if(schedule.IsDue(now))await RunSync(schedule.UploadDue(now),false);};
            if (start) timer.Start();
        }
        private void Update(string status) { Status = status; if (!disposed) Changed?.Invoke(); }
        private static bool InvalidCredential(CloudApiException error) =>
            error.Code == "session_revoked" || error.Code == "refresh_expired" || error.Code == "refresh_revoked"
            || error.Code == "refresh_reused" || error.Code == "account_disabled" || error.Code == "invalid_credentials"
            || error.Code == "session_expired" || error.Code == "legacy_expired";

        internal bool CanUseAccountTransfer => TransferLoginAvailable(Session);

        private bool TransferLoginAvailable(CloudSession login) => Current == this && !disposed && !Storage.Incognito && !NeedsLogin
            && login?.ProtocolVersion == 2 && !login.ReauthenticationRequired && Guid.TryParse(login.SessionId, out _);

        internal CancellationToken AccountTransferLifetime(CloudSession expected)
        {
            var current = Session;
            if (!TransferLoginAvailable(current) || expected == null || expected.UserId != current.UserId || expected.SessionId != current.SessionId)
                throw new InvalidOperationException("请在两台电脑登录同一个小羊账号后重试。");
            return transferLifetime.Token;
        }

        internal async Task<JObject> AccountTransferRequest(HttpMethod method, string path, CloudSession expected,
            object body, CancellationToken cancel)
        {
            AccountTransferLifetime(expected);
            string token = await EnsureAccess(null, cancel);
            AccountTransferLifetime(expected);
            JObject response;
            try { response = await api.TransferJson(method, path, body, token, cancel); }
            catch (CloudApiException error) when (error.Status == 401 && error.Code == "access_expired")
            {
                AccountTransferLifetime(expected);
                token = await EnsureAccess(token, cancel);
                AccountTransferLifetime(expected);
                response = await api.TransferJson(method, path, body, token, cancel);
            }
            AccountTransferLifetime(expected);
            return response;
        }

        private void ResetAccountTransfers()
        {
            var previous = transferLifetime;
            transferLifetime = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
        }

        private void RequireLogin()
        {
            if (Session == null) return;
            var identity = Session.Copy();
            identity.Token = identity.RefreshToken = identity.PendingRefreshToken = null;
            identity.PendingRefreshRequestId = identity.PendingUpgradeRequestId = null;
            identity.ReauthenticationRequired = true;
            // Even if disk is temporarily unavailable, stop all authenticated networking.
            Session = identity; NeedsLogin = true;
            ResetAccountTransfers();
            ClearAvatar();
            CloudStore.SaveSession(identity);
            Update("登录已失效，本机内容已保留，请重新登录");
        }

        private async Task<string> EnsureAccess(string rejectedToken, CancellationToken cancel)
        {
            int observedAttempt = Volatile.Read(ref authAttempt);
            await authGate.WaitAsync(cancel);
            bool attempted = false;
            try {
                var current = Session;
                if (disposed) throw new OperationCanceledException();
                if (current == null || NeedsLogin || current.ReauthenticationRequired)
                    throw new CloudApiException(401, "请重新登录，本机内容已保留。", "session_revoked");
                bool refresh = !string.IsNullOrEmpty(current.RefreshToken);
                bool pending = !string.IsNullOrEmpty(current.PendingRefreshRequestId);
                // A concurrent request may already have rotated the rejected access token.
                if (refresh && !pending && current.Token?.Length == 43 && current.ExpiresUtc > DateTime.UtcNow.AddSeconds(45)
                    && (rejectedToken == null || rejectedToken != current.Token)) return current.Token;
                if (!refresh && current.ExpiresUtc <= DateTime.UtcNow) {
                    RequireLogin(); throw new CloudApiException(401, "原有登录已过期，请重新登录。", "legacy_expired");
                }
                if (refresh && !pending && current.RefreshExpiresUtc <= DateTime.UtcNow) {
                    RequireLogin(); throw new CloudApiException(401, "长期未连接云端，请重新登录。", "refresh_expired");
                }
                if (!refresh && legacyUpgradeRetryUtc > DateTime.UtcNow && rejectedToken == null) return current.Token;
                // Requests already waiting for one failed renewal share its result as well.
                // A later scheduled/manual sync can retry the durable pending request.
                if (observedAttempt != authAttempt && lastAuthFailure != null)
                    ExceptionDispatchInfo.Capture(lastAuthFailure).Throw();
                attempted = true; lastAuthFailure = null;
                var requested = current.Copy();
                JObject response;
                if (refresh) {
                    if (!pending) {
                        requested.PendingRefreshRequestId = Guid.NewGuid().ToString();
                        requested.PendingRefreshToken = requested.RefreshToken;
                        CloudStore.SaveSession(requested); Session = requested;
                    }
                    Session = requested;
                    response = await api.Json("/auth/desktop/refresh", new {
                        refreshToken = requested.PendingRefreshToken, requestId = requested.PendingRefreshRequestId
                    }, cancel: cancel);
                } else {
                    if (string.IsNullOrEmpty(requested.PendingUpgradeRequestId)) {
                        requested.PendingUpgradeRequestId = Guid.NewGuid().ToString();
                        requested.DeviceId = requested.DeviceId ?? Guid.NewGuid().ToString();
                        CloudStore.SaveSession(requested); Session = requested;
                    }
                    Session = requested;
                    try {
                        response = await api.Json("/auth/desktop/upgrade", new {
                            requestId = requested.PendingUpgradeRequestId, deviceName = Environment.MachineName, deviceId = requested.DeviceId
                        }, requested.Token, cancel: cancel);
                    } catch (CloudApiException error) when (error.Status == 404) {
                        // During a rolling server deployment a valid legacy identity remains usable.
                        legacyUpgradeRetryUtc = DateTime.UtcNow.AddMinutes(5);
                        if (rejectedToken != null) throw;
                        return requested.Token;
                    }
                }
                var renewed = CloudSession.FromResponse(response, requested.DeviceId);
                if (renewed.UserId != requested.UserId || refresh && renewed.SessionId != requested.SessionId) {
                    RequireLogin();
                    throw new InvalidDataException("续期账号校验不一致，已停止同步。");
                }
                cancel.ThrowIfCancellationRequested();
                if (disposed || !ReferenceEquals(Session, requested)) throw new OperationCanceledException();
                // Persist the replacement refresh token before using either replacement credential.
                CloudStore.SaveSession(renewed); Session = renewed;
                return renewed.Token;
            } catch (CloudApiException error) {
                if (attempted) lastAuthFailure = error;
                if (InvalidCredential(error)) RequireLogin();
                throw;
            } catch (Exception error) {
                if (attempted && !(error is OperationCanceledException)) lastAuthFailure = error;
                throw;
            } finally {
                if (attempted) Interlocked.Increment(ref authAttempt);
                authGate.Release();
            }
        }
        private string DirectoryPath => InitConfig.GetConfigValue("notesPath");
        private string PathOf(string name)
        {
            if (name != Path.GetFileName(name) || !name.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("同步索引中的文件名无效。");
            return Path.Combine(DirectoryPath, name);
        }
        private void Persist() => CloudStore.SaveState(Session.UserId, state);
        internal string DisplayTitle(string filename, string title) =>
            state != null && state.Notes.TryGetValue(filename, out var record) && record.ConflictOf != null ? title + "（冲突副本）" : title;
        internal void Saved(string path=null)
        {
            CloudStore.InvalidateHash(path);
            if (Session == null || Storage.Incognito || NeedsLogin) return;
            schedule.Saved(DateTime.UtcNow);
            if(!Busy)Update("本机已保存，等待同步");
            else if(!disposed)Changed?.Invoke();
        }
        private void TodoChanged() => Saved();

        internal static bool FlushOpen()
        {
            foreach (var form in Application.OpenForms.OfType<note>().ToArray()) if (!form.SaveCurrentNoteSafe()) return false;
            return true;
        }
        internal async Task SignIn(CloudSession session)
        {
            if (Storage.Incognito) throw new InvalidOperationException("无痕模式下不启用账号同步。");
            running?.Cancel(); await gate.WaitAsync();
            await authGate.WaitAsync();
            try {
                if (!FlushOpen()) throw new IOException("便签尚未保存，请先处理保存失败提示。");
                var loaded = CloudStore.ReadState(session.UserId);
                CloudStore.SaveSession(session);
                Session = session; state = loaded; CloudStore.UserId = session.UserId; NeedsLogin = false;
                ResetAccountTransfers();
                ClearAvatar();
                retryTodos = false; todoStatus = null;
                legacyUpgradeRetryUtc = DateTime.MinValue;
                lastAuthFailure = null;
                CloseOtherAccountNotes(); Storage.NotifyNotesChanged(); schedule.Reset(); Update("已登录，等待同步");
            } finally { authGate.Release(); gate.Release(); }
            await SyncNow();
        }
        private static void CloseOtherAccountNotes()
        {
            foreach (var form in Application.OpenForms.OfType<note>().ToArray())
                if (form.FilePath != null && !CloudStore.CanShow(form.FilePath)) form.Close();
        }
        internal async Task SignOut()
        {
            running?.Cancel(); await gate.WaitAsync();
            await authGate.WaitAsync();
            try {
                if (!FlushOpen()) throw new IOException("便签尚未保存，暂不能退出账号。");
                var old = Session;
                CloudStore.ClearSession(); Session = null; state = null; CloudStore.UserId = null; NeedsLogin = false;
                ResetAccountTransfers();
                ClearAvatar();
                retryTodos = false; todoStatus = null;
                schedule.Reset();
                CloseOtherAccountNotes(); Storage.NotifyNotesChanged(); Update("已退出，本地副本仍保留在原目录");
                if (old != null) {
                    using (var revoke = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        try {
                            if (old.RefreshToken?.Length == 43)
                                await api.Json("/auth/desktop/logout", new { refreshToken = old.PendingRefreshToken ?? old.RefreshToken }, cancel: revoke.Token);
                            else if (old.Token?.Length == 43) await api.Json("/auth/logout", new { }, old.Token, cancel: revoke.Token);
                        } catch (Exception) { Update("已在本机退出；网络未连接，云端设备可在官网撤销"); }
                }
            } finally { authGate.Release(); gate.Release(); }
        }
        internal void Deleted(string path)
        {
            if (Session == null || state == null || !state.Notes.TryGetValue(Path.GetFileName(path), out var record)) return;
            record.DeleteRequested = true; Persist(); schedule.Saved(DateTime.UtcNow);
            Update(NeedsLogin ? "本机删除已保存，重新登录后继续同步" : "删除操作等待同步");
        }
        internal Task SyncNow(bool retryFailures=false) => RunSync(true,retryFailures);
        private async Task RunSync(bool upload,bool retryFailures)
        {
            if (disposed || Busy || Session == null || Storage.Incognito || NeedsLogin) return;
            if (!await gate.WaitAsync(0)) return;
            Busy = true; running = new CancellationTokenSource(); var cancel = running.Token;
            TimeSpan? retryDelay=null;
            try {
                if (Application.OpenForms.OfType<note>().Any(form => form.IsImeComposing)) {
                    Update("正在输入，稍后同步"); retryDelay=TimeSpan.FromSeconds(2); return;
                }
                if (!FlushOpen()) throw new IOException("本机保存未完成，暂缓同步。");
                schedule.Begin(upload);
                Update("正在同步…");
                // Verify identity before adopting any local files into this account.
                var me = await api.Json("/auth/me", token: Session.Token, cancel: cancel);
                var user = me["user"];
                if (user?.Value<string>("id") != Session.UserId) throw new IOException("账号校验不一致，已停止同步。");
                string nickname = user.Value<string>("nickname"), avatar = user.Value<string>("avatarId");
                if (Session.Nickname != nickname || Session.AvatarId != avatar)
                {
                    Session.Nickname = nickname;
                    Session.AvatarId = avatar;
                    try { CloudStore.SaveSession(Session); }
                    catch (Exception error) { Program.LogError(error); }
                    Changed?.Invoke();
                }
                RefreshAvatar(user["avatarUrl"]);
                if (!Directory.Exists(DirectoryPath)) Directory.CreateDirectory(DirectoryPath);
                await Bootstrap(cancel);
                bool discovered = false;
                foreach (var file in System.IO.Directory.GetFiles(DirectoryPath, "*.rtf")) {
                    var name = Path.GetFileName(file);
                    if (!CloudStore.BelongsTo(name, Session.UserId)) continue;
                    if (!state.Notes.ContainsKey(name)) {
                        state.Notes[name] = new CloudRecord(); discovered = true;
                    }
                }
                if (discovered) Persist();
                foreach (var item in state.Notes.ToArray()) {
                    cancel.ThrowIfCancellationRequested();
                    var record = item.Value; var path = PathOf(item.Key);
                    string stamp=CloudStore.QuickStamp(path);
                    if(!retryFailures && record.RetryableError==false && record.LastError!=null && record.ErrorStamp==stamp && !record.DeleteRequested)continue;
                    activeFile=item.Key;Changed?.Invoke();
                    try {
                    if(record.IncomingHash!=null && !record.DeleteRequested) {
                        if(!File.Exists(path) || MatchesLocal(record,await Task.Run(()=>CloudStore.FileHash(path),cancel)))continue;
                        // A real user edit takes precedence over retrying a partial install.
                        record.IncomingHash=record.IncomingFinalHash=null;Persist();
                    }
                    if(!upload){
                        if(record.Pending!=null || record.DeleteRequested || File.Exists(path) && !record.Deleted && await Task.Run(()=>CloudStore.FileHash(path),cancel)!=record.LocalHash)
                            schedule.Discovered(DateTime.UtcNow);
                        continue;
                    }
                    if (record.Pending == null) {
                        string action = null; JObject document = null; string hash = null;
                        if (record.DeleteRequested) {
                            if (record.Revision == 0 && record.Pending == null) { record.Deleted = true; record.DeleteRequested = false; Persist(); continue; }
                            action = "delete";
                        } else if (File.Exists(path) && !record.Deleted && CloudStore.BelongsTo(item.Key, Session.UserId)) {
                            hash = await Task.Run(()=>CloudStore.FileHash(path),cancel);
                            if (hash != record.LocalHash) {
                                document = await CloudDocuments.Export(path, api, Session, state, Persist, cancel, includeEmpty: record.Revision > 0);
                                // Network image uploads may have allowed further local edits.
                                if (hash != await Task.Run(()=>CloudStore.FileHash(path),cancel)) continue;
                                if(document==null) {
                                    record.LocalHash=hash;record.LocalStamp=CloudStore.QuickStamp(path);record.LocalOnlyEmpty=true;
                                    record.LastError=record.ErrorStamp=null;record.RetryableError=false;Persist();continue;
                                }
                                record.LocalOnlyEmpty=false;
                                action = "upsert";
                            } else if(record.LocalStamp!=stamp || record.LastError!=null || record.RetryableError==true) {
                                record.LocalStamp=stamp;record.LastError=record.ErrorStamp=null;record.RetryableError=false;Persist();
                            }
                        }
                        if (action == null) continue;
                        if(action=="upsert")CloudStore.Claim(item.Key,Session.UserId);
                        record.Pending = new JObject { ["operationId"] = Guid.NewGuid().ToString(), ["noteId"] = record.RemoteId,
                            ["baseRevision"] = record.Revision, ["action"] = action };
                        if (document != null) record.Pending["document"] = document;
                        record.PendingHash = hash; Persist();
                    }
                    var response = await api.Json("/sync/push", new JObject { ["operations"] = new JArray(record.Pending.DeepClone()) }, Session.Token, cancel: cancel);
                    var result = (JObject)response["results"]?[0] ?? throw new IOException("同步响应无效。");
                    var remote = (JObject)result["note"];
                    string outcome = result.Value<string>("status");
                    if (outcome == "conflict") {
                        record.DeleteRequested = false; record.Pending = null;
                        if (File.Exists(path)) { Persist(); throw new IOException("便签有新的本机修改，稍后保留冲突副本。"); }
                        // A stale delete must preserve the newer cloud note, including locally.
                        record.LocalHash = ""; record.Revision = 0; Persist();
                        await Apply(remote, cancel, true); continue;
                    }
                    if (record.Pending.Value<string>("action") == "upsert") {
                        record.LocalHash = record.PendingHash;
                        record.LocalStamp=File.Exists(path) && record.LocalHash==await Task.Run(()=>CloudStore.FileHash(path),cancel) ? CloudStore.QuickStamp(path) : null;
                    }
                    if (outcome == "conflict_copy") {
                        record.ConflictOf = record.RemoteId;
                        record.SourceDevice = null;
                    }
                    record.RemoteId = remote.Value<string>("id"); record.Revision = remote.Value<long>("revision");
                    CaptureSourceDevice(record, remote);
                    record.Deleted = remote["deletedAt"]?.Type == JTokenType.String || remote["deletedAt"]?.Type == JTokenType.Date;
                    if (record.Pending.Value<string>("action") == "delete") record.DeleteRequested = false;
                    record.Pending = null; record.PendingHash = null;record.LastError=record.ErrorStamp=null;record.RetryableError=false;
                    record.IncomingHash=record.IncomingFinalHash=null;Persist();
                    // The push cursor is deliberately ignored: other devices' unread events remain in pull.
                    } catch(Exception error) when(NoteFailure(error)) {
                        record.LastError=FailureMessage(error);record.ErrorStamp=stamp;
                        record.RetryableError=error is IOException && !(error is InvalidDataException);
                        // A rejected operation was never committed; rebuild it after edits or manual retry.
                        if(error is CloudApiException) {record.Pending=null;record.PendingHash=null;}
                        Persist();
                    } finally {activeFile=null;Changed?.Invoke();}
                }
                foreach(var deferred in state.Deferred.Values.ToArray())await Receive(deferred,cancel);
                bool more;
                do {
                    var response = await api.Json("/sync/pull?cursor=" + Uri.EscapeDataString(state.Cursor) + "&limit=20", token: Session.Token, cancel: cancel);
                    foreach (JObject change in response["changes"]) await Receive(change, cancel);
                    string cursor=response.Value<string>("cursor");more=response.Value<bool>("hasMore");
                    if(state.Cursor!=cursor){state.Cursor=cursor;Persist();}
                } while (more);
                if (upload || retryTodos) {
                    try {
                        todoStatus = await PushTodos(cancel);
                        retryTodos = false;
                    }
                    catch (Exception error) when (!cancel.IsCancellationRequested && !(error is CloudApiException failed && InvalidCredential(failed))) {
                        Program.LogError(error);
                        retryTodos = true;
                        todoStatus = "待办同步未完成，本机已保留，稍后重试";
                    }
                }
                LastSyncUtc = DateTime.UtcNow;
                int failures=state.Notes.Count(p=>p.Value.LastError!=null && p.Value.ErrorStamp==CloudStore.QuickStamp(PathOf(p.Key)));
                if(state.Notes.Values.Any(r=>r.LastError!=null && r.RetryableError==true))retryDelay=TimeSpan.FromSeconds(30);
                string status = failures>0 ? failures+" 条便签同步失败，其他便签已同步" : state.Deferred.Count>0 ? "部分便签正在编辑，稍后继续同步" : schedule.HasPendingUpload ? "本机已保存，等待同步" : "已同步";
                Update(todoStatus == null ? status : status + "；" + todoStatus);
            } catch (OperationCanceledException) { if (!disposed) Update("本机内容已保留，稍后继续同步"); }
            catch (CloudApiException e) {
                if (InvalidCredential(e)) {
                    RequireLogin(); Update("登录已失效，本机内容已保留，请重新登录");
                }
                else if (e.Code == "access_expired") Update("连接云端暂未完成，本机已保存，稍后继续同步");
                else Update("同步未完成：" + e.Message);
                retryDelay=TimeSpan.FromSeconds(e.Status==429 ? 120 : 30);
            } catch (Exception e) { Update("同步未完成，本机内容已保留：" + e.Message); retryDelay=TimeSpan.FromSeconds(30); }
            finally {schedule.Complete(DateTime.UtcNow,retryDelay);activeFile=null;Busy = false; running.Dispose(); running = null; gate.Release(); if (!disposed) Changed?.Invoke(); }
        }

        private void RefreshAvatar(JToken url)
        {
            var version = CloudApi.AvatarVersion(url?.Type == JTokenType.String ? (string)url : null);
            if (avatarVersion != version)
            {
                bool hadImage = AvatarImage != null;
                ClearAvatar();
                avatarVersion = version;
                if (hadImage) Changed?.Invoke();
            }
            if (!version.HasValue || AvatarImage != null || avatarDownload != null || NeedsLogin ||
                disposed || string.IsNullOrEmpty(Session?.Token)) return;

            avatarDownload = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _ = LoadAvatar(version.Value, Session.Token, avatarDownload);
        }

        private async Task LoadAvatar(Guid version, string token, CancellationTokenSource download)
        {
            try
            {
                var data = await api.DownloadAvatar(version, token, download.Token);
                if (download.IsCancellationRequested || !ReferenceEquals(avatarDownload, download)) return;
                AvatarImage = data;
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!download.IsCancellationRequested) Program.LogError(error); }
            finally
            {
                if (ReferenceEquals(avatarDownload, download)) avatarDownload = null;
                download.Dispose();
            }
        }

        private void ClearAvatar()
        {
            var previous = avatarDownload;
            avatarDownload = null;
            avatarVersion = null;
            AvatarImage = null;
            previous?.Cancel();
        }

        private async Task<string> PushTodos(CancellationToken cancel)
        {
            const int textLimit = 256 * 1024;
            const int batchLimit = 3 * 1024 * 1024;
            string owner = Session.UserId;
            if (!TodoStore.TryFlush()) throw new IOException("待办尚未保存，暂缓同步。");
            if (!TodoStore.HasPendingCloudChanges(owner)) return null;
            if (!await api.SupportsTodoSync(cancel)) return "当前服务暂不支持待办同步";
            cancel.ThrowIfCancellationRequested();

            var batch = new JArray();
            var sent = new List<TodoItem>();
            int size = 0, skipped = 0;
            foreach (var item in TodoStore.PrepareCloudChanges(owner))
            {
                JObject change;
                if (item.DeletedAt.HasValue)
                {
                    change = JObject.FromObject(new {
                        id = item.CloudId, revision = item.CloudRevision, deletedAt = item.DeletedAt.Value.ToUniversalTime()
                    });
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(item.Text) || item.Text.IndexOf('\0') >= 0 ||
                        Encoding.UTF8.GetByteCount(item.Text) > textLimit)
                    {
                        skipped++;
                        continue;
                    }
                    change = JObject.FromObject(new {
                        id = item.CloudId, revision = item.CloudRevision, text = item.Text,
                        createdAt = item.Created == default(DateTime) ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) : item.Created.ToUniversalTime(),
                        dueAt = item.Due?.ToUniversalTime(), done = item.Done, doneAt = item.DoneAt?.ToUniversalTime(),
                        repeat = (int)item.Repeat
                    });
                }
                int bytes = Encoding.UTF8.GetByteCount(change.ToString(Newtonsoft.Json.Formatting.None)) + 1;
                if (sent.Count == 50 || size + bytes > batchLimit)
                {
                    await PushTodoBatch(batch, sent, owner, cancel);
                    batch = new JArray();
                    sent.Clear();
                    size = 0;
                }
                batch.Add(change);
                sent.Add(item);
                size += bytes;
            }
            if (sent.Count > 0) await PushTodoBatch(batch, sent, owner, cancel);
            return skipped == 0 ? null : skipped + " 条待办超出云端内容限制或为空，本机已保留";
        }

        private async Task PushTodoBatch(JArray batch, IReadOnlyList<TodoItem> sent, string owner, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var response = await api.Json("/todos", new JObject { ["todos"] = batch }, Session.Token, cancel: cancel);
            cancel.ThrowIfCancellationRequested();
            if (response.Value<bool?>("ok") != true) throw new InvalidDataException("待办同步响应无效。");
            TodoStore.AcknowledgeCloudChanges(owner, sent);
        }

        private async Task Bootstrap(CancellationToken cancel)
        {
            if(state.BootstrapComplete || state.Cursor!="0")return;
            if(!await api.SupportsBootstrap(cancel)){state.BootstrapComplete=true;Persist();return;}
            Update("正在下载云端便签…");
            bool more;
            do{
                string query="/sync/bootstrap?limit=20";
                if(state.BootstrapCursor!=null)query+="&cursor="+Uri.EscapeDataString(state.BootstrapCursor);
                if(state.BootstrapAfter!=null)query+="&after="+Uri.EscapeDataString(state.BootstrapAfter);
                var response=await api.Json(query,token:Session.Token,cancel:cancel);
                string cursor=response.Value<string>("cursor"),after=response.Value<string>("nextAfter");
                if(!long.TryParse(cursor,out var sequence) || sequence<0 || state.BootstrapCursor!=null && state.BootstrapCursor!=cursor)
                    throw new InvalidDataException("云端初次同步检查点无效。");
                more=response.Value<bool>("hasMore");
                if(more && (!Guid.TryParseExact(after,"D",out _) || after==state.BootstrapAfter))throw new InvalidDataException("云端初次同步分页无效。");
                if(state.BootstrapCursor==null){state.BootstrapCursor=cursor;Persist();}
                foreach(JObject note in response["notes"] as JArray ?? throw new InvalidDataException("云端便签列表无效。"))await Receive(note,cancel);
                state.BootstrapAfter=more ? after : null;
                if(!more){state.Cursor=cursor;state.BootstrapComplete=true;state.BootstrapCursor=null;}
                Persist();
            }while(more);
            Update("正在同步…");
        }
        private static bool NoteFailure(Exception error) => error is InvalidDataException || error is IOException || error is CloudApiException apiError && (apiError.Status==400 || apiError.Status==413 || apiError.Status==422);
        private static string FailureMessage(Exception error)
        {
            if(error is CloudApiException apiError && apiError.Status==413)return "图片或便签超过云端大小限制；本机原图已保留。";
            return error.Message;
        }
        private static bool MatchesLocal(CloudRecord record,string hash) => hash==record.LocalHash ||
            record.IncomingHash!=null && (hash==record.IncomingHash || hash==record.IncomingFinalHash);
        private async Task<bool> CanInstall(string path,CloudRecord record,CancellationToken cancel)
        {
            if(!FlushOpen() || record.Pending!=null || record.DeleteRequested)return false;
            string stamp=CloudStore.QuickStamp(path);
            string hash=File.Exists(path) ? await Task.Run(()=>CloudStore.FileHash(path),cancel) : null;
            // Hashing yields to the message loop. Flush once more, then compare the
            // exact file generation and delete intent without another asynchronous gap.
            return FlushOpen() && stamp==CloudStore.QuickStamp(path) && record.Pending==null && !record.DeleteRequested &&
                (hash==null || MatchesLocal(record,hash));
        }
        private async Task Receive(JObject remote,CancellationToken cancel)
        {
            string id=Guid.Parse(remote.Value<string>("id")).ToString();
            if(state.Deferred.TryGetValue(id,out var newer) && newer.Value<long>("revision")>remote.Value<long>("revision"))remote=newer;
            bool changed=false;
            try {await Apply(remote,cancel);changed=state.Deferred.Remove(id);}
            catch(Exception error) when(NoteFailure(error)) {
                if(!state.Deferred.TryGetValue(id,out var old) || !JToken.DeepEquals(remote,old)) {state.Deferred[id]=(JObject)remote.DeepClone();changed=true;}
                var record=state.Notes.Values.FirstOrDefault(r=>r.RemoteId==id);
                if(record!=null && (error is InvalidDataException || error is CloudApiException)) {
                    string name=state.Notes.First(p=>p.Value==record).Key;
                    string message=FailureMessage(error),stamp=CloudStore.QuickStamp(PathOf(name));
                    if(record.LastError!=message || record.ErrorStamp!=stamp || record.RetryableError!=false) {
                        record.LastError=message;record.ErrorStamp=stamp;record.RetryableError=false;changed=true;
                    }
                }
            }
            if(changed)Persist();
        }
        private async Task Apply(JObject remote, CancellationToken cancel, bool force = false)
        {
            string id = Guid.Parse(remote.Value<string>("id")).ToString();
            var item = state.Notes.FirstOrDefault(pair => pair.Value.RemoteId == id);
            var name = item.Key ?? "cloud-" + id + ".rtf";
            if (item.Key == null && (state.Notes.ContainsKey(name) || File.Exists(PathOf(name))))
                name = "cloud-" + id + "-" + Guid.NewGuid().ToString("N") + ".rtf";
            var record = item.Value ?? new CloudRecord { RemoteId = id };
            long revision = remote.Value<long>("revision");
            if (revision <= record.Revision && !force) {
                if (CaptureSourceDevice(record, remote)) { Persist(); Changed?.Invoke(); }
                return;
            }
            var path = PathOf(name);
            foreach (var opened in Application.OpenForms.OfType<note>().Where(n => n.FilePath == Path.GetFullPath(path)).ToArray())
                if (!opened.SaveCurrentNoteSafe()) throw new IOException("打开的便签尚未保存，暂缓接收云端版本。");
            // Never overwrite edits typed while an HTTP request was in flight.
            if (!force && !await CanInstall(path,record,cancel))
                throw new IOException("便签仍有本机修改，稍后合并云端版本。");
            bool deleted = remote["deletedAt"] != null && remote["deletedAt"].Type != JTokenType.Null;
            if (deleted) {
                if (File.Exists(path)) {
                    var backup = Path.Combine(CloudStore.Root, Session.UserId, "recycle", id + "-" + revision + ".rtf");
                    Storage.Write(backup, temp => File.Copy(path, temp));
                    File.Delete(path);
                }
                foreach (var opened in Application.OpenForms.OfType<note>().Where(n => n.FilePath == Path.GetFullPath(path)).ToArray()) opened.DiscardAndClose();
            } else {
                var document = (JObject)remote["document"];
                string rtf = await CloudDocuments.Import(document, api, Session.Token, cancel);
                var native=await CloudDocuments.NativeBytes(rtf,cancel);
                // Downloads may have allowed a user edit. Check again before touching disk.
                string contentHash=await Task.Run(()=>CloudStore.Hash(native),cancel);
                if (!await CanInstall(path,record,cancel))
                    throw new IOException("便签正在编辑，稍后合并云端版本。");
                CloudStore.Claim(name, Session.UserId);
                record.IncomingHash=contentHash+":"+Storage.Metadata(path).ThemeId;
                record.IncomingFinalHash=contentHash+":"+document.Value<int>("themeId");
                state.Notes[name]=record;state.Deferred[id]=(JObject)remote.DeepClone();Persist();
                Storage.Write(path,temp=>File.WriteAllBytes(temp,native));
                var metadata = Storage.Metadata(path); metadata.ThemeId = document.Value<int>("themeId");
                Storage.WriteText(path + ".meta.json", Newtonsoft.Json.JsonConvert.SerializeObject(metadata));
                foreach (var opened in Application.OpenForms.OfType<note>().Where(n => n.FilePath == Path.GetFullPath(path)).ToArray()) opened.ReloadCloud();
                CloudStore.InvalidateHash(path);
                record.LocalHash = record.IncomingFinalHash;
                record.LocalStamp=CloudStore.QuickStamp(path);record.LastError=record.ErrorStamp=null;record.RetryableError=false;record.LocalOnlyEmpty=false;
            }
            record.IncomingHash=record.IncomingFinalHash=null;
            record.Revision = revision; record.Deleted = deleted; record.ConflictOf = remote.Value<string>("conflictOf");
            CaptureSourceDevice(record, remote);
            state.Notes[name] = record; Persist(); Storage.NotifyNotesChanged();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; TodoStore.Changed -= TodoChanged;
            transferLifetime.Cancel(); transferLifetime.Dispose();
            ClearAvatar();
            timer.Stop(); timer.Dispose(); running?.Cancel(); api.Dispose();
            if (Current == this) Current = null;
        }
    }
}
