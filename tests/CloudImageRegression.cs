using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;
using Newtonsoft.Json.Linq;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class CloudImageRegression
{
    static int checks;
    static void Check(bool value,string description) {if(!value)throw new Exception(description);checks++;Console.WriteLine("PASS "+description);}
    static T Wait<T>(Task<T> task) {while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(2);}return task.GetAwaiter().GetResult();}
    static bool Invalid(Action action) {try{action();return false;}catch(InvalidDataException){return true;}}
    static Bitmap Noise(int width,int height) {
        var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb);
        var bits=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,bitmap.PixelFormat);
        try {
            var row=new byte[bits.Stride];uint seed=0x1234abcd;
            for(int y=0;y<height;y++) {for(int i=0;i<width*3;i++){seed^=seed<<13;seed^=seed>>17;seed^=seed<<5;row[i]=(byte)seed;}Marshal.Copy(row,0,IntPtr.Add(bits.Scan0,y*bits.Stride),row.Length);}
        }finally {bitmap.UnlockBits(bits);}return bitmap;
    }
    static byte[] Pixels(Bitmap image) {
        var bytes=new byte[image.Width*image.Height*3];var bits=image.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try{for(int y=0;y<image.Height;y++)Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),bytes,y*image.Width*3,image.Width*3);}finally{image.UnlockBits(bits);}return bytes;
    }
    static string Goals(string rtf) => string.Join(";",Regex.Matches(rtf,@"\\(?:picwgoal|pichgoal|picscalex|picscaley|piccropl|piccropr|piccropt|piccropb)-?\d+").Cast<Match>().Select(m=>m.Value).OrderBy(s=>s));
    static CloudSession Session()=>new CloudSession{Token=new string('a',43)};
    static string Make(string root,string name,Bitmap bitmap,out string original) {
        string path=Path.Combine(root,name+".rtf");
        using(var editor=new RichTextBox()) {
            var handle=editor.Handle;editor.Text="BEFORE\n";editor.Select(0,6);editor.SelectionFont=new Font(editor.Font,FontStyle.Bold|FontStyle.Italic);
            editor.Select(editor.TextLength,0);note.AppendCloudImage(editor,bitmap);
            var picture=NoteImages.PictureBoxes(editor).Single();
            Check(NoteImages.ResizeTo(editor,picture.Index,new Size(423,287)),name+" stores custom picture display goals");
            editor.Select(editor.TextLength,0);editor.SelectedText="\nAFTER";editor.Select(editor.TextLength-5,5);editor.SelectionColor=Color.DarkRed;
            original=editor.Rtf;editor.SaveFile(path);
        }return path;
    }
    static void RoundTrip(string root,string name,Bitmap bitmap,bool expectOldLimit,Server server,CloudApi api) {
        string original;string path=Make(root,name,bitmap,out original);string local=File.ReadAllText(path);
        string originalGroup=CloudDocuments.Pictures(original).Single();
        if(expectOldLimit)Check(Invalid(()=>CloudDocuments.Pack(original)),name+" reproduces old 2 MiB packed image failure");
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var doc=Wait(CloudDocuments.Export(path,api,Session(),new CloudState(),()=>{},CancellationToken.None));
        Check(doc["rtfImages"] is JArray && doc["rtfImages"].Count()==1,name+" stores image privately with one inline reference");
        string template=CloudDocuments.Unpack(doc.Value<string>("rtfGzip"));
        Check(template.Length<10000 && !template.Contains(@"\pict"),name+" template contains no duplicate image payload");
        Check(File.ReadAllText(path)==local,name+" exporting never modifies local original");
        var restored=Wait(CloudDocuments.Import(doc,api,Session().Token,CancellationToken.None));
        var restoredGroup=CloudDocuments.Pictures(restored).Single();
        Check(Goals(originalGroup)==Goals(restoredGroup),name+" preserves exact display goals and picture properties");
        using(var before=NoteImages.Extract(originalGroup))using(var after=NoteImages.Extract(restoredGroup)) {
            var one=Pixels(before);var two=Pixels(after);Check(before.Size==after.Size && one.SequenceEqual(two),name+" preserves every original image pixel without downsampling");
        }
        Check(restored.IndexOf("BEFORE",StringComparison.Ordinal)<restored.IndexOf(@"\pict",StringComparison.Ordinal) && restored.IndexOf(@"\pict",StringComparison.Ordinal)<restored.IndexOf("AFTER",StringComparison.Ordinal),name+" keeps image inline between original text");
        using(var editor=new RichTextBox()) {
            var handle=editor.Handle;editor.Rtf=restored;editor.Select(editor.Find("BEFORE"),6);
            Check(editor.SelectionFont.Bold && editor.SelectionFont.Italic,name+" preserves text formatting");
            editor.Select(editor.Find("AFTER"),5);Check(editor.SelectionColor.ToArgb()==Color.DarkRed.ToArgb(),name+" preserves text after image and its color");
            Check(NoteImages.PictureBoxes(editor).Count==1,name+" restores one native RichEdit image");
            var bounds=NoteImages.PictureBoxes(editor).Single().Bounds;
            Check(Math.Abs(bounds.Width-423)<=2 && Math.Abs(bounds.Height-287)<=2,name+" native image occupies the original resized area");
            using(var loaded=NoteImages.Extract(CloudDocuments.Pictures(editor.Rtf).Single()))using(var before=NoteImages.Extract(originalGroup))
                Check(loaded.Size==before.Size && Pixels(loaded).SequenceEqual(Pixels(before)),name+" actual RichEdit load/save keeps every pixel unchanged");
        }
        Console.WriteLine("Elapsed "+name+": "+watch.ElapsedMilliseconds+" ms");
    }
    [STAThread]static int Main(string[] args) {
        try {
            string root=args[0];Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",root);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());InitConfig.InitSettings();
            var server=new Server();using(var api=new CloudApi(server)) {
                using(var noise=Noise(1100,800))RoundTrip(root,"high-entropy",noise,true,server,api);
                using(var image=new Bitmap(3840,2160)) {
                    using(var g=Graphics.FromImage(image)){g.Clear(Color.LightBlue);g.FillRectangle(Brushes.DarkGreen,80,70,1900,700);g.DrawLine(Pens.Red,0,0,3839,2159);}
                    RoundTrip(root,"4K",image,false,server,api);
                }
                Check(server.ConfigCalls==1,"capability cached across multiple note exports");
                Check(server.CompatibleHeaders,"every desktop image/config request advertises safe inline reconstruction support");
                string id=server.Images.Keys.First(),marker=Guid.NewGuid().ToString();
                var doc=new JObject { ["title"]="test",["text"]="test",["attachments"]=new JArray(id),["rtfGzip"]=CloudDocuments.Pack(@"{\rtf1{\*\kimimage "+marker+"}}"),["rtfImages"]=new JArray(new JObject{["marker"]=marker,["attachmentId"]=id,["properties"]=@"\picwgoal400\pichgoal300"})};
                var missing=(JObject)doc.DeepClone();missing.Remove("rtfImages");Check(Invalid(()=>Wait(CloudDocuments.Import(missing,api,null,CancellationToken.None))),"missing image reference rejected without silently importing blank images");
                var duplicate=(JObject)doc.DeepClone();((JArray)duplicate["rtfImages"]).Add(duplicate["rtfImages"][0].DeepClone());Check(Invalid(()=>Wait(CloudDocuments.Import(duplicate,api,null,CancellationToken.None))),"duplicate image reference rejected");
                var foreign=(JObject)doc.DeepClone();foreign["attachments"]=new JArray();Check(Invalid(()=>Wait(CloudDocuments.Import(foreign,api,null,CancellationToken.None))),"unlisted image reference rejected before download");
                var inject=(JObject)doc.DeepClone();inject["rtfImages"][0]["properties"]=@"\object\objdata";Check(Invalid(()=>Wait(CloudDocuments.Import(inject,api,null,CancellationToken.None))),"executable picture properties rejected");
                var unused=(JObject)doc.DeepClone();unused["rtfGzip"]=CloudDocuments.Pack(@"{\rtf1{\*\kimimage "+marker+@"}{\*\kimimage "+Guid.NewGuid()+"}}");Check(Invalid(()=>Wait(CloudDocuments.Import(unused,api,null,CancellationToken.None))),"unused image markers rejected");
                var malformed=(JObject)doc.DeepClone();malformed["rtfGzip"]=CloudDocuments.Pack(@"{\rtf1{\*\kimimage "+marker+"}}}");Check(Invalid(()=>Wait(CloudDocuments.Import(malformed,api,null,CancellationToken.None))),"unbalanced template rejected");
                var cancel=new CancellationTokenSource();cancel.Cancel();bool cancelled=false;try{Wait(CloudDocuments.Import(doc,api,null,cancel.Token));}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancellation stops image import");
                string legacy=@"{\rtf1\ansi old text}";Check(Wait(CloudDocuments.Import(new JObject{["rtfGzip"]=CloudDocuments.Pack(legacy)},api,null,CancellationToken.None))==legacy,"old compressed notes remain readable");
                Check(Wait(CloudDocuments.Import(new JObject{["rtf"]=legacy},api,null,CancellationToken.None))==legacy,"old plain RTF notes remain readable");
                Check(Invalid(()=>CloudDocuments.Pack(@"{\rtf1{\filetbl bad}}")),"file table rejected");
                Check(Invalid(()=>CloudDocuments.Pack(@"{\rtf1\bin4 bad!}")),"binary data control rejected with numeric length");
                using(var packed=new MemoryStream()) {
                    using(var zip=new System.IO.Compression.GZipStream(packed,System.IO.Compression.CompressionMode.Compress,true)) {
                        byte[] chunk=Encoding.ASCII.GetBytes(new string('x',1024*1024));zip.Write(Encoding.ASCII.GetBytes(@"{\rtf1"),0,6);
                        for(int i=0;i<33;i++)zip.Write(chunk,0,chunk.Length);
                    }
                    Check(Invalid(()=>CloudDocuments.Unpack(Convert.ToBase64String(packed.ToArray()))),"compressed template bomb stops at 32 MiB");
                }
                using(var image=new Bitmap(2,2))using(var stream=new MemoryStream()) {
                    image.SetPixel(1,1,Color.Red);image.Save(stream,ImageFormat.Png);stream.Write(new byte[]{12,34,56},0,3);var png=stream.ToArray();
                    var pict=@"{\pict\pngblip "+BitConverter.ToString(png).Replace("-","")+"}";
                    var method=typeof(CloudDocuments).GetMethod("PngBytes",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
                    Check(((byte[])method.Invoke(null,new object[]{pict})).SequenceEqual(png),"existing PNG original bytes and metadata preserved exactly");
                }
            }
            using(var old=new CloudApi(new Server{Capability=false}))using(var small=new Bitmap(20,20)) {
                string original;var path=Make(root,"legacy-server",small,out original);
                var doc=Wait(CloudDocuments.Export(path,old,Session(),new CloudState(),()=>{},CancellationToken.None));
                Check(doc["rtfImages"]==null && CloudDocuments.Pictures(CloudDocuments.Unpack(doc.Value<string>("rtfGzip"))).Count()==1,"old server uses compatible embedded RTF for small notes");
                Check(Invalid(()=>Wait(CloudDocuments.Export(Path.Combine(root,"high-entropy.rtf"),old,Session(),new CloudState(),()=>{},CancellationToken.None))),"old server reports recoverable large-image error instead of sending unreadable template");
            }
            var retryServer=new Server{ConfigFailures=1};using(var retryApi=new CloudApi(retryServer)) {
                bool transient=false;try{Wait(CloudDocuments.Export(Path.Combine(root,"legacy-server.rtf"),retryApi,Session(),new CloudState(),()=>{},CancellationToken.None));}catch(CloudApiException error){transient=error.Status==503;}
                Check(transient && retryServer.Images.Count==0,"temporary capability error does not downgrade or upload duplicate image data");
                var recovered=Wait(CloudDocuments.Export(Path.Combine(root,"legacy-server.rtf"),retryApi,Session(),new CloudState(),()=>{},CancellationToken.None));
                Check(recovered["rtfImages"]!=null && retryServer.ConfigCalls==2,"temporary capability error retried successfully on same API instance");
            }
            Console.WriteLine("PASS "+checks+" cloud image checks");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    sealed class Server:HttpMessageHandler {
        internal readonly Dictionary<string,byte[]> Images=new Dictionary<string,byte[]>();internal int ConfigCalls,ConfigFailures;internal bool Capability=true,CompatibleHeaders=true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel) {
            cancel.ThrowIfCancellationRequested();string path=request.RequestUri.AbsolutePath;
            CompatibleHeaders &= request.Headers.Contains("X-Kim-Inline-Images") && request.Headers.GetValues("X-Kim-Inline-Images").Single()=="1";
            if(path.EndsWith("/config")){ConfigCalls++;if(ConfigFailures-->0)return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("{}")};return Json(new JObject{["inlineImageAttachmentsVersion"]=Capability?1:0});}
            if(path.EndsWith("/attachments")){string id=Guid.NewGuid().ToString();Images[id]=await request.Content.ReadAsByteArrayAsync();return Json(new JObject{["id"]=id});}
            string key=path.Substring(path.LastIndexOf('/')+1);if(Images.ContainsKey(key))return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Images[key])};
            return new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("{}")};
        }
        static HttpResponseMessage Json(JObject value)=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(value.ToString(),Encoding.UTF8,"application/json")};
    }
}

