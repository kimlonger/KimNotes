using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;
using Newtonsoft.Json.Linq;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class CloudRegression
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr first,IntPtr second);
    static int checks;
    static void Check(bool value,string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS "+message); }
    static void Wait(Task task) { var until=DateTime.UtcNow.AddSeconds(60);while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(2);if(DateTime.UtcNow>until)throw new Exception("Cloud task timed out after 60 seconds"); } task.GetAwaiter().GetResult(); }
    static string Make(string name,string text) { var path=Path.Combine(InitConfig.GetConfigValue("notesPath"),name); Directory.CreateDirectory(Path.GetDirectoryName(path)); using(var r=new RichTextBox()){var handle=r.Handle;r.Text=text; r.SaveFile(path,RichTextBoxStreamType.RichText);} return path; }
    const string SessionId="bc858343-50a5-4c65-b4e0-0bf0a8b3123e";
    static CloudSession Account(string id)=>new CloudSession {UserId=id,Email="synthetic@example.invalid",Token=new string('x',43),ExpiresUtc=DateTime.UtcNow.AddMinutes(15),RefreshToken=new string('r',43),RefreshExpiresUtc=DateTime.UtcNow.AddDays(180),SessionId=SessionId,ProtocolVersion=2};
    static void Snapshot(Form form,string path) {
        using(var bmp=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,form.ClientRectangle);bmp.Save(path);}
    }
    static bool Fits(Control parent) {
        foreach(Control c in parent.Controls)if(c.Visible) {
            if(!parent.ClientRectangle.Contains(c.Bounds)) { Console.WriteLine("Clipped: "+c.GetType().Name+" "+c.Name+" "+c.Bounds+" in "+parent.ClientRectangle); return false; }
            var label=c as Label;
            if(label!=null && !label.AutoSize && !label.AutoEllipsis && !string.IsNullOrEmpty(label.Text)
                && TextRenderer.MeasureText(label.Text,label.Font,new System.Drawing.Size(label.Width,int.MaxValue),TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height>label.Height) {
                Console.WriteLine("Clipped label text: "+label.Name+" "+label.Text); return false;
            }
            if(!Fits(c))return false;
        }
        return true;
    }
    static bool AccountLayout(Form form) {
        var email=form.Controls.Find("account-email",true)[0];
        var password=form.Controls.Find("account-password",true)[0];
        var reveal=form.Controls.Find("account-reveal",true)[0];
        var register=form.Controls.Find("account-register",true)[0];
        bool fits = Fits(form) && form.Size==form.ClientSize && form.ClientSize.Width==(int)Math.Round(320*form.DeviceDpi/96f)
            && email.Height>=((TextBox)email).PreferredHeight && password.Height>=((TextBox)password).PreferredHeight
            && password.Right<=reveal.Left && register.Height>=register.PreferredSize.Height;
        if(!fits)Console.WriteLine("Account bounds: "+form.Size+"; client "+form.ClientSize+"; DPI "+form.DeviceDpi+"; email "+email.Height+"/"+email.PreferredSize.Height+"; password "+password.Height+"/"+password.PreferredSize.Height+"; register "+register.Height+"/"+register.PreferredSize.Height+"; password "+password.Bounds+" / eye "+reveal.Bounds);
        return fits;
    }
    [STAThread] static int Main(string[] args)
    {
        string root=args[0]; Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",root);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        try {
            InitConfig.InitSettings();
            var nativePath=Make("native.rtf","标题\n正文");
            using(var editor=new note("native.rtf"))
            using(var image=new System.Drawing.Bitmap(40,30)) { editor.InsertImage(image); Check(editor.SaveCurrentNoteSafe(),"note image saved before sync"); }
            string original=File.ReadAllText(nativePath);
            Check(CloudDocuments.Unpack(CloudDocuments.Pack(original))==original,"compressed rich text round trips exactly");
            Check(CloudDocuments.Pictures(original).Count()==1,"embedded picture group extracted");
            bool rejected=false;try{CloudDocuments.Pack(@"{\rtf1{\object evil}}");}catch(InvalidDataException){rejected=true;}
            Check(rejected,"embedded executable object rejected");
            string user=Guid.NewGuid().ToString();
            var fake=new Server(user);
            using(var sync=new CloudSync(new CloudApi(fake),false)) {
                if(args.Length>1 && args[1]=="ui") {
                    var form=new KimNotes.settings.AccountForm(sync,new CloudApi(fake));
                    Exception uiFailure=null;
                    form.Shown += (sender,eventArgs) => form.BeginInvoke((Action)(async () => {
                      try {
                        var login=(Button)typeof(KimNotes.settings.AccountForm).GetField("login",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(form);
                        Check(login.Enabled,"native login is available immediately without CAPTCHA or embedded browser");
                        Console.WriteLine("Native account DPI: "+form.DeviceDpi+"; client: "+form.ClientSize);
                        Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(form.Handle),(IntPtr)(-4)),"account UI runner uses the same real PerMonitorV2 configuration as desktop EXE");
                        Check(form.Controls.Find("account-email",true).Length==1 && form.Controls.Find("account-password",true).Length==1,"native account window has email and password fields");
                        Check(!form.GetType().Assembly.GetReferencedAssemblies().Any(a=>a.Name.Contains("WebView2")),"desktop assembly has no WebView2 dependency");
                        Snapshot(form,Path.Combine(root,"account-window.png"));
                        Check(AccountLayout(form),"login fields, eye button and registration footer fit without clipping");
                        bool geometry=true; var observed=new HashSet<int>();
                        for(int round=0;round<4;round++)foreach(var screen in Screen.AllScreens) {
                            form.Location=new System.Drawing.Point(screen.WorkingArea.Left+40,screen.WorkingArea.Top+40);
                            await Task.Delay(60);
                            observed.Add(form.DeviceDpi);
                            geometry &= form.DeviceDpi==(int)GetDpiForWindow(form.Handle) && AccountLayout(form);
                            if(round==0)Snapshot(form,Path.Combine(root,"account-dpi-"+form.DeviceDpi+".png"));
                        }
                        Console.WriteLine("Account monitor DPI evidence: "+string.Join(", ",observed.OrderBy(v=>v)));
                        Check(geometry,"repeated physical monitor transitions preserve account field sizes and visible footer");
                        using(var settings=new KimNotes.settings.ConfigForm()) {
                            settings.Show(); await Task.Delay(30);
                            var entry=(LinkLabel)settings.Controls.Find("settings-account",true).Single();
                            var close=entry.Parent.Controls.OfType<ToolIconButton>().Single(b=>b.IconId=="close");
                            Check(entry.BackColor==System.Drawing.Color.Transparent && !entry.Bounds.IntersectsWith(close.Bounds),"settings account link blends into titlebar and leaves the close button clear");
                            Snapshot(settings,Path.Combine(root,"settings-account.png")); settings.Close();
                        }
                        Check(KimNotes.settings.AccountForm.RegisterUrl=="https://note.kimlulu.com/login/?mode=register","registration targets official register page in default browser");
                        ((TextBox)form.Controls.Find("account-email",true)[0]).Text="synthetic@example.invalid";
                        var secret=(TextBox)form.Controls.Find("account-password",true)[0];
                        var eye=(Button)form.Controls.Find("account-reveal",true)[0];
                        secret.Text="wrong-password"; secret.Select(2,3); eye.PerformClick();
                        Check(!secret.UseSystemPasswordChar && secret.SelectionStart==2 && secret.SelectionLength==3,"password eye preserves input and selection while revealing it");
                        eye.PerformClick(); Check(secret.UseSystemPasswordChar,"password eye masks the password again");
                        fake.LoginFailure=string.Concat(Enumerable.Repeat("网络繁忙，请稍后重试。",5));
                        login.PerformClick();
                        var working=typeof(KimNotes.settings.AccountForm).GetField("working",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                        var until=DateTime.UtcNow.AddSeconds(10);
                        while((bool)working.GetValue(form) && DateTime.UtcNow<until)await Task.Delay(10);
                        await Task.Delay(60);
                        Check(sync.Session==null && login.Enabled && AccountLayout(form),"failed login keeps native inputs usable and long error messages never clip registration");
                        Snapshot(form,Path.Combine(root,"account-error.png"));
                        fake.LoginHold=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        fake.SyncHold=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        secret.Text="unit-password";
                            login.PerformClick();
                        var authUntil=DateTime.UtcNow.AddSeconds(5);
                        while(!fake.LoginWaiting && DateTime.UtcNow<authUntil)await Task.Delay(10);
                        bool waitingLayout=fake.LoginWaiting;
                        foreach(var screen in Screen.AllScreens) {
                            form.Location=new System.Drawing.Point(screen.WorkingArea.Left+40,screen.WorkingArea.Top+40);
                            await Task.Delay(60);
                            waitingLayout &= AccountLayout(form) && secret.Visible && !login.Enabled;
                        }
                        Check(waitingLayout,"slow authentication keeps the whole login form intact while moving across monitors");
                        fake.LoginHold.SetResult(true);
                        var syncUntil=DateTime.UtcNow.AddSeconds(5);
                        while((sync.Session==null || !fake.SyncWaiting) && DateTime.UtcNow<syncUntil)await Task.Delay(10);
                        await Task.Delay(60);
                        bool acceptedLayout=fake.SyncWaiting && sync.Session!=null;
                        foreach(var screen in Screen.AllScreens) {
                            form.Location=new System.Drawing.Point(screen.WorkingArea.Left+40,screen.WorkingArea.Top+40);
                            await Task.Delay(60);
                            acceptedLayout &= AccountLayout(form) && !secret.Visible && form.Controls.Find("account-status",true)[0].Text.Contains("同步");
                        }
                        Check(acceptedLayout,"accepted login shows the signed-in view immediately while initial sync is still waiting across monitors");
                        Snapshot(form,Path.Combine(root,"account-initial-sync.png"));
                        fake.SyncHold.SetResult(true);
                            until=DateTime.UtcNow.AddSeconds(10);
                            while(((bool)working.GetValue(form) || sync.Session==null || sync.Busy) && DateTime.UtcNow<until) await Task.Delay(10);
                        await Task.Delay(60);
                        Console.WriteLine("Native UI sync status: "+sync.Status);
                        Console.WriteLine("Native fixture HTTP paths: "+string.Join(",",fake.Requests.Select(r=>r.AbsolutePath)));
                        Check(sync.Session?.UserId==user && sync.Status=="已同步","native form email/password login connects to automatic sync");
                        Check(((TextBox)form.Controls.Find("account-password",true)[0]).Text=="","native password field cleared after successful login");
                        Check(AccountLayout(form) && !secret.Visible && form.Controls.Find("account-status",true)[0].Visible,"signed-in compact layout shows sync status without clipped or hidden actions");
                        Snapshot(form,Path.Combine(root,"account-signed-in.png"));
                        fake.Unauthorized=true; await sync.SyncNow(); await Task.Delay(50);
                        Check(secret.Visible && login.Enabled && AccountLayout(form),"expired session restores login inputs without clipping error, logout or registration");
                        Snapshot(form,Path.Combine(root,"account-expired.png"));
                        fake.Unauthorized=false; await sync.SignOut(); await Task.Delay(50);
                        Check(AccountLayout(form) && secret.Visible,"signing out restores the full compact login layout");
                      } catch(Exception error){uiFailure=error;}
                      finally {fake.LoginHold?.TrySetResult(true);fake.SyncHold?.TrySetResult(true);form.Close();}
                    }));
                    Application.Run(form);
                    if(uiFailure!=null)throw uiFailure;
                    Console.WriteLine("UI smoke passed; "+Path.Combine(root,"account-window.png"));return 0;
                }
                Wait(sync.SignIn(Account(user)));
                Check(fake.Documents.Count==1 && fake.Uploads==1 && sync.Status=="已同步","initial local rich text and private image uploaded through domain");
                var session=CloudStore.ReadSession();
                var encrypted=File.ReadAllBytes(Path.Combine(CloudStore.Root,"session.dat"));
                var decrypted=Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(encrypted,null,System.Security.Cryptography.DataProtectionScope.CurrentUser));
                Check(session.UserId==user && session.Token==null && !decrypted.Contains(sync.Session.Token) && !Encoding.UTF8.GetString(encrypted).Contains(session.RefreshToken),"DPAPI persists refresh and identity, never the v2 access token");
                var state=CloudStore.ReadState(user); var record=state.Notes["native.rtf"];
                Check(record.Revision==1 && record.Pending==null,"acknowledged operation persisted");
                fake.FailAfterCommit=true; Make("native.rtf","离线编辑\n不能丢失");
                Wait(sync.SyncNow());
                state=CloudStore.ReadState(user);
                Check(state.Notes["native.rtf"].Pending!=null,"lost response retains immutable pending operation");
                var pending=state.Notes["native.rtf"].Pending.Value<string>("operationId");
                using(var resumed=new CloudSync(new CloudApi(fake),false)) {
                    Check(resumed.Session?.UserId==user,"restart restores encrypted session without password");
                    Wait(resumed.SyncNow());
                }
                Wait(sync.SignIn(Account(user)));
                Check(fake.OperationCounts[pending]==2 && fake.Documents.Values.Single().Value<long>("revision")==2,"retry reuses operation ID and does not duplicate write");
                record=CloudStore.ReadState(user).Notes["native.rtf"];
                fake.Remote(record.RemoteId,"云端更新",record.Revision+1);
                Wait(sync.SyncNow());
                using(var r=new RichTextBox()){r.LoadFile(nativePath);Check(r.Text.Contains("云端更新"),"remote update downloaded to local RTF");}
                Check(CloudStore.ReadState(user).Cursor==fake.Changes.Count.ToString(),"pull cursor advances only after applying changes");
                fake.Remote(record.RemoteId,"网页图片",4);
                fake.Documents[record.RemoteId]["document"]["attachments"]=new JArray(fake.ImageId);
                fake.Changes[fake.Changes.Count-1]=(JObject)fake.Documents[record.RemoteId].DeepClone();
                Wait(sync.SyncNow());
                Check(CloudDocuments.Pictures(File.ReadAllText(nativePath)).Count()==1,"web image downloads and becomes a native embedded picture");
                Make("native.rtf","两台电脑同时编辑");
                fake.Remote(record.RemoteId,"另一台电脑内容",5);
                Wait(sync.SyncNow());
                Check(fake.Documents.Count==2 && Directory.GetFiles(InitConfig.GetConfigValue("notesPath"),"*.rtf").Length==2,"concurrent edit preserves local conflict copy and remote original");
                fake.Offline=true; Make("native.rtf","离线保存");
                Wait(sync.SyncNow()); Check(File.Exists(nativePath),"network outage preserves local contents");
                fake.Offline=false; Wait(sync.SyncNow());
                fake.Unauthorized=true; Wait(sync.SyncNow()); Check(sync.NeedsLogin && CloudStore.ReadSession()!=null,"expired server session pauses sync without erasing local identity");
                fake.Unauthorized=false;
                Wait(sync.SignIn(Account(user)));
                File.Delete(nativePath); sync.Deleted(nativePath); Wait(sync.SyncNow());
                Check(CloudStore.ReadState(user).Notes["native.rtf"].Deleted,"local deletion becomes cloud soft deletion");
                Wait(sync.SignOut());
                Check(CloudStore.ReadSession()==null && !CloudStore.CanShow(nativePath),"logout erases credential and hides account-owned notes");
            }
            using(var restarted=new CloudSync(new CloudApi(fake),false)) {
                fake.UserId=Guid.NewGuid().ToString(); fake.Documents.Clear(); fake.Changes.Clear(); fake.Results.Clear(); int pushes=fake.Pushes;
                Wait(restarted.SignIn(Account(fake.UserId)));
                Check(fake.Pushes==pushes,"another account never uploads previous account's local copies");
                Wait(restarted.SignOut());
            }
            Check(fake.Requests.All(u=>u.Scheme=="https" && u.Host=="note.kimlulu.com"),"every client request uses HTTPS domain, never IP");
            AuthenticationChecks();
            NoteSyncChecks();
            Console.WriteLine("All "+checks+" cloud checks passed. Data: "+root); return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void AuthenticationChecks()
    {
        CloudStore.ClearSession();
        string user=Guid.NewGuid().ToString();
        var legacy=Account(user);legacy.ProtocolVersion=0;legacy.RefreshToken=null;legacy.ExpiresUtc=DateTime.UtcNow.AddDays(20);
        var oldServer=new Server(user);CloudStore.SaveSession(legacy);
        using(var sync=new CloudSync(new CloudApi(oldServer),false)) {
            Wait(sync.SyncNow());
            Check(oldServer.Upgrades==1 && sync.Session.ProtocolVersion==2 && sync.Status=="已同步","valid legacy Bearer upgrades exactly once before adopting or uploading any note");
            Check(CloudStore.ReadSession().Token==null && CloudStore.ReadSession().RefreshToken==sync.Session.RefreshToken,"legacy upgrade atomically replaces disk Bearer with protected refresh credential");
        }
        CloudStore.ClearSession();legacy.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);CloudStore.SaveSession(legacy);
        var staleServer=new Server(user);
        using(var sync=new CloudSync(new CloudApi(staleServer),false)) {
            Wait(sync.SyncNow());
            Check(sync.NeedsLogin && staleServer.Upgrades==0 && sync.Session.UserId==user && CloudStore.ReadSession().Token==null,"expired legacy credential requires login while preserving identity");
        }
        CloudStore.ClearSession();legacy.ExpiresUtc=DateTime.UtcNow.AddDays(1);CloudStore.SaveSession(legacy);
        var rolling=new Server(user){LegacyUpgradeAvailable=false};
        using(var sync=new CloudSync(new CloudApi(rolling),false)) {
            Wait(sync.SyncNow());Wait(sync.SyncNow());
            Check(!sync.NeedsLogin && sync.Status=="已同步" && rolling.Upgrades==1 && CloudStore.ReadSession().Token==legacy.Token,"legacy upgrade 404 preserves valid identity and throttles migration retries");
        }
        CloudStore.ClearSession();
        var server=new Server(Guid.NewGuid().ToString());
        var api=new CloudApi(server);
        using(var sync=new CloudSync(api,false)) {
            Wait(sync.SignIn(Account(server.UserId)));
            sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);Wait(sync.SyncNow());
            Check(server.Refreshes==1 && !sync.NeedsLogin && sync.Status=="已同步","expired access token silently renews instead of signing out");
            int count=server.Refreshes;server.ExpirePath="/sync/pull";Wait(sync.SyncNow());
            Check(server.Refreshes==count+1 && sync.Status=="已同步","access_expired during sync renews once and safely retries JSON request");
            server.ExpirePath="/attachments";var bytes=new byte[]{1,2,3,4,5};
            Wait(api.Upload(bytes,sync.Session.Token,CancellationToken.None));
            Check(server.UploadPayloads.Count==2 && server.UploadPayloads.Distinct().Count()==1 && server.Image.SequenceEqual(bytes),"image upload retry recreates HttpContent with identical bytes");
            server.ExpirePath="/attachments/"+server.ImageId;
            var download=api.Download(server.ImageId,sync.Session.Token,CancellationToken.None);Wait(download);
            Check(download.Result.SequenceEqual(bytes),"private image download automatically renews access and retries");
            sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);count=server.Refreshes;
            server.RefreshHold=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first=api.Json("/auth/me",token:sync.Session.Token);
            var second=api.Json("/auth/me",token:sync.Session.Token);
            var third=api.Json("/auth/me",token:sync.Session.Token);
            server.RefreshHold.SetResult(true);Wait(Task.WhenAll(first,second,third));
            Check(server.Refreshes==count+1,"three concurrent requests share a single credential refresh");
            server.RefreshHold=null;
            sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);count=server.Refreshes;
            server.RefreshHold=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);server.RefreshStatus=503;
            first=api.Json("/auth/me",token:sync.Session.Token);second=api.Json("/auth/me",token:sync.Session.Token);third=api.Json("/auth/me",token:sync.Session.Token);
            server.RefreshHold.SetResult(true);
            try { Wait(Task.WhenAll(first,second,third)); } catch(CloudApiException) { }
            Check(server.Refreshes==count+1 && first.IsFaulted && second.IsFaulted && third.IsFaulted && !sync.NeedsLogin,"concurrent renewal failure is shared without queued refresh bursts or sign-out");
            server.RefreshHold=null;server.RefreshStatus=0;Wait(sync.SyncNow());
            count=server.Refreshes;server.ExpireAlways=true;server.ExpirePath="/auth/me";Wait(sync.SyncNow());
            Check(server.Refreshes==count+1 && !sync.NeedsLogin,"repeated access_expired is bounded to one renewal, without password or refresh loops");
            server.ExpireAlways=false;server.ExpirePath=null;
            sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);server.LoseRefreshResponse=true;
            string old=sync.Session.RefreshToken;Wait(sync.SyncNow());
            var disk=CloudStore.ReadSession();
            Check(!sync.NeedsLogin && disk.RefreshToken==old && disk.PendingRefreshToken==old && !string.IsNullOrEmpty(disk.PendingRefreshRequestId),"lost refresh response retains encrypted old credential and durable request ID");
        }
        var pending=CloudStore.ReadSession().PendingRefreshRequestId;
        using(var resumed=new CloudSync(new CloudApi(server),false)) {
            Check(resumed.Session.Token==null && resumed.Session.UserId==server.UserId,"restart restores identity and refresh only, never cached access");
            Wait(resumed.SyncNow());
            Check(resumed.Status=="已同步" && server.RefreshRequests[server.RefreshRequests.Count-1]==pending && CloudStore.ReadSession().PendingRefreshRequestId==null,"restart retries lost refresh with same ID and persists exactly the cached replacement pair");
            Check(resumed.Session.RefreshToken==server.RefreshToken && resumed.Session.Token==server.Token,"retried rotation uses the identical pair issued before response loss");
        }
        CloudStore.ClearSession();var transient=new Server(Guid.NewGuid().ToString());
        using(var sync=new CloudSync(new CloudApi(transient),false)) {
            Wait(sync.SignIn(Account(transient.UserId)));sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);
            transient.Offline=true;Wait(sync.SyncNow());
            Check(!sync.NeedsLogin && sync.Session.UserId==transient.UserId && CloudStore.ReadSession().PendingRefreshRequestId!=null,"network failure during renewal keeps account and pending synchronization");
            transient.Offline=false;transient.RefreshStatus=429;Wait(sync.SyncNow());
            Check(!sync.NeedsLogin && !string.IsNullOrEmpty(sync.Session.RefreshToken),"refresh rate limit preserves credentials without forcing login");
            transient.RefreshStatus=503;Wait(sync.SyncNow());
            Check(!sync.NeedsLogin,"refresh service outage preserves long-lived login");
            transient.RefreshStatus=0;Wait(sync.SyncNow());
            Check(sync.Status=="已同步" && transient.RefreshRequests.Distinct().Count()==1,"transient renewal retries use one durable request ID until successful");
            transient.TransientStatus=503;Wait(sync.SyncNow());
            Check(!sync.NeedsLogin,"business API server errors never erase login identity");
            transient.TransientStatus=0;
        }
        CloudStore.ClearSession();var atomic=new Server(Guid.NewGuid().ToString());
        string sessionPath=Path.Combine(CloudStore.Root,"session.dat");
        string atomicRequest;
        using(var sync=new CloudSync(new CloudApi(atomic),false)) {
            Wait(sync.SignIn(Account(atomic.UserId)));sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);
            string previous=sync.Session.Token;
            atomic.BeforeRefreshResponse=()=>File.SetAttributes(sessionPath,File.GetAttributes(sessionPath)|FileAttributes.ReadOnly);
            try { Wait(sync.SyncNow()); }
            finally { File.SetAttributes(sessionPath,File.GetAttributes(sessionPath)&~FileAttributes.ReadOnly);atomic.BeforeRefreshResponse=null; }
            var disk=CloudStore.ReadSession();atomicRequest=disk.PendingRefreshRequestId;
            Check(!sync.NeedsLogin && sync.Session.Token==previous && !string.IsNullOrEmpty(atomicRequest),"failed atomic replacement never uses unpersisted credentials and keeps durable retry");
            Check(atomic.PendingWasDurable,"refresh credential and request ID are persisted before request reaches server");
        }
        using(var resumed=new CloudSync(new CloudApi(atomic),false)) {
            Wait(resumed.SyncNow());
            Check(resumed.Status=="已同步" && atomic.RefreshRequests.Last()==atomicRequest && resumed.Session.RefreshToken==atomic.RefreshToken,"restart recovers exact rotation after local credential save failure");
            atomic.Offline=true;Wait(resumed.SignOut());
            Check(resumed.Session==null && CloudStore.ReadSession()==null && resumed.Status.Contains("本机退出"),"offline logout immediately erases local credentials and reports remote revocation unconfirmed");
        }
        CloudStore.ClearSession();var mismatch=new Server(Guid.NewGuid().ToString());
        using(var sync=new CloudSync(new CloudApi(mismatch),false)) {
            string originalId=mismatch.UserId;Wait(sync.SignIn(Account(originalId)));
            sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);mismatch.UserId=Guid.NewGuid().ToString();
            Wait(sync.SyncNow());
            Check(sync.NeedsLogin && sync.Session.UserId==originalId && CloudStore.ReadSession().UserId==originalId,"refresh response for another account never replaces account identity or claims its notes");
        }
        foreach(string code in new[]{"session_revoked","refresh_expired","refresh_revoked","refresh_reused","account_disabled"}) {
            CloudStore.ClearSession();var revoked=new Server(Guid.NewGuid().ToString());
            string owned="identity-"+Guid.NewGuid().ToString("N")+".rtf";
            using(var sync=new CloudSync(new CloudApi(revoked),false)) {
                Wait(sync.SignIn(Account(revoked.UserId)));CloudStore.Claim(owned,revoked.UserId);
                sync.Session.ExpiresUtc=DateTime.UtcNow.AddSeconds(-1);revoked.RefreshStatus=401;revoked.RefreshError=code;
                Wait(sync.SyncNow());
                var disk=CloudStore.ReadSession();
                Check(sync.NeedsLogin && disk.UserId==revoked.UserId && disk.ReauthenticationRequired && disk.RefreshToken==null && CloudStore.CanShow(owned),code+" stops networking and clears credentials while preserving account-owned local notes");
                int calls=revoked.Refreshes;Wait(sync.SyncNow());Check(revoked.Refreshes==calls,code+" does not retry failed credentials in background");
            }
            using(var restarted=new CloudSync(new CloudApi(revoked),false))
                Check(restarted.NeedsLogin && restarted.Session.UserId==revoked.UserId && CloudStore.CanShow(owned),code+" reauthentication state and account isolation survive restart");
        }
        CloudStore.ClearSession();var closing=new Server(Guid.NewGuid().ToString());var closingApi=new CloudApi(closing);
        var closingSync=new CloudSync(closingApi,false);Wait(closingSync.SignIn(Account(closing.UserId)));closingSync.Dispose();
        try { Wait(closingApi.Json("/auth/me",token:closingSync.Session.Token)); } catch(OperationCanceledException) { }
        Check(!CloudStore.ReadSession().ReauthenticationRequired && CloudStore.ReadSession().RefreshToken!=null,"shutdown cancellation never marks a valid persisted login as revoked");
        CloudStore.ClearSession();
    }
    static void NoteSyncChecks()
    {
        CloudStore.ClearSession();var server=new Server(Guid.NewGuid().ToString()){RejectText="reject-limit"};
        string bad=Make("limit-failure.rtf","reject-limit"),healthy=Make("healthy.rtf","normal note");
        string remoteId=Guid.NewGuid().ToString();server.Remote(remoteId,"remote unaffected",1);
        using(var sync=new CloudSync(new CloudApi(server),false)) {
            Check(sync.NoteStatus(Path.GetFileName(bad))==null,"signed-out note cards do not show account sync state");
            Wait(sync.SignIn(Account(server.UserId)));
            Check(sync.NoteStatus(Path.GetFileName(bad))=="同步失败" && sync.NoteError(Path.GetFileName(bad)).Contains("限制"),"oversized note has a persistent readable failure reason without losing its local original");
            Check(sync.NoteStatus(Path.GetFileName(healthy))=="已同步" && server.Documents.Values.Any(d=>d["document"].Value<string>("text")=="normal note"),"one rejected note does not block healthy local uploads");
            var state=CloudStore.ReadState(server.UserId);
            Check(state.Notes.Any(p=>p.Value.RemoteId==remoteId) && sync.Status.Contains("1 条"),"remote changes are still received and global status counts failed notes accurately");
            int failures=server.Rejections;Wait(sync.SyncNow());
            Check(server.Rejections==failures,"unchanged rejected image note is not encoded or uploaded again every sync tick");
            Make("limit-failure.rtf","fixed local content");sync.Saved(bad);
            Check(sync.NoteStatus(Path.GetFileName(bad))=="待同步","editing a failed note immediately changes its state to pending");
            Wait(sync.SyncNow());
            Check(sync.NoteStatus(Path.GetFileName(bad))=="已同步" && sync.NoteError(Path.GetFileName(bad))==null,"edited valid note retries automatically and clears stale error after acknowledgement");
            Make("limit-failure.rtf","reject-limit");sync.Saved(bad);
            state=CloudStore.ReadState(server.UserId);string original=state.Notes[Path.GetFileName(bad)].RemoteId;
            server.Remote(original,"new cloud original",state.Notes[Path.GetFileName(bad)].Revision+1);
            string second=Guid.NewGuid().ToString();server.Remote(second,"independent pull",1);
            Wait(sync.SyncNow());state=CloudStore.ReadState(server.UserId);
            Check(state.Deferred.ContainsKey(original) && state.Notes.Any(p=>p.Value.RemoteId==second),"a cloud conflict with a failed local note is durably deferred while later remote notes still arrive");
            Check(File.ReadAllText(bad).Contains("reject-limit"),"deferred cloud change never overwrites unsynced local content");
            server.RejectText=null;Wait(sync.SyncNow(true));state=CloudStore.ReadState(server.UserId);
            Check(state.Deferred.Count==0 && state.Notes[Path.GetFileName(bad)].ConflictOf==original && state.Notes.Any(p=>p.Value.RemoteId==original),"manual retry preserves both conflict copy and deferred cloud original");
            Make("healthy.rtf","before slow upload");sync.Saved(healthy);
            server.BeforePushResponse=()=>{server.BeforePushResponse=null;Make("healthy.rtf","edited during upload");sync.Saved(healthy);};
            Wait(sync.SyncNow());
            Check(sync.NoteStatus(Path.GetFileName(healthy))=="待同步","editing during an upload is never falsely marked as synced");
            Wait(sync.SyncNow());
            Check(sync.NoteStatus(Path.GetFileName(healthy))=="已同步" && server.Documents.Values.Any(d=>d["document"].Value<string>("text")=="edited during upload"),"next sync sends the later edit and then marks it synced");
            Wait(sync.SignOut());
        }
    }

    sealed class Server:HttpMessageHandler
    {
        internal string UserId; internal bool Offline,Unauthorized,FailAfterCommit;
        internal TaskCompletionSource<bool> LoginHold,SyncHold;
        internal bool LoginWaiting,SyncWaiting;
        internal string LoginFailure="邮箱或密码不正确，请重新输入。";
        internal int Uploads,Pushes;
        internal string RejectText;
        internal int Rejections;
        internal Action BeforePushResponse;
        internal int Refreshes,Upgrades,TransientStatus,RefreshStatus;
        internal string RefreshError,ExpirePath,Token=new string('x',43),RefreshToken=new string('r',43);
        internal bool LoseRefreshResponse,LegacyUpgradeAvailable=true,ExpireAlways;
        internal bool PendingWasDurable;
        internal Action BeforeRefreshResponse;
        internal TaskCompletionSource<bool> RefreshHold;
        internal readonly List<string> RefreshRequests=new List<string>(),UploadPayloads=new List<string>();
        internal readonly Dictionary<string,JObject> RefreshResults=new Dictionary<string,JObject>();
        internal byte[] Image;
        internal string ImageId=Guid.NewGuid().ToString();
        internal readonly List<Uri> Requests=new List<Uri>();
        internal readonly Dictionary<string,JObject> Documents=new Dictionary<string,JObject>();
        internal readonly List<JObject> Changes=new List<JObject>();
        internal readonly Dictionary<string,JObject> Results=new Dictionary<string,JObject>();
        internal readonly Dictionary<string,int> OperationCounts=new Dictionary<string,int>();
        internal Server(string user){UserId=user;}
        internal JObject Credentials()=>new JObject{["token"]=Token,["refreshToken"]=RefreshToken,["user"]=new JObject{["id"]=UserId,["email"]="synthetic@example.invalid"},["expiresIn"]=900,["refreshExpiresIn"]=15552000,["sessionId"]=SessionId,["protocolVersion"]=2};
        internal void Remote(string id,string text,long rev) {
            var note=new JObject {["id"]=id,["revision"]=rev,["document"]=new JObject {["title"]=text,["text"]=text,["themeId"]=2,["attachments"]=new JArray()}};
            Documents[id]=note;Changes.Add((JObject)note.DeepClone());
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken cancel) {
            Requests.Add(req.RequestUri);if(Offline)throw new HttpRequestException("offline fixture");
            if(!req.Headers.TryGetValues("X-Kim-Auth-Version",out var versions) || versions.Single()!="2")throw new Exception("Missing v2 auth version header");
            if(Unauthorized)return Response(401,new JObject {["message"]="expired",["code"]="session_revoked"});
            var path=req.RequestUri.AbsolutePath.Substring(CloudApi.Prefix.Length);
            if(path=="/config")return Response(200,new JObject{["inlineImageAttachmentsVersion"]=1});
            if(path=="/auth/desktop/refresh" || path=="/auth/desktop/upgrade") {
                var body=JObject.Parse(await req.Content.ReadAsStringAsync());
                bool upgrade=path.EndsWith("upgrade");
                if(upgrade) {
                    Upgrades++;
                    if(!LegacyUpgradeAvailable)return Response(404,new JObject{["code"]="not_found"});
                    if(req.Headers.Authorization?.Parameter!=new string('x',43))return Response(401,new JObject{["code"]="invalid_credentials"});
                } else {
                    Refreshes++;RefreshRequests.Add(body.Value<string>("requestId"));
                    var stored=CloudStore.ReadSession();
                    PendingWasDurable=stored.PendingRefreshRequestId==body.Value<string>("requestId") && stored.PendingRefreshToken==body.Value<string>("refreshToken");
                    if(RefreshHold!=null && !RefreshHold.Task.IsCompleted)await RefreshHold.Task;
                    if(RefreshStatus!=0)return Response(RefreshStatus,new JObject{["code"]=RefreshError,["message"]="refresh fixture"});
                }
                string key=(upgrade?"upgrade":body.Value<string>("refreshToken"))+":"+body.Value<string>("requestId");
                if(!RefreshResults.TryGetValue(key,out var pair)) {
                    if(!upgrade && body.Value<string>("refreshToken")!=RefreshToken)return Response(401,new JObject{["code"]="refresh_reused"});
                    Token="a"+Guid.NewGuid().ToString("N")+"0000000000";
                    RefreshToken="r"+Guid.NewGuid().ToString("N")+"0000000000";
                    pair=Credentials();RefreshResults.Add(key,pair);
                }
                if(LoseRefreshResponse){LoseRefreshResponse=false;throw new HttpRequestException("refresh response lost after commit");}
                BeforeRefreshResponse?.Invoke();
                return Response(200,(JObject)pair.DeepClone());
            }
            if(path=="/auth/desktop/logout")return Response(200,new JObject{["ok"]=true});
            if(path==ExpirePath) {
                if(req.Content!=null)UploadPayloads.Add(Convert.ToBase64String(await req.Content.ReadAsByteArrayAsync()));
                if(!ExpireAlways)ExpirePath=null;
                return Response(401,new JObject{["code"]="access_expired",["message"]="expired access"});
            }
            if(TransientStatus!=0)return Response(TransientStatus,new JObject{["code"]="temporary_failure"});
            if(path=="/auth/me") {
                if(SyncHold!=null && !SyncHold.Task.IsCompleted) {SyncWaiting=true;await SyncHold.Task;}
                return Response(200,new JObject{["user"]=new JObject{["id"]=UserId}});
            }
            if(path=="/auth/desktop/login") {
                var credentials=JObject.Parse(await req.Content.ReadAsStringAsync());
                if(credentials.Value<string>("password")!="unit-password")return Response(401,new JObject{["message"]=LoginFailure});
                if(LoginHold!=null && !LoginHold.Task.IsCompleted) {LoginWaiting=true;await LoginHold.Task;}
                return Response(200,Credentials());
            }
            if(path=="/auth/logout")return Response(200,new JObject());
            if(path=="/attachments") {Uploads++;Image=await req.Content.ReadAsByteArrayAsync();UploadPayloads.Add(Convert.ToBase64String(Image));return Response(201,new JObject{["id"]=ImageId});}
            if(path=="/attachments/"+ImageId) return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Image)};
            if(path=="/sync/push") {
                Pushes++; var body=JObject.Parse(await req.Content.ReadAsStringAsync());var operation=(JObject)body["operations"][0];
                if(RejectText!=null && operation["document"]?.Value<string>("text")==RejectText) {Rejections++;return Response(413,new JObject{["code"]="body_too_large",["message"]="fixture limit"});}
                string op=operation.Value<string>("operationId"),id=operation.Value<string>("noteId");
                OperationCounts[op]=OperationCounts.TryGetValue(op,out var count)?count+1:1;
                if(!Results.TryGetValue(op,out var result)) {
                    string status="applied";
                    if(operation.Value<string>("action")=="upsert") {
                        long rev=Documents.TryGetValue(id,out var existing)?existing.Value<long>("revision"):0;
                        if(rev!=operation.Value<long>("baseRevision")) {id=Guid.NewGuid().ToString();rev=0;status="conflict_copy";}
                        var note=new JObject{["id"]=id,["revision"]=rev+1,["document"]=operation["document"].DeepClone()};
                        if(status=="conflict_copy")note["conflictOf"]=operation["noteId"];
                        Documents[id]=note;Changes.Add((JObject)note.DeepClone());
                    } else {var note=(JObject)Documents[id].DeepClone();note["revision"]=note.Value<long>("revision")+1;note["deletedAt"]=DateTime.UtcNow;Documents[id]=note;Changes.Add(note);}
                    result=new JObject{["operationId"]=op,["status"]=status,["note"]=Documents[id].DeepClone()};Results[op]=result;
                }
                if(FailAfterCommit){FailAfterCommit=false;throw new HttpRequestException("response lost after commit");}
                BeforePushResponse?.Invoke();
                return Response(200,new JObject{["results"]=new JArray(result.DeepClone()),["cursor"]=Changes.Count.ToString()});
            }
            if(path=="/sync/pull") {
                var query=System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);int cursor=int.Parse(query["cursor"]);
                return Response(200,new JObject{["changes"]=new JArray(Changes.Skip(cursor).Select(c=>c.DeepClone())),["cursor"]=Changes.Count.ToString(),["hasMore"]=false});
            }
            throw new Exception("unexpected fixture path "+path);
        }
        static HttpResponseMessage Response(int status,JObject value)=>new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(value.ToString(),Encoding.UTF8,"application/json")};
    }
}
