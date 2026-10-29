using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Collections.Generic;
using KimNotes;
using KimNotes.utils;
using Newtonsoft.Json.Linq;

[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class CloudEdgeRegression
{
    static int checks;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    static void Wait(Task task){var until=DateTime.UtcNow.AddSeconds(90);while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(2);if(DateTime.UtcNow>until)throw new Exception("fixture timeout");}task.GetAwaiter().GetResult();}
    static CloudSession Account(string id)=>new CloudSession{UserId=id,Email="audit@example.invalid",Token=new string('x',43),ExpiresUtc=DateTime.UtcNow.AddHours(1),RefreshToken=new string('r',43),RefreshExpiresUtc=DateTime.UtcNow.AddDays(180),SessionId=Guid.NewGuid().ToString(),ProtocolVersion=2};
    static string Make(string name,string text){var path=Path.Combine(InitConfig.GetConfigValue("notesPath"),name);Directory.CreateDirectory(Path.GetDirectoryName(path));using(var r=new RichTextBox()){var h=r.Handle;r.Text=text;r.SaveFile(path);}return path;}
    static void ImagePixels(){
        using(var source=new Bitmap(256,160,PixelFormat.Format32bppArgb))using(var editor=new RichTextBox()){
            uint seed=12345;for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++){seed^=seed<<13;seed^=seed>>17;seed^=seed<<5;source.SetPixel(x,y,Color.FromArgb(255,(int)((seed>>16)&255),(int)((seed>>8)&255),(int)(seed&255)));}
            var h=editor.Handle;note.AppendCloudImage(editor,source);
            using(var extracted=NoteImages.Extract(CloudDocuments.Pictures(editor.Rtf).Single())){
                int diffs=0,max=0;for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++){var a=source.GetPixel(x,y);var b=extracted.GetPixel(x,y);if(a.ToArgb()!=b.ToArgb())diffs++;max=Math.Max(max,Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B))));}
                Console.WriteLine("IMAGE_INSERT source="+source.Size+" result="+extracted.Size+" changedPixels="+diffs+"/"+(source.Width*source.Height)+" maxChannelChange="+max);
                Check(diffs==0 && source.Size==extracted.Size,"native image insertion preserves every source pixel and original dimensions");
            }
        }
    }
    static void DeleteDuringDownload(){
        CloudStore.ClearSession();string user=Guid.NewGuid().ToString();var server=new Server(user);
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SignIn(Account(user)));string path=Make("delete-during-download.rtf","local original");Wait(sync.SyncNow());
            var record=CloudStore.ReadState(user).Notes[Path.GetFileName(path)];
            server.Remote(record.RemoteId,"remote update with picture",record.Revision+1,true);
            server.DownloadHold=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var syncing=sync.SyncNow();var until=DateTime.UtcNow.AddSeconds(10);
            while(!server.DownloadWaiting){Application.DoEvents();Thread.Sleep(2);if(DateTime.UtcNow>until)throw new Exception("attachment not requested");}
            File.Delete(path);sync.Deleted(path);server.DownloadHold.SetResult(true);Wait(syncing);
            Check(!File.Exists(path) && CloudStore.ReadState(user).Notes[Path.GetFileName(path)].DeleteRequested,"a user deletion during remote image download cannot be overwritten by that download");
            Console.WriteLine("DELETE_DURING_PULL afterDownload fileExists="+File.Exists(path)+" status="+sync.NoteStatus(Path.GetFileName(path)));
            Wait(sync.SyncNow());var result=CloudStore.ReadState(user).Notes[Path.GetFileName(path)];
            Console.WriteLine("DELETE_DURING_PULL afterDeleteAck fileExists="+File.Exists(path)+" recordDeleted="+result.Deleted+" deleteRequested="+result.DeleteRequested+" status="+sync.NoteStatus(Path.GetFileName(path)));
            Check(!File.Exists(path) && result.Deleted && !result.DeleteRequested,"acknowledged deletion leaves neither a resurrected local file nor pending remote content");
            Wait(sync.SignOut());
        }
    }
    static void PartialImport(){
        CloudStore.ClearSession();string user=Guid.NewGuid().ToString();var server=new Server(user);string id=Guid.NewGuid().ToString();server.Remote(id,"remote new note",1,false);
        string path=Path.Combine(InitConfig.GetConfigValue("notesPath"),"cloud-"+id+".rtf"),meta=path+".meta.json";Directory.CreateDirectory(meta);
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SignIn(Account(user)));Console.WriteLine("PARTIAL_IMPORT afterFailure localFile="+File.Exists(path)+" mapped="+CloudStore.ReadState(user).Notes.ContainsKey(Path.GetFileName(path))+" deferred="+CloudStore.ReadState(user).Deferred.Count);
            Check(CloudStore.ReadState(user).Notes.ContainsKey(Path.GetFileName(path)) && CloudStore.ReadState(user).Deferred.Count==1,"partial remote install persists its original identity and deferred operation");
        }
        Directory.Delete(meta);
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SyncNow());
            var state=CloudStore.ReadState(user);int copies=Directory.GetFiles(InitConfig.GetConfigValue("notesPath"),"cloud-"+id+"*.rtf").Length;
            Console.WriteLine("PARTIAL_IMPORT recovered cloudNotes="+server.Notes.Count+" localCopies="+copies+" mappings="+state.Notes.Count+" status="+sync.Status);
            Check(server.Notes.Count==1 && copies==1 && state.Notes.Count==1 && state.Notes.Values.Single().IncomingHash==null,"restarting after a partial install completes one original note without creating or uploading duplicates");
            Wait(sync.SignOut());
        }
    }
    static void UnchangedSync(){
        CloudStore.ClearSession();string user=Guid.NewGuid().ToString();var server=new Server(user);var state=new CloudState();
        for(int i=0;i<300;i++){string name="unchanged-"+i+".rtf";string path=Make(name,"unchanged");CloudStore.Claim(name,user);state.Notes[name]=new CloudRecord{Revision=1,LocalHash=CloudStore.FileHash(path),LocalStamp=CloudStore.QuickStamp(path)};}
        CloudStore.SaveState(user,state);
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SignIn(Account(user)));
            string statePath=Path.Combine(CloudStore.Root,user,"sync.json");int events=0;
            using(var watch=new FileSystemWatcher(Path.GetDirectoryName(statePath),"sync.json")){
                watch.Filter="*";watch.NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.FileName;watch.Changed+=(s,e)=>Interlocked.Increment(ref events);watch.Created+=(s,e)=>Interlocked.Increment(ref events);watch.Renamed+=(s,e)=>Interlocked.Increment(ref events);watch.EnableRaisingEvents=true;
                long biggestGap=0;var clock=Stopwatch.StartNew();var timer=new System.Windows.Forms.Timer{Interval=10};long last=0;timer.Tick+=(s,e)=>{long current=clock.ElapsedMilliseconds;biggestGap=Math.Max(biggestGap,current-last);last=current;};timer.Start();
                int pushes=server.Pushes;Wait(sync.SyncNow());Application.DoEvents();timer.Stop();timer.Dispose();Thread.Sleep(100);
                Console.WriteLine("UNCHANGED_SYNC notes=300 elapsedMs="+clock.ElapsedMilliseconds+" maxUiGapMs="+biggestGap+" stateFileEvents="+events+" stateBytes="+new FileInfo(statePath).Length+" pushes="+(server.Pushes-pushes));
                Check(events==0 && server.Pushes==pushes,"unchanged notes never rewrite sync state, backups or temporary state files and never upload");
            }
            Wait(sync.SignOut());
        }
    }
    static void NetworkBodyFailure(){
        CloudStore.ClearSession();string user=Guid.NewGuid().ToString();var server=new Server(user);
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SignIn(Account(user)));string path=Make("network-body-failure.rtf","keep offline edits");server.FailBodyOnce=true;Wait(sync.SyncNow());
            var state=CloudStore.ReadState(user);var record=state.Notes[Path.GetFileName(path)];Console.WriteLine("BODY_IO_FAILURE initial pending="+(record.Pending!=null)+" error="+record.LastError+" status="+sync.NoteStatus(Path.GetFileName(path)));
            int pushes=server.Pushes;Wait(sync.SyncNow());Wait(sync.SyncNow());record=CloudStore.ReadState(user).Notes[Path.GetFileName(path)];
            Console.WriteLine("BODY_IO_FAILURE afterTwoAutoRetries pending="+(record.Pending!=null)+" pushDelta="+(server.Pushes-pushes)+" status="+sync.NoteStatus(Path.GetFileName(path))+" sync="+sync.Status);
            Check(record.Pending==null && record.LastError==null && server.Pushes-pushes==1 && server.Notes.Values.Single().Value<long>("revision")==1,"response-body IO failure retries automatically with the same operation, without duplicating the committed revision");
            Make(Path.GetFileName(path),"second edit saved by a legacy client");server.FailBodyOnce=true;Wait(sync.SyncNow());
            string statePath=Path.Combine(CloudStore.Root,user,"sync.json");var legacy=JObject.Parse(File.ReadAllText(statePath));
            ((JObject)legacy["Notes"][Path.GetFileName(path)]).Remove("RetryableError");Storage.WriteText(statePath,legacy.ToString());
            using(var restarted=new CloudSync(new CloudApi(server),false)){
                Wait(restarted.SyncNow());var recovered=CloudStore.ReadState(user).Notes[Path.GetFileName(path)];
                Check(recovered.Pending==null && recovered.LastError==null && recovered.Revision==2,"upgrading a legacy persisted failure retries it automatically after restart without duplicating the operation");
                Wait(restarted.SignOut());
            }
        }
    }
    static void EmptyNotes(){
        CloudStore.ClearSession();string user=Guid.NewGuid().ToString();var server=new Server(user);
        using(var sync=new CloudSync(new CloudApi(server),false)){
            string blank=Make("blank.rtf"," \n\t  ");Wait(sync.SignIn(Account(user)));
            Check(server.Pushes==0 && sync.NoteStatus(Path.GetFileName(blank))=="仅本地" && CloudStore.ReadState(user).Notes[Path.GetFileName(blank)].Pending==null,"new whitespace-only notes stay local with no upload");
            Make("blank.rtf","new content");Wait(sync.SyncNow());
            Check(server.Notes.Count==1 && sync.NoteStatus("blank.rtf")=="已同步","adding content to a local blank note starts normal synchronization");
            Make("blank.rtf","");Wait(sync.SyncNow());
            Check(CloudStore.ReadState(user).Notes["blank.rtf"].Revision==2 && server.Notes.Values.Single()["document"].Value<string>("text")=="","clearing an already synchronized note clears its cloud document rather than restoring old content");
            string picture=Make("picture-only.rtf","");using(var editor=new RichTextBox())using(var image=new Bitmap(32,24)){var h=editor.Handle;editor.LoadFile(picture);note.AppendCloudImage(editor,image);editor.SaveFile(picture);}Wait(sync.SyncNow());
            Check(server.Notes.Count==2 && sync.NoteStatus("picture-only.rtf")=="已同步","an image-only note is content and synchronizes normally");Wait(sync.SignOut());
        }
    }
    [STAThread]static int Main(string[] args){
        try{Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",args[0]);Directory.CreateDirectory(args[0]);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());InitConfig.InitSettings();
            ImagePixels();DeleteDuringDownload();PartialImport();NetworkBodyFailure();UnchangedSync();EmptyNotes();Console.WriteLine("All "+checks+" cloud edge checks passed. Data: "+args[0]);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    sealed class Server:HttpMessageHandler{
        internal string User,ImageId=Guid.NewGuid().ToString();internal int Pushes;internal bool DownloadWaiting,FailBodyOnce;internal TaskCompletionSource<bool> DownloadHold;
        internal readonly Dictionary<string,JObject> Notes=new Dictionary<string,JObject>();internal readonly List<JObject> Changes=new List<JObject>();internal byte[] Image;
        internal readonly Dictionary<string,JObject> Results=new Dictionary<string,JObject>();
        internal Server(string user){User=user;using(var image=new Bitmap(32,24))using(var stream=new MemoryStream()){using(var g=Graphics.FromImage(image))g.Clear(Color.Coral);image.Save(stream,ImageFormat.Png);Image=stream.ToArray();}}
        internal void Remote(string id,string text,long rev,bool image){var item=new JObject{["id"]=id,["revision"]=rev,["document"]=new JObject{["title"]=text,["text"]=text,["themeId"]=2,["attachments"]=image?new JArray(ImageId):new JArray()}};Notes[id]=item;Changes.Add((JObject)item.DeepClone());}
        static HttpResponseMessage Json(JObject obj)=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(obj.ToString(),Encoding.UTF8,"application/json")};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken cancel){
            string path=req.RequestUri.AbsolutePath.Substring(CloudApi.Prefix.Length);
            if(path=="/auth/me")return Json(new JObject{["user"]=new JObject{["id"]=User}});
            if(path=="/auth/desktop/refresh")return Json(new JObject{["token"]=new string('x',43),["refreshToken"]=new string('r',43),["sessionId"]=CloudStore.ReadSession().SessionId,["protocolVersion"]=2,["expiresIn"]=900,["refreshExpiresIn"]=15552000,["user"]=new JObject{["id"]=User,["email"]="audit@example.invalid"}});
            if(path=="/auth/desktop/logout")return Json(new JObject{["ok"]=true});
            if(path=="/config")return Json(new JObject{["inlineImageAttachmentsVersion"]=1});
            if(path=="/attachments"){Image=await req.Content.ReadAsByteArrayAsync();return Json(new JObject{["id"]=ImageId});}
            if(path.StartsWith("/attachments/")){DownloadWaiting=true;if(DownloadHold!=null)await DownloadHold.Task;return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Image)};}
            if(path=="/sync/pull"){int cursor=int.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["cursor"]);return Json(new JObject{["changes"]=new JArray(Changes.Skip(cursor).Select(c=>c.DeepClone())),["cursor"]=Changes.Count.ToString(),["hasMore"]=false});}
            if(path=="/sync/push"){
                Pushes++;var body=JObject.Parse(await req.Content.ReadAsStringAsync());var op=(JObject)body["operations"][0];string id=op.Value<string>("noteId");long revision=Notes.ContainsKey(id)?Notes[id].Value<long>("revision")+1:1;
                string operation=op.Value<string>("operationId");if(Results.TryGetValue(operation,out var old))return Json((JObject)old.DeepClone());
                var remote=new JObject{["id"]=id,["revision"]=revision};
                if(op.Value<string>("action")=="delete"){remote=(JObject)Notes[id].DeepClone();remote["revision"]=revision;remote["deletedAt"]=DateTime.UtcNow;}
                else remote["document"]=op["document"].DeepClone();
                Notes[id]=remote;Changes.Add((JObject)remote.DeepClone());var result=new JObject{["results"]=new JArray(new JObject{["status"]="applied",["note"]=remote.DeepClone()})};Results[operation]=result;
                if(FailBodyOnce){FailBodyOnce=false;return new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new BrokenStream())};}return Json((JObject)result.DeepClone());
            }
            throw new Exception("unexpected "+path);
        }
    }
    sealed class BrokenStream:MemoryStream{public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>Task.FromException<int>(new IOException("synthetic connection interrupted while reading HTTP response body"));}
}
