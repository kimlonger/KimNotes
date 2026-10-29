using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class DisplayRegression
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks,failures;
    [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint mode);
    static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    static object Static(string name,params object[] args)=>typeof(ScreenshotHelper).GetMethod(name,Flags).Invoke(null,args);
    static T State<T>(Form form,string key)=>(T)form.Tag.GetType().GetProperty(key).GetValue(form.Tag,null);
    static void SetState(Form form,string key,object value)=>form.Tag.GetType().GetProperty(key).SetValue(form.Tag,value,null);
    static void Check(bool ok,string message){checks++;if(!ok)failures++;Console.WriteLine((ok?"PASS ":"FAIL ")+message);}
    static Bitmap Painted(Control control)
    {
        var image=new Bitmap(control.Width,control.Height);var source=GetDC(control.Handle);
        try{using(var g=Graphics.FromImage(image)){var target=g.GetHdc();try{if(!BitBlt(target,0,0,image.Width,image.Height,source,0,0,0x00cc0020))throw new Exception("Cannot capture fixture pixels");}finally{g.ReleaseHdc(target);}}}
        finally{ReleaseDC(control.Handle,source);}return image;
    }
    static int Difference(Bitmap a,Bitmap b,int inset=0)
    {
        if(a.Size!=b.Size)return int.MaxValue;int count=0;
        for(int y=inset;y<a.Height-inset;y++)for(int x=inset;x<a.Width-inset;x++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())count++;
        return count;
    }
    static void DpiMessage(Form form,int dpi)
    {
        var rect=Marshal.AllocHGlobal(16);
        try{
            int[] values={form.Left,form.Top,form.Left+(int)(form.Width*1.5),form.Top+(int)(form.Height*1.5)};
            Marshal.Copy(values,0,rect,4);
            var message=Message.Create(form.Handle,0x02e0,(IntPtr)(dpi|(dpi<<16)),rect);
            Call(form,"WndProc",message);
        }finally{Marshal.FreeHGlobal(rect);}
    }
    static void Screenshots(string root)
    {
        var source=new Bitmap(320,220);
        for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++)source.SetPixel(x,y,((x+y)&1)==0?Color.Black:Color.White);
        using(var g=Graphics.FromImage(source))using(var font=new Font("Segoe UI",12)){
            g.FillRectangle(Brushes.DarkSlateGray,20,20,220,100);g.DrawString("Sharp pixels 1:1\nWheel + monitor changes",font,Brushes.White,24,24);
        }
        using(var reference=(Bitmap)source.Clone())
        using(var form=(Form)Static("CreateScreenshotForm",source,new Rectangle(40,40,source.Width,source.Height))) {
            SetState(form,"AutoMenu",false);SetState(form,"SuppressHistory",true);
            form.Show();Application.DoEvents();
            var picture=form.Controls.OfType<PictureBox>().Single();var original=picture.Image;
            foreach(var screen in Screen.AllScreens)Console.WriteLine("Monitor "+screen.DeviceName+" "+screen.Bounds);
            bool monitors=true,clean=true;var dpis=new System.Collections.Generic.HashSet<int>();
            for(int round=0;round<4;round++)foreach(var screen in Screen.AllScreens) {
                form.Location=new Point(screen.WorkingArea.Left+40,screen.WorkingArea.Top+40);Application.DoEvents();dpis.Add(form.DeviceDpi);
                monitors &= form.ClientSize==reference.Size && picture.Size==reference.Size && Math.Abs(State<float>(form,"Scale")-1f)<0.0001f;
                form.Refresh();using(var painted=Painted(picture)) {
                    int diff=Difference(painted,reference,4);clean &= diff==0;
                    if(diff!=0){painted.Save(Path.Combine(root,"one-to-one-live.png"));reference.Save(Path.Combine(root,"one-to-one-source.png"));Console.WriteLine("1:1 difference="+diff+" example="+painted.GetPixel(10,10)+"/"+reference.GetPixel(10,10)+" bounds="+picture.Bounds+" dpi="+picture.DeviceDpi);}
                }
            }
            Console.WriteLine("Observed monitor DPI: "+string.Join(",",dpis));
            Check(monitors,"monitor round trips keep the physical 1:1 viewport and zoom");
            Check(clean,"actual 1:1 painted screenshot pixels stay sharp after monitor changes");
            picture.Scale(new SizeF(1.5f,1.5f));
            Check(picture.Size==reference.Size,"DPI child-bound scaling cannot stretch the source pixel canvas");
            bool geometry=true;
            foreach(int dpi in new[]{96,144,120,192,96,144}) {
                DpiMessage(form,dpi);
                geometry &= form.ClientSize==reference.Size && picture.Size==reference.Size;
            }
            Check(geometry,"suggested DPI rectangles cannot alter screenshot zoom or pixel bounds");
            // Deliver a burst, without yielding to the paint queue, exactly as a
            // fast wheel can do after multiple WM_DPICHANGED notifications.
            for(int round=0;round<6;round++) {
                for(int i=0;i<4;i++)Call(picture,"OnMouseWheel",new HandledMouseEventArgs(MouseButtons.None,0,20,20,120));
                for(int i=0;i<6;i++)Call(picture,"OnMouseWheel",new HandledMouseEventArgs(MouseButtons.None,0,20,20,-120));
                Check(form.ClientSize==picture.Size && picture.Location==Point.Empty,"wheel burst keeps parent and picture geometry equal: "+round);
            }
            using(var live=Painted(picture))using(var expected=new Bitmap(picture.Width,picture.Height)) {
                // Capture first: DrawToBitmap repairs damage and would otherwise
                // hide the persistent paint trails this regression is testing.
                picture.DrawToBitmap(expected,picture.ClientRectangle);
                int stale=Difference(live,expected,4);Console.WriteLine("Stale screenshot pixels: "+stale);
                live.Save(Path.Combine(root,"screenshot-wheel-live.png"));
                Check(stale==0,"fast wheel redraw leaves no stale scaled frames in the live pixels");
            }
            Static("ResetZoom",form);
            using(var painted=Painted(picture)){painted.Save(Path.Combine(root,"reset-live.png"));Check(Difference(painted,reference,4)==0,"resetting zoom recovers every interior source pixel without repaint assistance");}
            using(var originalPixels=(Bitmap)picture.Image.Clone())Check(ReferenceEquals(original,picture.Image)&&Difference(originalPixels,reference)==0,"display changes never replace, resize or recompress the source image");
            using(var copy=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(copy,form.ClientRectangle);copy.Save(Path.Combine(root,"screenshot-reset.png"));}
            form.Close();
        }
    }
    static void Notes(string root)
    {
        using(var note=new note()) {
            note.Show();Application.DoEvents();
            var bar=(Panel)note.GetType().GetField("toolbarPanel",Flags).GetValue(note);
            var buttons=bar.Controls.OfType<ToolIconButton>().OrderBy(b=>b.Left).ToArray();
            int gap=bar.Width-buttons.Last().Right;
            Check(Math.Abs(gap-buttons.First().Left)<=2,"new note width fits the fixed toolbar with matching left/right padding");
            Check(note.ClientSize.Height>note.ClientSize.Width,"compact note retains portrait proportions");
            bool compact=true;
            foreach(var screen in Screen.AllScreens) {
                note.Location=new Point(screen.WorkingArea.Left+40,screen.WorkingArea.Top+40);Application.DoEvents();
                compact &= Math.Abs((bar.Width-buttons.Last().Right)-buttons.First().Left)<=2 && buttons.All(b=>b.Right<bar.Width);
            }
            Check(compact,"default toolbar padding stays balanced after mixed-DPI monitor transitions");
            using(var image=new Bitmap(note.Width,note.Height)){note.DrawToBitmap(image,note.ClientRectangle);image.Save(Path.Combine(root,"note-compact-default.png"));}
            var editor=(RichTextBox)note.GetType().GetField("richTextBox1",Flags).GetValue(note);
            editor.Text="https://note.kimlulu.com/path?q=1\nwww.example.com\nplain text";
            string url=null;int opened=0;
            note.GetType().GetField("openWebsite",Flags).SetValue(note,new Action<string>(value=>{url=value;opened++;}));
            Check(editor.DetectUrls,"note editor detects and exposes clickable website links");
            Call(editor,"OnLinkClicked",new LinkClickedEventArgs("https://note.kimlulu.com/path?q=1"));
            Check(opened==1&&url=="https://note.kimlulu.com/path?q=1","clicking an HTTPS link dispatches its full address to the browser launcher");
            Call(editor,"OnLinkClicked",new LinkClickedEventArgs("www.example.com"));
            Check(opened==2&&url=="https://www.example.com/","www link opens as an HTTPS website");
            foreach(var text in new[]{"javascript:alert(1)","file:///C:/Windows/notepad.exe","cmd:run","plain text",""})Call(editor,"OnLinkClicked",new LinkClickedEventArgs(text));
            Check(opened==2,"non-website link content cannot execute a local program or arbitrary URI scheme");
            Check(editor.Text.Contains("plain text"),"opening a link does not replace or remove note text");
            note.Close();
        }
        using(var note=new note()) {
            var editor=(RichTextBox)note.GetType().GetField("richTextBox1",Flags).GetValue(note);
            editor.Text="Saved before display";editor.SelectAll();
            using(var font=new Font(editor.Font,FontStyle.Bold|FontStyle.Italic))editor.SelectionFont=font;
            Call(note,"SaveCurrentNoteSafe");
            note.Show();Application.DoEvents();editor.SelectAll();
            Check(editor.Text=="Saved before display" && editor.SelectionFont.Bold && editor.SelectionFont.Italic,"initial display uses the latest saved content and preserves mixed character styles");
            note.Close();
        }
        string name="before-show-edit.rtf",path=Path.Combine((string)typeof(note).Assembly.GetType("KimNotes.utils.InitConfig").GetMethod("GetConfigValue",Flags).Invoke(null,new object[]{"notesPath"}),name);
        using(var editor=new RichTextBox()) {var handle=editor.Handle;editor.Text="Saved text";editor.SaveFile(path);}
        using(var note=new note(name)) {
            var editor=(RichTextBox)note.GetType().GetField("richTextBox1",Flags).GetValue(note);
            editor.Select(editor.TextLength,0);editor.SelectedText=" + edit before display";
            note.Show();Application.DoEvents();
            Check(editor.Text=="Saved text + edit before display","initial DPI restoration cannot overwrite edits made before Show");
            note.Close();
        }
    }
    [STAThread]static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",args[0]);Directory.CreateDirectory(args[0]);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try{
            typeof(note).Assembly.GetType("KimNotes.utils.InitConfig").GetMethod("InitSettings",Flags).Invoke(null,null);
            Screenshots(args[0]);if(args.Length<2)Notes(args[0]);
            Console.WriteLine("Display regression: "+checks+" checks, "+failures+" failed; "+args[0]);return failures==0?0:1;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
