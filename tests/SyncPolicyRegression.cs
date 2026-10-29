using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;
using Newtonsoft.Json.Linq;
[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class SyncPolicyRegression
{
    static int checks;
    const BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    static void Wait(Task task){var until=DateTime.UtcNow.AddSeconds(60);while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(2);if(DateTime.UtcNow>until)throw new Exception("Fixture timeout");}task.GetAwaiter().GetResult();}
    static void Timing(){
        var start=new DateTime(2026,10,29,0,0,0,DateTimeKind.Utc);var schedule=new SyncSchedule();
        Check(schedule.IsDue(start)&&schedule.UploadDue(start),"startup schedules an immediate upload and pull");schedule.Begin(true);schedule.Complete(start);
        Check(!schedule.IsDue(start.AddSeconds(59))&&schedule.IsDue(start.AddSeconds(60))&&!schedule.UploadDue(start.AddSeconds(60)),"idle polling waits sixty seconds and does not force early uploads");
        schedule.Saved(start.AddSeconds(55));Check(schedule.IsDue(start.AddSeconds(60))&&!schedule.UploadDue(start.AddSeconds(60)),"a due cloud pull cannot bypass the ten-second upload quiet period");
        schedule.Begin(false);schedule.Complete(start.AddSeconds(60));Check(!schedule.UploadDue(start.AddSeconds(64))&&schedule.UploadDue(start.AddSeconds(65)),"a pull-only round preserves the pending upload deadline");
        schedule.Begin(true);schedule.Complete(start);
        for(int second=1;second<61;second+=4)schedule.Saved(start.AddSeconds(second));
        Check(!schedule.UploadDue(start.AddSeconds(60))&&schedule.UploadDue(start.AddSeconds(61)),"continuous edits cannot postpone an upload beyond sixty seconds from the first saved edit");
        schedule.Begin(true);schedule.Saved(start.AddSeconds(70));schedule.Complete(start.AddSeconds(72));
        Check(!schedule.UploadDue(start.AddSeconds(79))&&schedule.UploadDue(start.AddSeconds(80)),"saving during an active sync retains a separate next-round deadline");
        schedule.Complete(start,TimeSpan.FromSeconds(30));schedule.Saved(start.AddSeconds(1));
        Check(!schedule.IsDue(start.AddSeconds(29))&&schedule.IsDue(start.AddSeconds(30)),"network backoff takes precedence over repeated local saves");
        schedule.Complete(start,TimeSpan.FromSeconds(120));Check(!schedule.IsDue(start.AddSeconds(119))&&schedule.IsDue(start.AddSeconds(120)),"rate limits retain a two-minute backoff");
        schedule.Reset();Check(schedule.IsDue(start)&&schedule.UploadDue(start),"logging in resets old deadlines and triggers immediate synchronization");
    }
    static CloudSession Session(Server s)=>new CloudSession{UserId=s.User,Email="sync-policy@example.invalid",Token=new string('x',43),RefreshToken=new string('r',43),SessionId=s.SessionId,ProtocolVersion=2,ExpiresUtc=DateTime.UtcNow.AddMinutes(15),RefreshExpiresUtc=DateTime.UtcNow.AddDays(180)};
    static string Make(string name,string text){string path=Path.Combine(InitConfig.GetConfigValue("notesPath"),name);Directory.CreateDirectory(Path.GetDirectoryName(path));using(var editor=new RichTextBox()){var handle=editor.Handle;editor.Text=text;editor.SaveFile(path);}return path;}
    static void SaveTiming(){using(var n=new note()){
        n.Show();n.SetText("two-second idle save");var edited=(DateTime)typeof(note).GetField("lastEditUtc",Flags).GetValue(n);
        typeof(note).GetMethod("AutoSaveTick",Flags).Invoke(n,new object[]{edited.AddMilliseconds(1900)});
        Check((bool)typeof(note).GetField("hasUnsavedChanges",Flags).GetValue(n),"typing is not written after only 1.9 seconds of idle time");
        typeof(note).GetMethod("AutoSaveTick",Flags).Invoke(n,new object[]{edited.AddMilliseconds(2100)});
        Check(!(bool)typeof(note).GetField("hasUnsavedChanges",Flags).GetValue(n),"typing saves after the two-second idle period");
        n.SetText("continuous typing remains protected");edited=(DateTime)typeof(note).GetField("lastEditUtc",Flags).GetValue(n);typeof(note).GetField("dirtySinceUtc",Flags).SetValue(n,edited.AddSeconds(-5));
        typeof(note).GetMethod("AutoSaveTick",Flags).Invoke(n,new object[]{edited});
        Check(!(bool)typeof(note).GetField("hasUnsavedChanges",Flags).GetValue(n),"continuous input still saves at the five-second ceiling without a pause");
        CloudStore.Claim(Path.GetFileName(n.FilePath),Guid.NewGuid().ToString());n.Close();
    }}
    static void LatestDownload(){
        CloudStore.ClearSession();var server=new Server();server.AddHistory();
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SignIn(Session(server)));var state=CloudStore.ReadState(server.User);
            Check(server.BootstrapCalls==2&&state.BootstrapComplete&&state.Cursor=="21"&&server.PulledFrom.All(c=>c>=21),"new-device login pages current notes and starts incremental catchup at the pinned checkpoint");
            Check(state.Notes.Count==2&&server.Downloads==1&&server.Pushes==0,"twenty historical image edits cause one latest-image download and no duplicate upload");
            string name=state.Notes.Single(p=>p.Value.RemoteId==Server.First).Key;
            using(var editor=new RichTextBox()){var handle=editor.Handle;editor.LoadFile(Path.Combine(InitConfig.GetConfigValue("notesPath"),name));Check(editor.Text.Contains("latest-20")&&NoteImages.PictureBoxes(editor).Count==1,"downloaded latest text and image become a readable native local note");}
            string path=Make("policy-local.rtf","pending local edit");sync.Saved(path);
            Wait((Task)typeof(CloudSync).GetMethod("RunSync",Flags).Invoke(sync,new object[]{false,false}));
            Check(server.Pushes==0&&sync.NoteStatus("policy-local.rtf")=="待同步","a pull-only round leaves recently saved local contents pending");
            Wait(sync.SyncNow());Check(server.Pushes==1&&sync.NoteStatus("policy-local.rtf")=="已同步","manual sync bypasses the quiet period and uploads immediately");
            string plainName=state.Notes.Single(p=>p.Value.RemoteId==Server.Second).Key;
            using(var n=new note(plainName)){
                n.Show();Application.DoEvents();Wait(sync.SyncNow());var schedule=(SyncSchedule)typeof(CloudSync).GetField("schedule",Flags).GetValue(sync);
                Check(!schedule.HasPendingUpload,"opening and synchronizing an existing note leaves no pending upload: "+sync.Status);
                n.Left+=12;n.SaveCurrentNoteSafe();Check(!schedule.HasPendingUpload,"moving a saved window only stores its local layout and does not schedule cloud traffic");
                var colors=(List<Button>)typeof(note).GetField("swatchButtons",Flags).GetValue(n);typeof(Button).GetMethod("OnClick",Flags).Invoke(colors[4],new object[]{EventArgs.Empty});n.SaveCurrentNoteSafe();
                Check(schedule.HasPendingUpload,"a real theme change still schedules a cloud update");n.Close();
            }
            Wait(sync.SyncNow());Wait(sync.SignOut());
        }
    }
    static void ResumeDownload(){
        CloudStore.ClearSession();var server=new Server{FailSecondPage=true};server.AddHistory();
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SignIn(Session(server)));var state=CloudStore.ReadState(server.User);
            Check(state.Cursor=="0"&&!state.BootstrapComplete&&state.BootstrapCursor=="21"&&state.BootstrapAfter==Server.First,"interrupted first download durably retains its page and initial checkpoint");
        }
        // The next page reflects changes made by another device during downtime.
        server.Change(Server.First,21,"concurrent update",false);server.Change(Server.Second,2,"removed",false,true);server.Change(Server.Third,1,"new while downloading",false);
        using(var sync=new CloudSync(new CloudApi(server),false)){
            Wait(sync.SyncNow());var state=CloudStore.ReadState(server.User);
            Check(state.BootstrapComplete&&server.AfterAnchors.Last()==Server.First&&server.PulledFrom.All(c=>c>=21),"restart resumes the original page and replays only changes after its pinned checkpoint");
            string path=Path.Combine(InitConfig.GetConfigValue("notesPath"),state.Notes.Single(p=>p.Value.RemoteId==Server.First).Key);
            using(var editor=new RichTextBox()){var h=editor.Handle;editor.LoadFile(path);Check(editor.Text.Contains("concurrent update"),"concurrent cloud edits are not lost between latest-note pages and incremental catchup");}
            Check(state.Notes.Values.Count(r=>!r.Deleted)==2&&state.Notes.Values.Any(r=>r.RemoteId==Server.Third),"concurrent deletion and creation leave exactly the current live notes");Wait(sync.SignOut());
        }
    }
    [STAThread]static int Main(string[] args){try{Directory.CreateDirectory(args[0]);Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",args[0]);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());InitConfig.InitSettings();Timing();SaveTiming();LatestDownload();ResumeDownload();Console.WriteLine("All "+checks+" sync policy checks passed. Data: "+args[0]);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
    sealed class Server:HttpMessageHandler{
        internal const string First="10000000-0000-4000-8000-000000000001",Second="20000000-0000-4000-8000-000000000002",Third="30000000-0000-4000-8000-000000000003";
        internal readonly string User=Guid.NewGuid().ToString(),SessionId=Guid.NewGuid().ToString(),ImageId=Guid.NewGuid().ToString();
        internal readonly Dictionary<string,JObject> Notes=new Dictionary<string,JObject>();internal readonly List<JObject> Changes=new List<JObject>();
        internal readonly List<long> PulledFrom=new List<long>();internal readonly List<string> AfterAnchors=new List<string>();
        internal int BootstrapCalls,Downloads,Pushes;internal bool FailSecondPage;readonly byte[] image;
        internal Server(){using(var bmp=new Bitmap(20,16))using(var stream=new MemoryStream()){using(var g=Graphics.FromImage(bmp))g.Clear(Color.Coral);bmp.Save(stream,ImageFormat.Png);image=stream.ToArray();}}
        internal void Change(string id,long revision,string text,bool picture,bool deleted=false){var note=new JObject{["id"]=id,["revision"]=revision,["document"]=new JObject{["title"]=text,["text"]=text,["themeId"]=2,["attachments"]=picture?new JArray(ImageId):new JArray()}};if(deleted)note["deletedAt"]=DateTime.UtcNow;Notes[id]=note;Changes.Add((JObject)note.DeepClone());}
        internal void AddHistory(){for(int n=1;n<=20;n++)Change(First,n,"latest-"+n,true);Change(Second,1,"second latest",false);}
        static HttpResponseMessage Json(JObject value)=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(value.ToString(),Encoding.UTF8,"application/json")};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel){
            string path=request.RequestUri.AbsolutePath.Substring(CloudApi.Prefix.Length);var q=System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if(path=="/config")return Json(new JObject{["inlineImageAttachmentsVersion"]=1,["syncBootstrapVersion"]=1});
            if(path=="/auth/me")return Json(new JObject{["user"]=new JObject{["id"]=User}});
            if(path=="/auth/desktop/logout")return Json(new JObject());
            if(path=="/auth/desktop/refresh")return Json(new JObject{["token"]=new string('x',43),["refreshToken"]=new string('r',43),["sessionId"]=SessionId,["protocolVersion"]=2,["expiresIn"]=900,["refreshExpiresIn"]=15552000,["user"]=new JObject{["id"]=User,["email"]="sync-policy@example.invalid"}});
            if(path=="/sync/bootstrap"){
                BootstrapCalls++;string after=q["after"];AfterAnchors.Add(after);
                if(FailSecondPage&&after!=null){FailSecondPage=false;throw new HttpRequestException("interrupted page fixture");}
                string checkpoint=q["cursor"]??Changes.Count.ToString();var live=Notes.Values.Where(n=>n["deletedAt"]==null&&(after==null||string.CompareOrdinal(n.Value<string>("id"),after)>0)).OrderBy(n=>n.Value<string>("id"),StringComparer.Ordinal).ToArray();
                return Json(new JObject{["notes"]=new JArray(live.Take(1).Select(n=>n.DeepClone())),["cursor"]=checkpoint,["hasMore"]=live.Length>1,["nextAfter"]=live.Length>1?live[0].Value<string>("id"):""});
            }
            if(path=="/sync/pull"){int cursor=int.Parse(q["cursor"]);PulledFrom.Add(cursor);return Json(new JObject{["changes"]=new JArray(Changes.Skip(cursor).Select(n=>n.DeepClone())),["cursor"]=Changes.Count.ToString(),["hasMore"]=false});}
            if(path.StartsWith("/attachments/")){Downloads++;return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(image)};}
            if(path=="/sync/push"){
                Pushes++;var op=(JObject)JObject.Parse(await request.Content.ReadAsStringAsync())["operations"][0];string id=op.Value<string>("noteId");long rev=Notes.ContainsKey(id)?Notes[id].Value<long>("revision")+1:1;
                var n=new JObject{["id"]=id,["revision"]=rev,["document"]=op["document"]?.DeepClone()};if(op.Value<string>("action")=="delete")n["deletedAt"]=DateTime.UtcNow;Notes[id]=n;Changes.Add((JObject)n.DeepClone());return Json(new JObject{["results"]=new JArray(new JObject{["status"]="applied",["note"]=n.DeepClone()})});
            }
            throw new Exception("Unexpected fixture request "+path);
        }
    }
}
