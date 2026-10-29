using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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
        private DateTime next = DateTime.UtcNow.AddSeconds(2);
        private bool disposed;
        private DateTime legacyUpgradeRetryUtc;
        private int authAttempt;
        private Exception lastAuthFailure;
        private string activeFile;
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
            timer.Tick += async (s, e) => { if (DateTime.UtcNow >= next) await SyncNow(); };
            if (start) timer.Start();
        }
        private void Update(string status) { Status = status; if (!disposed) Changed?.Invoke(); }
        private static bool InvalidCredential(CloudApiException error) =>
            error.Code == "session_revoked" || error.Code == "refresh_expired" || error.Code == "refresh_revoked"
            || error.Code == "refresh_reused" || error.Code == "account_disabled" || error.Code == "invalid_credentials"
            || error.Code == "session_expired" || error.Code == "legacy_expired";

        private void RequireLogin()
        {
            if (Session == null) return;
            var identity = Session.Copy();
            identity.Token = identity.RefreshToken = identity.PendingRefreshToken = null;
            identity.PendingRefreshRequestId = identity.PendingUpgradeRequestId = null;
            identity.ReauthenticationRequired = true;
            // Even if disk is temporarily unavailable, stop all authenticated networking.
            Session = identity; NeedsLogin = true;
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
                if (InvalidCredential(error) || error.Status == 401 && error.Code != "access_expired") RequireLogin();
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
            next = DateTime.UtcNow.AddSeconds(3);
            if(!Busy)Update("本机已保存，等待同步");
            else if(!disposed)Changed?.Invoke();
        }
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
                legacyUpgradeRetryUtc = DateTime.MinValue;
                lastAuthFailure = null;
                CloseOtherAccountNotes(); Storage.NotifyNotesChanged(); next = DateTime.UtcNow; Update("已登录，等待同步");
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
            record.DeleteRequested = true; Persist(); next = DateTime.UtcNow.AddSeconds(2);
            Update(NeedsLogin ? "本机删除已保存，重新登录后继续同步" : "删除操作等待同步");
        }
        internal async Task SyncNow(bool retryFailures=false)
        {
            if (disposed || Busy || Session == null || Storage.Incognito || NeedsLogin) return;
            if (!await gate.WaitAsync(0)) return;
            Busy = true; running = new CancellationTokenSource(); var cancel = running.Token;
            next = DateTime.UtcNow.AddSeconds(30);
            try {
                if (!FlushOpen()) throw new IOException("本机保存未完成，暂缓同步。");
                Update("正在同步…");
                // Verify identity before adopting any local files into this account.
                var me = await api.Json("/auth/me", token: Session.Token, cancel: cancel);
                if (me["user"]?.Value<string>("id") != Session.UserId) throw new IOException("账号校验不一致，已停止同步。");
                if (!Directory.Exists(DirectoryPath)) Directory.CreateDirectory(DirectoryPath);
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
                    if (outcome == "conflict_copy") record.ConflictOf = record.RemoteId;
                    record.RemoteId = remote.Value<string>("id"); record.Revision = remote.Value<long>("revision");
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
                LastSyncUtc = DateTime.UtcNow;
                int failures=state.Notes.Count(p=>p.Value.LastError!=null && p.Value.ErrorStamp==CloudStore.QuickStamp(PathOf(p.Key)));
                Update(failures>0 ? failures+" 条便签同步失败，其他便签已同步" : state.Deferred.Count>0 ? "部分便签正在编辑，稍后继续同步" : "已同步");
            } catch (OperationCanceledException) { if (!disposed) Update("本机内容已保留，稍后继续同步"); }
            catch (CloudApiException e) {
                if (InvalidCredential(e) || e.Status == 401 && e.Code != "access_expired") {
                    RequireLogin(); Update("登录已失效，本机内容已保留，请重新登录");
                }
                else if (e.Code == "access_expired") Update("连接云端暂未完成，本机已保存，稍后继续同步");
                else Update("同步未完成：" + e.Message);
                next = DateTime.UtcNow.AddSeconds(e.Status == 429 ? 120 : 30);
            } catch (Exception e) { Update("同步未完成，本机内容已保留：" + e.Message); next = DateTime.UtcNow.AddSeconds(30); }
            finally { activeFile=null;Busy = false; running.Dispose(); running = null; gate.Release(); if (!disposed) Changed?.Invoke(); }
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
            if (revision <= record.Revision && !force) return;
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
            state.Notes[name] = record; Persist(); Storage.NotifyNotesChanged();
        }
        public void Dispose()
        {
            disposed = true; timer.Stop(); timer.Dispose(); running?.Cancel(); api.Dispose();
            if (Current == this) Current = null;
        }
    }
}
