// Render demonstration data with the shipped v1.4.11 WinForms controls.
// This does not capture the desktop, show windows, or read the user's data.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;

internal static class ExportProductAssets
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Assembly App = typeof(note).Assembly;
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Range { public int Min, Max; }
    [StructLayout(LayoutKind.Sequential)] struct FormatRange { public IntPtr Hdc, Target; public Rect Area, Page; public Range Range; }
    [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern IntPtr FormatMessage(IntPtr window,int message,IntPtr render,ref FormatRange range);
    [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern IntPtr ReleaseFormat(IntPtr window,int message,IntPtr render,IntPtr range);
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    static void Static(string type, string method, params object[] args) => App.GetType(type).GetMethod(method, Flags).Invoke(null, args);
    static IEnumerable<Control> Controls(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls)
            foreach (var nested in Controls(child)) yield return nested;
    }
    static void Prepare(Control root)
    {
        foreach (Control control in Controls(root).ToArray())
        {
            var handle = control.Handle;
            control.PerformLayout();
        }
        root.PerformLayout();
    }
    static void DoubleFonts(Control root)
    {
        var controls = Controls(root).ToArray();
        var originals = controls.ToDictionary(c => c, c => c.Font);
        foreach (var control in controls)
            if (control is ContainerControl) ((ContainerControl)control).AutoScaleMode = AutoScaleMode.None;
        foreach (var control in controls)
            control.Font = new Font(originals[control].FontFamily, originals[control].SizeInPoints * 2, originals[control].Style);
    }
    static Bitmap Render(Form form)
    {
        Prepare(form);
        // Let layout code query effective visibility while the native root window
        // remains hidden. No Show/ShowWindow, desktop capture or input injection.
        var setState=typeof(Control).GetMethod("SetState",BindingFlags.Instance|BindingFlags.NonPublic);
        setState.Invoke(form,new object[]{2,true});
        try
        {
            if(form is TodoListForm) { Call(form,"Render"); Prepare(form); }
            form.PerformLayout();
            var image = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            foreach(var header in Controls(form).Where(c=>c.GetType().Name=="GroupHeader"))
            {
                int x=0,y=0;Control child=header;
                while(child!=form) { x+=child.Left;y+=child.Top;child=child.Parent; }
                header.DrawToBitmap(image,new Rectangle(x,y,header.Width,header.Height));
            }
            foreach(var editor in Controls(form).OfType<RichTextBox>())
            {
                int x=0,y=0; Control child=editor;
                while(child!=form) { x+=child.Left;y+=child.Top;child=child.Parent; }
                using(var graphics=Graphics.FromImage(image))
                {
                    var rect=new Rect{Left=(x+2)*15,Top=y*15,Right=(x+editor.Width-2)*15,Bottom=(y+editor.Height)*15};
                    var hdc=graphics.GetHdc();
                    try
                    {
                        var range=new FormatRange{Hdc=hdc,Target=hdc,Area=rect,Page=rect,Range=new Range{Min=0,Max=-1}};
                        FormatMessage(editor.Handle,0x0439,(IntPtr)1,ref range);
                        ReleaseFormat(editor.Handle,0x0439,IntPtr.Zero,IntPtr.Zero);
                    }
                    finally { graphics.ReleaseHdc(hdc); }
                }
            }
            return image;
        }
        finally { setState.Invoke(form,new object[]{2,false}); }
    }
    static Bitmap Note(int width, int height, string content, bool mint)
    {
        using (var form = new note())
        {
            form.AutoScaleMode = AutoScaleMode.None;
            foreach (var timer in new[] {"idleTimer","autoSaveTimer","chromeAnimationTimer"}) Field<Timer>(form,timer).Stop();
            Call(form,"ApplyTheme",NoteTheme.All[mint ? 2 : 0]);
            form.ClientSize = new Size(width,height);
            DoubleFonts(form);
            var body = Field<RichTextBox>(form,"richTextBox1");
            body.Font = new Font("Microsoft YaHei UI",21f);
            body.Text = content;
            body.SelectAll(); body.SelectionColor = NoteTheme.All[mint ? 2 : 0].Text; body.Select(0,0);
            Call(form,"ReconcileDpiLayout",192);
            foreach (var button in Controls(form).OfType<ToolIconButton>()) Set(button,"<IconScaleOverride>k__BackingField",2f);
            Call(form,"Touch"); Call(form,"AdvanceChromeAnimation",DateTime.UtcNow.AddSeconds(2));
            Call(form,"SaveCurrentNoteSafe");
            var result = Render(form);
            Console.WriteLine("note " + result.Width + "x" + result.Height);
            return result;
        }
    }
    [STAThread]
    static int Main(string[] args)
    {
        string data = args[0], output = args[1];
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",data);
        Directory.CreateDirectory(output);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Static("KimNotes.utils.InitConfig","InitSettings");
            var config = (Dictionary<string,string>)App.GetType("KimNotes.utils.InitConfig").GetField("configMap",Flags).GetValue(null);
            config["checkBox3"]="false"; config["theme"]="2";
            App.GetType("KimNotes.utils.UiDpi").GetField("factor",Flags).SetValue(null,(float?)2f);
            using (var image = Note(672,896,"今天的小计划\n\n整理本周的项目进度\n记录会议中的新想法\n把截图和资料放在一起\n\n灵感随手记，重要的事慢慢完成。",true))
                image.Save(Path.Combine(output,"note-mint-2x.png"),ImageFormat.Png);
            using (var background = Note(1000,720,"会议记录\n\n项目进度：周五完成界面优化\n设计反馈：减少操作，突出重点\n\n下一步\n整理资料，并将截图插入便签。",false))
            {
                var type = App.GetType("KimNotes.utils.ScreenOverlay");
                using (var overlay = (Form)Activator.CreateInstance(type,Flags,null,new object[]{new Rectangle(Point.Empty,background.Size),new Bitmap(background)},null))
                {
                    Set(overlay,"selectionRect",new Rectangle(24,104,952,430));
                    DoubleFonts(overlay); Prepare(overlay);
                    var tools = Field<Control>(overlay,"selectionTools");
                    Call(tools,"Arrange",2f,952);
                    tools.Location=new Point(overlay.Width-tools.Width-24,550); tools.Visible=true;
                    using(var image=Render(overlay)) image.Save(Path.Combine(output,"screenshot-selection-2x.png"),ImageFormat.Png);
                }
            }
            var store=App.GetType("KimNotes.utils.TodoStore");
            store.GetField("items",Flags).SetValue(null,new List<TodoItem>());
            store.GetField("nextId",Flags).SetValue(null,1);
            var first=(TodoItem)store.GetMethod("Add",Flags).Invoke(null,new object[]{"整理会议记录",DateTime.Today.AddDays(1).AddHours(9),null,null});
            first.Repeat=RepeatRule.Weekdays;
            store.GetMethod("Add",Flags).Invoke(null,new object[]{"确认设计反馈",DateTime.Today.AddDays(1).AddHours(14),null,null});
            var done=(TodoItem)store.GetMethod("Add",Flags).Invoke(null,new object[]{"收集项目资料",null,null,null});
            done.Done=true; done.DoneAt=DateTime.Now;
            using(var list=new TodoListForm())
            {
                list.AutoScaleMode=AutoScaleMode.None;
                foreach(var field in list.GetType().GetFields(Flags).Where(f=>f.FieldType==typeof(Font)))
                {
                    var font=(Font)field.GetValue(list);
                    if(font!=null) field.SetValue(list,new Font(font.FontFamily,font.SizeInPoints*2,font.Style));
                }
                DoubleFonts(list); Call(list,"Render");
                using(var image=Render(list)) image.Save(Path.Combine(output,"todo-list-2x.png"),ImageFormat.Png);
            }
            var dialogType=App.GetType("KimNotes.utils.TodoEditDialog");
            using(var dialog=(Form)Activator.CreateInstance(dialogType,new object[]{"整理会议记录",false}))
            {
                Call(dialog,"ApplyPreset",3,(DateTime?)DateTime.Today.AddDays(1).AddHours(9));
                Call(dialog,"SetRepeat",RepeatRule.Weekdays);
                DoubleFonts(dialog);
                Call(dialog,"LayoutBody");
                using(var image=Render(dialog)) image.Save(Path.Combine(output,"todo-reminder-2x.png"),ImageFormat.Png);
            }
            Console.WriteLine("Saved lossless previews from " + App.GetName().Version);
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
    }
}
