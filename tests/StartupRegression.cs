using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;

[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class StartupRegression
{
    private static int checks;
    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint mode);
    private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    private static IEnumerable<Control> Tree(Control parent){yield return parent;foreach(Control child in parent.Controls)foreach(var control in Tree(child))yield return control;}
    private sealed class PaintSpy:NativeWindow,IDisposable
    {
        internal readonly Control Control;
        internal int Paints;
        internal PaintSpy(Control control){Control=control;control.HandleCreated+=Created;if(control.IsHandleCreated)AssignHandle(control.Handle);}
        private void Created(object s,EventArgs e){if(Handle==IntPtr.Zero)AssignHandle(Control.Handle);}
        protected override void WndProc(ref Message m){if(m.Msg==0x000f)Paints++;base.WndProc(ref m);}
        public void Dispose(){Control.HandleCreated-=Created;if(Handle!=IntPtr.Zero)ReleaseHandle();}
    }
    private static Bitmap Capture(Control control)
    {
        var image=new Bitmap(control.Width,control.Height);var source=GetDC(control.Handle);
        try{using(var g=Graphics.FromImage(image)){var target=g.GetHdc();try{if(!BitBlt(target,0,0,image.Width,image.Height,source,0,0,0x00cc0020))throw new Exception("Cannot capture fixture control");}finally{g.ReleaseHdc(target);}}}
        finally{ReleaseDC(control.Handle,source);}return image;
    }
    private static bool Same(Bitmap first,Bitmap second)
    {
        if(first.Size!=second.Size)return false;
        for(int y=0;y<first.Height;y++)for(int x=0;x<first.Width;x++)if(first.GetPixel(x,y).ToArgb()!=second.GetPixel(x,y).ToArgb())return false;
        return true;
    }
    private static void Verify(Form form,List<PaintSpy> spies,string name,string root)
    {
        var visible=spies.Where(s=>s.Control.Visible && s.Control.Width>0 && s.Control.Height>0).ToArray();
        var missing=visible.Where(s=>s.Paints==0).Select(s=>s.Control.GetType().Name+":"+(s.Control is ToolIconButton b ? b.IconId : s.Control.Text)).ToArray();
        Console.WriteLine(name+" visible children="+visible.Length+" unpainted="+string.Join(",",missing));
        Check(visible.Length>0 && missing.Length==0,name+" paints every visible native child before returning from Show");
        var images=new Dictionary<Control,Bitmap>();
        try{
            foreach(var spy in visible.Where(s=>s.Control is ToolIconButton || s.Control is Label || s.Control is PictureBox))images[spy.Control]=Capture(spy.Control);
            using(var first=Capture(form))first.Save(Path.Combine(root,name+"-first.png"));
            form.Refresh();bool equal=true;
            foreach(var pair in images)using(var full=Capture(pair.Key)){
                if(!Same(pair.Value,full)){equal=false;Console.WriteLine("Incomplete first pixels "+name+" "+pair.Key.GetType().Name+" "+pair.Key.Text);}
            }
            Check(equal,name+" initial title and action pixels already match a complete repaint");
        }finally{foreach(var image in images.Values)image.Dispose();}
    }
    private static void Show(Func<Form> factory,string name,Screen screen,string root)
    {
        var watch=Stopwatch.StartNew();
        using(var form=factory()){
            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(screen.WorkingArea.Left+30,screen.WorkingArea.Top+30);
            // Some windows choose a default position in Load. Place this fixture
            // after that handler so each case really opens on the requested screen.
            form.Load+=(s,e)=>form.Location=new Point(screen.WorkingArea.Left+30,screen.WorkingArea.Top+30);
            var spies=Tree(form).Where(c=>c!=form).Select(c=>new PaintSpy(c)).ToList();
            Form owner=null;
            try{
                if(form is ScreenshotToolbarForm toolbar){
                    owner=new note {StartPosition=FormStartPosition.Manual,Location=new Point(screen.WorkingArea.Left+30,screen.WorkingArea.Top+30)};
                    owner.Show();owner.Activate();Application.DoEvents();toolbar.Follow(owner);form.Owner=owner;
                }
                int activations=0;form.Activated+=(s,e)=>activations++;
                form.Show();Console.WriteLine(name+" initial frame "+watch.ElapsedMilliseconds+"ms DPI="+form.DeviceDpi+" monitor="+Screen.FromControl(form).DeviceName);
                Check(Screen.FromControl(form).DeviceName==screen.DeviceName,name+" opens on its requested monitor");Verify(form,spies,name,root);Application.DoEvents();
                if(form is ScreenshotToolbarForm)Check(activations==0,name+" preserves screenshot toolbar no-activation behavior");
                form.Close();
            }
            finally{foreach(var spy in spies)spy.Dispose();owner?.Close();}
        }
    }
    [STAThread]private static int Main(string[] args)
    {
        try{
            Directory.CreateDirectory(args[0]);Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",args[0]);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);InitConfig.InitSettings();
            using(var sync=new CloudSync(start:false)){
                int monitor=0;
                foreach(var screen in Screen.AllScreens){
                    string tag="screen-"+(monitor++);
                    Show(()=>new note(),tag+"-note",screen,args[0]);
                    Show(()=>new KimNotes.settings.ConfigForm(),tag+"-settings",screen,args[0]);
                    Show(()=>new KimNotes.settings.AccountForm(sync),tag+"-account",screen,args[0]);
                    Show(()=>new history(InitConfig.GetConfigValue("notesPath")),tag+"-history",screen,args[0]);
                    Show(()=>new TodoListForm(),tag+"-todos",screen,args[0]);
                    Show(()=>new TodoEditDialog("new task",false),tag+"-editor",screen,args[0]);
                    Show(()=>new ScreenshotToolbarForm(action=>{}),tag+"-screenshot-toolbar",screen,args[0]);
                }
                using(var owner=new note())using(var dialog=new TodoEditDialog("modal task",false)){
                    owner.Show();Application.DoEvents();var spies=Tree(dialog).Where(c=>c!=dialog).Select(c=>new PaintSpy(c)).ToList();
                    Exception failure=null;
                    dialog.Shown+=(s,e)=>{try{Verify(dialog,spies,"modal-editor",args[0]);}catch(Exception error){failure=error;}finally{dialog.BeginInvoke((Action)(()=>dialog.Close()));}};
                    try{dialog.ShowDialog(owner);}finally{foreach(var spy in spies)spy.Dispose();owner.Close();}
                    if(failure!=null)throw failure;
                    Check(owner.Enabled,"closing the prepared modal editor restores its owner");
                }
            }
            Console.WriteLine("All "+checks+" initial frame checks passed. Data: "+args[0]);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
