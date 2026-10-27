using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes;
using KimNotes.utils;

internal static class ExperienceRegression
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target,int x,int y,int width,int height,IntPtr source,int sx,int sy,uint raster);
    static Bitmap PaintedPixels(Control control)
    {
        var bitmap=new Bitmap(control.ClientSize.Width,control.ClientSize.Height);
        var source=GetDC(control.Handle);
        try {using(var g=Graphics.FromImage(bitmap)) {var target=g.GetHdc();try {if(!BitBlt(target,0,0,bitmap.Width,bitmap.Height,source,0,0,0x00cc0020))throw new Exception("capture fixture pixels");}finally {g.ReleaseHdc(target);}}}
        finally {ReleaseDC(control.Handle,source);}
        return bitmap;
    }
    static int StalePicturePixels(RichTextBox editor,Bitmap bitmap)
    {
        var frame=editor.Controls.Cast<Control>().Where(c=>c.Visible && c.Name.StartsWith("note-image-")).Select(c=>Rectangle.Inflate(c.Bounds,1,1)).ToArray();
        int count=0,accent=Color.FromArgb(74,127,193).ToArgb();
        for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)
            if(bitmap.GetPixel(x,y).ToArgb()==accent && !frame.Any(r=>r.Contains(x,y)))count++;
        return count;
    }
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly Assembly App = typeof(note).Assembly;
    static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    static object Static(string type, string name, params object[] args) => App.GetType(type).GetMethod(name, Flags).Invoke(null, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
    static string PathOf(note n) => (string)typeof(note).GetProperty("FilePath", Flags).GetValue(n, null);
    static RichTextBox Body(note n) => Field<RichTextBox>(n, "richTextBox1");
    static Rectangle PictureBounds(RichTextBox editor,int index)
    {
        foreach(var item in (System.Collections.IEnumerable)Static("KimNotes.utils.NoteImages","PictureBoxes",editor))
            if((int)item.GetType().GetField("Index",Flags).GetValue(item)==index)return (Rectangle)item.GetType().GetField("Bounds",Flags).GetValue(item);
        return Rectangle.Empty;
    }
    static void DragPictureHandle(Control handle,int x,int y)
    {
        var center=new Point(handle.Width/2,handle.Height/2);
        Call(handle,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,center.X,center.Y,0));
        Call(handle,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,center.X+x,center.Y+y,0));
        Call(handle,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,center.X,center.Y,0));
        Application.DoEvents();
    }

    internal static void Run(string root, Action<bool, string> check)
    {
        LayoutAndSave(root, check);
        Editing(check);
        Images(check);
        History(root, check);
        Repeats(check);
        Dialogs(root, check);
        HistoryTypography(root, check);
        ShortcutMigration(check);
    }

    static void LayoutAndSave(string root, Action<bool, string> check)
    {
        string path; Rectangle bounds;
        using (var n = new note())
        {
            n.Show(); n.SetText("布局与自动保存测试"); Application.DoEvents();
            var wa = Screen.FromControl(n).WorkingArea;
            n.Bounds = new Rectangle(wa.Left + 70, wa.Top + 50, 500, 560);
            n.TopMost = true;
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            path = PathOf(n); bounds = n.Bounds;
            check(File.Exists(path) && !Field<bool>(n, "hasUnsavedChanges") && Field<Label>(n, "saveStatus").Text == "已保存", "input pause saves the document and reports saved status");
            byte[] bytes = File.ReadAllBytes(path);
            int changed = 0;
            Action onChanged = () => changed++;
            var notesChanged = App.GetType("KimNotes.utils.Storage").GetEvent("NotesChanged", Flags);
            notesChanged.AddEventHandler(null, onChanged);
            n.Left += 12; Call(n, "SaveCurrentNoteSafe"); bounds = n.Bounds;
            notesChanged.RemoveEventHandler(null, onChanged);
            check(bytes.SequenceEqual(File.ReadAllBytes(path)), "layout-only persistence does not rewrite or replace the rich-text backup");
            check(changed == 0, "moving a saved note does not rebuild every open note-list preview");
            Call(n, "OnResizeBegin", EventArgs.Empty);
            n.SetText("拖动过程中延后保存");
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(10));
            check(Field<bool>(n, "hasUnsavedChanges"), "interactive window movement defers disk saves until the drag finishes");
            Call(n, "OnResizeEnd", EventArgs.Empty);
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            check(!Field<bool>(n, "hasUnsavedChanges"), "autosave resumes after interactive movement ends");
            n.Close();
        }
        using (var n = new note(Path.GetFileName(path)))
        {
            n.Show(); Application.DoEvents();
            Console.WriteLine("Layout reopened "+n.Bounds+" dpi="+n.DeviceDpi+" expected="+bounds+" pinned="+n.TopMost);
            check(n.Bounds == bounds && n.TopMost, "reopened note restores saved monitor position, size and pin state");
            n.SetText("停顿后更新正文");
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddMilliseconds(200));
            check(Field<bool>(n, "hasUnsavedChanges"), "autosave debounce does not write immediately on every keystroke");
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            check(!Field<bool>(n, "hasUnsavedChanges"), "autosave persists updated content after the idle interval");
            string held = path + ".autosave-held";
            File.Move(path, held); Directory.CreateDirectory(path);
            try
            {
                n.SetText("保存失败后仍保留的正文");
                Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
                check(Field<bool>(n, "hasUnsavedChanges") && Field<Label>(n, "saveStatus").Text == "保存失败",
                    "autosave failure retains unsaved contents and exposes a failure status");
            }
            finally { Directory.Delete(path); File.Move(held, path); }
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2));
            check(!Field<bool>(n, "hasUnsavedChanges") && Field<Label>(n, "saveStatus").Text == "已保存", "autosave retries after storage becomes writable without requiring another edit");
            var session = (System.Collections.Generic.List<string>)Static("KimNotes.utils.DesktopSession", "Read");
            check(session.Contains(path), "desktop session records the saved open note for the next application startup");
            var type = App.GetType("KimNotes.utils.DesktopSession");
            type.GetField("Exiting", Flags).SetValue(null, true);
            n.Close();
            check(((System.Collections.Generic.List<string>)Static("KimNotes.utils.DesktopSession", "Read")).Contains(path), "application shutdown preserves open-note session instead of clearing it while closing windows");
            type.GetField("Exiting", Flags).SetValue(null, false);
        }
    }

    static void Images(Action<bool, string> check)
    {
        using (var n = new note())
        using (var source = new Bitmap(80, 60))
        {
            using (var g = Graphics.FromImage(source)) { g.Clear(Color.Coral); g.FillRectangle(Brushes.Navy, 0, 0, 20, 20); }
            n.Show();
            var imageMonitor=Screen.AllScreens.OrderByDescending(screen=>screen.WorkingArea.Left).First().WorkingArea;
            n.Location=new Point(imageMonitor.Left+45,imageMonitor.Top+45);Application.DoEvents();
            n.SetText("图片之前\n");
            Call(n, "AppendImage", source);
            var editor = Body(n);
            editor.AppendText("\n图片之后");
            int index = Enumerable.Range(0, editor.TextLength).First(i => Static("KimNotes.utils.NoteImages", "SelectedPicture", editor, i) != null);
            var pictureBounds=PictureBounds(editor,index);
            var middle=new Point(pictureBounds.Left+pictureBounds.Width/2,pictureBounds.Top+pictureBounds.Height/2);
            check((int)Static("KimNotes.utils.NoteImages","PictureAt",editor,middle)==index,
                "clicking the middle of a tall embedded image resolves its object rather than the following character");
            check((int)Static("KimNotes.utils.NoteImages","PictureAt",editor,new Point(pictureBounds.Right+20,middle.Y))==-1,
                "blank space beside a picture cannot accidentally operate on that picture");
            Call(editor,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,middle.X,middle.Y,0));
            editor.ContextMenuStrip.Show(editor,middle);Application.DoEvents();
            var copy=editor.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i=>i.Text=="复制图片");
            check(copy.Visible,"right-clicking the image center exposes the actual image actions");
            check(editor.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Where(i=>i.Visible).Select(i=>i.Text).SequenceEqual(new[]{"复制图片","查看大图","删除图片"}),
                "image menu contains only copy, view and delete in the requested order");
            check(editor.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Where(i=>new[]{"撤销","复制","粘贴","全选"}.Contains(i.Text)).All(i=>!i.Visible),
                "image context menu hides text editing commands and ambiguous duplicate copy actions");
            using(var menuImage=new Bitmap(editor.ContextMenuStrip.Width,editor.ContextMenuStrip.Height)) {
                editor.ContextMenuStrip.DrawToBitmap(menuImage,editor.ContextMenuStrip.ClientRectangle);
                menuImage.Save(Path.Combine(Path.GetDirectoryName(PathOf(n)),"picture-menu.png"));
            }
            copy.PerformClick();editor.ContextMenuStrip.Close();
            var blank=new Point(pictureBounds.Right+20,middle.Y);
            Call(editor,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,blank.X,blank.Y,0));
            editor.ContextMenuStrip.Show(editor,blank);Application.DoEvents();
            check(!copy.Visible && editor.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Where(i=>new[]{"撤销","复制","粘贴","全选"}.Contains(i.Text)).All(i=>i.Visible),
                "text and blank-area context menus retain normal text actions without image commands");
            editor.ContextMenuStrip.Close();
            using(var copied=(Bitmap)Clipboard.GetImage())check(copied!=null && copied.Size==source.Size && copied.GetPixel(10,10).ToArgb()==Color.Navy.ToArgb(),
                "right-click copy puts the source bitmap on the system clipboard for other applications");
            editor.Select(index,1);
            var keyMessage=Message.Create(editor.Handle,0x0100,(IntPtr)Keys.C,IntPtr.Zero);
            check((bool)Call(editor,"ProcessCmdKey",keyMessage,Keys.Control|Keys.C) && Clipboard.ContainsImage(),
                "Ctrl+C on a selected image is handled before native rich-text copying");
            editor.Select(0,2);
            check(!(bool)Static("KimNotes.utils.NoteImages","CopySelection",editor),"ordinary text selection retains its normal clipboard behavior");
            Call(editor,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,middle.X,middle.Y,0));Application.DoEvents();
            var handles=editor.Controls.Cast<Control>().Where(c=>c.Name.StartsWith("note-image-handle-")).ToArray();
            check(handles.Count(c=>c.Visible)==8,"selecting a note picture exposes corner and edge resize handles");
            var origin=n.Location;bool aligned=true;
            foreach(var screen in Screen.AllScreens) {
                n.Location=new Point(screen.WorkingArea.Left+45,screen.WorkingArea.Top+45);Application.DoEvents();
                var corner=handles.Single(c=>c.Name=="note-image-handle-4");
                var current=PictureBounds(editor,index);
                var until=DateTime.UtcNow.AddSeconds(2);
                while((!corner.Visible || Math.Abs(corner.Left+corner.Width/2-current.Right)>1 || Math.Abs(corner.Top+corner.Height/2-current.Bottom)>1) && DateTime.UtcNow<until) {
                    Application.DoEvents();System.Threading.Thread.Sleep(2);current=PictureBounds(editor,index);
                }
                if(!corner.Visible || Math.Abs(corner.Left+corner.Width/2-current.Right)>1 || Math.Abs(corner.Top+corner.Height/2-current.Bottom)>1)
                    Console.WriteLine("Image handle mismatch "+screen.DeviceName+" dpi="+n.DeviceDpi+" picture="+current+" handle="+corner.Bounds+" visible="+corner.Visible);
                aligned &= corner.Visible && Math.Abs(corner.Left+corner.Width/2-current.Right)<=1 && Math.Abs(corner.Top+corner.Height/2-current.Bottom)<=1;
            }
            n.Location=origin;Application.DoEvents();
            check(aligned,"selected picture resize handles stay attached to actual image bounds after real monitor DPI transitions");
            var paintedHandle=handles.Single(c=>c.Name=="note-image-handle-4");
            var center=new Point(paintedHandle.Width/2,paintedHandle.Height/2);
            var dragScreen=paintedHandle.PointToScreen(center);
            string beforePreview=editor.Rtf;
            n.TopMost=true;n.Activate();editor.Refresh();Application.DoEvents();
            Call(paintedHandle,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,center.X,center.Y,0));
            for(int step=1;step<=20;step++) {
                var at=paintedHandle.PointToClient(new Point(dragScreen.X+step*7,dragScreen.Y+step*4));
                Call(paintedHandle,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,at.X,at.Y,0));
            }
            using(var painted=PaintedPixels(editor)) {
                int stale=StalePicturePixels(editor,painted);
                painted.Save(Path.Combine(Path.GetDirectoryName(PathOf(n)),"image-resize-live-paint.png"));
                Console.WriteLine("Stale resize pixels: "+stale);
                check(stale==0,"successive live image resize frames erase old borders and handles without trails");
            }
            check(editor.Rtf==beforePreview,"live resize preview never changes the document before release");
            Call(editor,"OnKeyDown",new KeyEventArgs(Keys.Escape));Application.DoEvents();
            using(var painted=PaintedPixels(editor))check(StalePicturePixels(editor,painted)==0,"cancelled image resize erases the preview outline completely");
            var cancelled=PictureBounds(editor,index).Size;
            var cancelHandle=handles.Single(c=>c.Name=="note-image-handle-4");
            Call(cancelHandle,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,2,2,0));
            Call(cancelHandle,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,32,22,0));
            Call(editor,"OnKeyDown",new KeyEventArgs(Keys.Escape));
            check(PictureBounds(editor,index).Size==cancelled && !cancelHandle.Capture,"Escape cancels an image resize preview without changing or saving the picture");
            string originalText=editor.Text;
            DragPictureHandle(handles.Single(c=>c.Name=="note-image-handle-4"),37,29);
            var cornerSize=PictureBounds(editor,index).Size;
            check(cornerSize.Width>pictureBounds.Width && cornerSize.Height>pictureBounds.Height &&
                Math.Abs(cornerSize.Width/(double)cornerSize.Height-pictureBounds.Width/(double)pictureBounds.Height)<0.04 && editor.Text==originalText,
                "dragging a corner changes the actual image dimensions proportionally without changing note text");
            DragPictureHandle(handles.Single(c=>c.Name=="note-image-handle-3"),18,0);
            var edgeSize=PictureBounds(editor,index).Size;
            check(edgeSize.Width>cornerSize.Width && Math.Abs(edgeSize.Height-cornerSize.Height)<=1 && editor.Text==originalText,
                "dragging a side freely changes width while preserving image height and surrounding text");
            editor.Undo();Application.DoEvents();
            check(PictureBounds(editor,index).Size==cornerSize && editor.Text==originalText,"one undo restores the previous picture size without deleting the image");
            var undone=(string)Static("KimNotes.utils.NoteImages","SelectedPicture",editor,index);
            Call(n,"SaveCurrentNoteSafe");
            var expectedGoals=System.Text.RegularExpressions.Regex.Matches(undone,@"\\(?:picwgoal|pichgoal)\d+").Cast<System.Text.RegularExpressions.Match>().Select(m=>m.Value);
            var savedGoals=System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(PathOf(n)),@"\\(?:picwgoal|pichgoal)\d+").Cast<System.Text.RegularExpressions.Match>().Select(m=>m.Value);
            check(expectedGoals.SequenceEqual(savedGoals),"undoing an image resize is saved even though the note text has not changed");
            var rtf = (string)Static("KimNotes.utils.NoteImages", "SelectedPicture", editor, index);
            using (var extracted = (Bitmap)Static("KimNotes.utils.NoteImages", "Extract", rtf))
            {
                Console.WriteLine("Embedded image extraction: " + extracted.Size + "; source=" + source.Size);
                check(extracted != null && extracted.Size == source.Size && extracted.GetPixel(40, 30).ToArgb() == Color.Coral.ToArgb()
                    && extracted.GetPixel(10, 10).ToArgb() == Color.Navy.ToArgb(), "embedded WMF picture extraction preserves original dimensions and distinct source pixels without a desktop-sized blank canvas");
                extracted.Save(Path.Combine(Path.GetDirectoryName(PathOf(n)), "extracted-picture.png"));
            }
            string text = editor.Text;
            var currentSize=PictureBounds(editor,index).Size;
            check((bool)Static("KimNotes.utils.NoteImages", "ResizeTo", editor, index, new Size((int)(currentSize.Width*.75),(int)(currentSize.Height*.75))) && editor.Text == text,
                "resizing one embedded picture preserves surrounding note text");
            Call(n,"SaveCurrentNoteSafe");
            string savedPath=PathOf(n);
            var savedSize=PictureBounds(editor,index).Size;
            using(var reopened=new note(Path.GetFileName(savedPath))) {
                reopened.Show();Application.DoEvents();
                var restored=PictureBounds(Body(reopened),index).Size;
                Console.WriteLine("Reopened picture saved="+savedSize+" dpi="+editor.DeviceDpi+" restored="+restored+" dpi="+Body(reopened).DeviceDpi+" notes="+n.Location+"/"+reopened.Location);
                check(restored==savedSize,"resized image dimensions survive saving and reopening the native note");
                reopened.Close();
            }
            editor.Select(index, 1); editor.SelectedText = "";
            check(editor.Text.Contains("图片之前") && editor.Text.Contains("图片之后") && !editor.Rtf.Contains("\\pict"), "deleting a selected picture preserves the text before and after it");
            n.Close();
        }
    }

    internal static void RunImages(Action<bool,string> check) {Images(check);Editing(check);}

    static void Editing(Action<bool,string> check)
    {
        var notes=new System.Collections.Generic.List<note>();
        try {
            for(int i=0;i<7;i++) {
                var n=new note();notes.Add(n);n.Show();Application.DoEvents();
                check(notes.Take(i).All(old=>old.Location!=n.Location) && Screen.FromControl(n).WorkingArea.Contains(n.Bounds),"new note "+i+" has a distinct visible position inside the monitor");
            }
            var last=notes.Last();var area=Screen.FromControl(last).WorkingArea;
            last.Location=new Point(area.Right-last.Width-3,area.Bottom-last.Height-3);
            var wrap=new note();notes.Add(wrap);wrap.Show();Application.DoEvents();
            check(area.Contains(wrap.Bounds) && wrap.Location!=last.Location,"new note wraps visibly when the previous note is at the screen edge");
        } finally {foreach(var n in notes)n.Dispose();}
        using(var n=new note()) {
            n.Show();Application.DoEvents();var editor=Body(n);
            n.SetText("Alpha中文");
            editor.Select(0,5);using(var f=new Font("Calibri",11f,FontStyle.Bold))editor.SelectionFont=f;
            editor.SelectionColor=Color.DarkRed;
            editor.Select(5,2);using(var f=new Font("Microsoft YaHei UI",12f,FontStyle.Italic))editor.SelectionFont=f;
            editor.SelectionColor=Color.DarkBlue;
            editor.SelectAll();Call(n,"button3_Click",null,EventArgs.Empty);
            editor.Select(0,1);bool first=editor.SelectionFont.Strikeout && editor.SelectionFont.Bold && editor.SelectionColor==Color.DarkRed;
            editor.Select(5,1);bool second=editor.SelectionFont.Strikeout && editor.SelectionFont.Italic && editor.SelectionColor==Color.DarkBlue;
            check(first && second && editor.Text=="Alpha中文","strikethrough preserves mixed fonts, colors and text without case conversion");
            editor.SelectAll();Call(n,"button3_Click",null,EventArgs.Empty);
            bool off=!Field<ToolIconButton>(n,"button4").Selected;
            for(int i=0;i<editor.TextLength;i++){editor.Select(i,1);off &= !editor.SelectionFont.Strikeout;}
            check(off,"strikethrough can be toggled off for the whole selection");
            int requests=0;string source=null,target=null;
            Func<string,string,System.Threading.Tasks.Task<string>> translate=(text,to)=>{requests++;source=text;target=to;return System.Threading.Tasks.Task.FromResult(to=="zh" ? "你好" : "Hello");};
            n.GetType().GetField("translateText",Flags).SetValue(n,translate);
            n.SetText("Hello world");editor.Select(2,0);Call(n,"button4_Click",null,EventArgs.Empty);
            check(requests==0 && editor.Text=="Hello world","translation without a selection does not send a request or change the note");
            foreach(string number in new[]{"123456","1,234.56","2026-10-23","１２３４５","10e3","1e309","一二三"," +123% ","!!!"}) {
                n.SetText(number);editor.SelectAll();Call(n,"button4_Click",null,EventArgs.Empty);
                check(requests==0 && editor.Text==number,"numeric or symbol selection is not translated: "+number);
            }
            n.SetText("Hello world");editor.Select(0,5);Call(n,"button4_Click",null,EventArgs.Empty);
            check(requests==1 && source=="Hello" && target=="zh" && editor.Text=="Hello\n你好 world","only selected English is translated into Chinese after the original text");
            n.SetText("你好，世界");editor.Select(0,2);Call(n,"button4_Click",null,EventArgs.Empty);
            check(requests==2 && source=="你好" && target=="en" && editor.Text=="你好\nHello，世界","selected Chinese is translated into English automatically");
            var completion=new System.Threading.Tasks.TaskCompletionSource<string>();
            n.GetType().GetField("translateText",Flags).SetValue(n,new Func<string,string,System.Threading.Tasks.Task<string>>((text,to)=>completion.Task));
            n.SetText("Wait here");editor.Select(0,4);Call(n,"button4_Click",null,EventArgs.Empty);
            check(!Field<ToolIconButton>(n,"button5").Enabled,"pending translation prevents duplicate requests without blocking editing");
            editor.Select(editor.TextLength,0);editor.SelectedText="!";completion.SetResult("等待");
            var until=DateTime.UtcNow.AddSeconds(3);
            while(!Field<ToolIconButton>(n,"button5").Enabled && DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(2);}
            check(editor.Text=="Wait here!" && Field<ToolIconButton>(n,"button5").Enabled,"late translation never inserts into a changed selection and restores the button");
            n.GetType().GetField("translateText",Flags).SetValue(n,new Func<string,string,System.Threading.Tasks.Task<string>>((text,to)=>System.Threading.Tasks.Task.FromResult<string>(null)));
            editor.SelectAll();string before=editor.Text;Call(n,"button4_Click",null,EventArgs.Empty);
            check(editor.Text==before && Field<ToolIconButton>(n,"button5").Enabled,"empty translation response preserves text and allows retry");
        }
    }

    static void History(string root, Action<bool, string> check)
    {
        using (var a = new Bitmap(60, 50))
        using (var b = new Bitmap(70, 55))
        {
            using (var g = Graphics.FromImage(a)) g.Clear(Color.Red);
            using (var g = Graphics.FromImage(b)) g.Clear(Color.Blue);
            string first = (string)Static("KimNotes.utils.ScreenshotArchive", "Save", a, false, null);
            string second = (string)Static("KimNotes.utils.ScreenshotArchive", "Save", b, false, null);
            var viewer = ScreenshotHelper.RecallLatest(); Application.DoEvents();
            var picture = viewer.Controls.OfType<PictureBox>().Single();
            check(((Bitmap)picture.Image).GetPixel(20, 20).B == 255, "recall opens the newest retained screenshot without taking a new screenshot");
            var oldViewer = viewer;
            ScreenshotHelper.NavigateHistory(1);
            check(ReferenceEquals(oldViewer, viewer) && ((Bitmap)picture.Image).GetPixel(20, 20).R == 255, "previous screenshot replaces the image in the same floating window");
            ScreenshotHelper.NavigateHistory(-1);
            check(((Bitmap)picture.Image).GetPixel(20, 20).B == 255, "next screenshot returns to the newer retained image");
            viewer.Close();
            using (var hotkeys = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.HotKeyHandlerForm"), Flags, null,
                new object[] { new Func<IntPtr, bool>(h => true), new Action<IntPtr>(h => { }) }, null))
            {
                Call(hotkeys, "WndProc", Message.Create(hotkeys.Handle, 0x0312, (IntPtr)2, IntPtr.Zero));
                Application.DoEvents();
                viewer = Application.OpenForms.Cast<Form>().First(f => f.GetType().Name == "ScreenshotForm");
                check(((Bitmap)viewer.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).B == 255, "recall hotkey dispatch reopens retained screenshot without recapturing the desktop");
                Call(hotkeys, "WndProc", Message.Create(hotkeys.Handle, 0x0312, (IntPtr)3, IntPtr.Zero));
                Application.DoEvents();
                check(((Bitmap)viewer.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).R == 255, "previous hotkey dispatch navigates the existing screenshot window");
                viewer.Close();
            }
            viewer = ScreenshotHelper.RecallLatest(); Application.DoEvents();
            check(viewer != null && !viewer.IsDisposed && ((Bitmap)viewer.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).B == 255,
                "closing a floating screenshot keeps its pixels available for recall");
            check(viewer.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == "上一张") &&
                viewer.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == "下一张"), "floating screenshot right-click menu exposes previous and next navigation");
            check(!viewer.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text.Contains("唤回")), "screenshot right-click menu omits redundant recall action");
            viewer.Close();
            int before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
            string secret = (string)Static("KimNotes.utils.ScreenshotArchive", "Save", a, true, null);
            using (var restored = (Bitmap)Static("KimNotes.utils.ScreenshotArchive", "Load", secret, true))
                check(restored != null && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == before,
                    "incognito screenshot recall stores history only in memory");
            using (var green = new Bitmap(40, 40))
            using (var orange = new Bitmap(40, 40))
            {
                using (var g = Graphics.FromImage(green)) g.Clear(Color.Green);
                using (var g = Graphics.FromImage(orange)) g.Clear(Color.Orange);
                var earlier = (Form)Static("KimNotes.ScreenshotHelper", "CreateScreenshotForm", new Bitmap(green), new Rectangle(50, 50, 40, 40));
                var later = (Form)Static("KimNotes.ScreenshotHelper", "CreateScreenshotForm", new Bitmap(orange), new Rectangle(150, 50, 40, 40));
                foreach (var window in new[] { earlier, later })
                { window.Tag.GetType().GetProperty("AutoMenu").SetValue(window.Tag, false, null); window.Show(); Application.DoEvents(); }
                earlier.Activate(); Application.DoEvents();
                var changed = ScreenshotHelper.NavigateHistory(1);
                check(ReferenceEquals(changed, earlier) && ((Bitmap)earlier.Controls.OfType<PictureBox>().Single().Image).GetPixel(20, 20).B == 255,
                    "global navigation follows the last activated floating screenshot when multiple windows exist");
                earlier.Close(); later.Close();
            }
        }
    }

    static void Repeats(Action<bool, string> check)
    {
        var start = new DateTime(2030, 7, 1, 9, 0, 0);
        var item = (TodoItem)Static("KimNotes.utils.TodoStore", "Add", "每日重复测试", start, null, null);
        Static("KimNotes.utils.TodoStore", "SetSchedule", item, start, RepeatRule.Daily);
        var now = start.AddDays(3).AddHours(2);
        var due = (System.Collections.Generic.List<TodoItem>)Static("KimNotes.utils.TodoStore", "TakeDueAt", now);
        check(due.Count(t => t.Id == item.Id) == 1 && item.Due == start.AddDays(3), "missed daily occurrences coalesce into one latest reminder at the original clock time");
        Static("KimNotes.utils.TodoStore", "MarkReminded", due);
        var storeType = App.GetType("KimNotes.utils.TodoStore");
        foreach (string field in new[] { "items", "fired" }) storeType.GetField(field, Flags).SetValue(null, null);
        item = (TodoItem)Static("KimNotes.utils.TodoStore", "Find", item.Id);
        check(item.Repeat == RepeatRule.Daily && item.Due == start.AddDays(3), "repeat rule and normalized occurrence survive a store reload");
        check(!((System.Collections.Generic.List<TodoItem>)Static("KimNotes.utils.TodoStore", "TakeDueAt", now)).Any(t => t.Id == item.Id), "repeat scanning cannot show the same occurrence twice");
        check(((System.Collections.Generic.List<TodoItem>)Static("KimNotes.utils.TodoStore", "TakeDueAt", now.AddDays(1))).Any(t => t.Id == item.Id), "the next daily occurrence is eligible even when the previous task was not completed");
        var friday = new DateTime(2026, 10, 16, 9, 0, 0);
        check((DateTime)Static("KimNotes.utils.TodoStore", "NextOccurrence", friday, RepeatRule.Weekdays, friday.AddHours(2)) == friday.AddDays(3),
            "weekday recurrence skips Saturday and Sunday while preserving the reminder clock time");
        check((DateTime)Static("KimNotes.utils.TodoStore", "NextOccurrence", friday, RepeatRule.Weekly, friday.AddDays(20)) == friday.AddDays(21),
            "weekly recurrence remains anchored to the chosen weekday after a long idle gap");
        var previous = item.Due;
        Static("KimNotes.utils.TodoStore", "SetDone", item, true);
        check(!item.Done && item.Due > previous, "completing a recurring task schedules its next occurrence and keeps the series active");
        Static("KimNotes.utils.TodoStore", "SetSchedule", item, null, RepeatRule.Daily);
        check(item.Repeat == RepeatRule.None && !item.Due.HasValue, "disabling reminders also disables recurrence");
    }

    static void Dialogs(string root, Action<bool, string> check)
    {
        using (var dialog = (Form)Activator.CreateInstance(App.GetType("KimNotes.utils.TodoEditDialog"), new object[] { "工作日检查项目进展", false }))
        {
            dialog.Show(); Application.DoEvents();
            Call(dialog, "SetRepeat", RepeatRule.Weekdays);
            var selector = Field<Button>(dialog, "repeatBox");
            check(selector.Text.Contains("工作日") && dialog.ClientRectangle.Contains(selector.Bounds) && selector.Bottom < Field<Button>(dialog, "btnOk").Top,
                "recurrence selector is visible inside the todo dialog without overlapping its actions");
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "todo-repeat.png")); }
            dialog.Close();
        }
        using (var settings = (Form)Activator.CreateInstance(App.GetType("KimNotes.settings.ConfigForm")))
        {
            settings.Show(); Application.DoEvents();
            var values = Field<System.Collections.Generic.Dictionary<string, string>>(settings, "historyKeyValues");
            var buttons = Field<System.Collections.Generic.Dictionary<string, Button>>(settings, "historyKeyButtons");
            var body = Field<Panel>(settings, "bodyPanel");
            var path = Field<TextBox>(settings, "textBox3");
            var capture = Field<Button>(settings, "keycapBtn");
            check(!body.VerticalScroll.Visible && !body.HorizontalScroll.Visible && path.Parent.Width == capture.Width
                && buttons.Values.All(b => b.Width == capture.Width) && path.Font.Size <= 8.5f,
                "compact settings fit without default scrollbars and align path and shortcut field widths");
            check(values.Count == 3 && buttons.Count == 3 && buttons.Values.All(b => b.Width > 100 && b.Parent.ClientRectangle.Contains(b.Bounds)),
                "settings expose all three configurable screenshot history shortcuts without clipping");
            check(buttons.Values.All(b => b.Text.StartsWith("Ctrl + ") && !b.Text.Contains("Alt")) &&
                Field<Label>(settings, "hintLabel").Bottom < Field<Button>(settings, "keycapBtn").Top,
                "Ctrl-only keycaps use a compact shared hint beside the shortcut section heading");
            var modifiers = (uint)App.GetType("KimNotes.utils.HotKeyHandlerForm").GetField("HistoryModifiers", Flags).GetRawConstantValue();
            check((modifiers & 3) == 2 && (modifiers & 0x4000) != 0, "native screenshot-history registration uses Ctrl only and suppresses key-repeat events");
            string captureKey = Field<TextBox>(settings, "textBox1").Text;
            buttons["recallKey"].PerformClick();
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs((Keys)Enum.Parse(typeof(Keys), captureKey)));
            check(Field<string>(settings, "historyCapturing") == "recallKey" && Field<Label>(settings, "hintLabel").Text.Contains("重复"),
                "history shortcuts reject a collision with the screenshot shortcut before saving");
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.F8));
            Call(settings, "BeginCapture");
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.F8));
            check(Field<TextBox>(settings, "textBox1").Text == captureKey && Field<bool>(settings, "capturing"),
                "screenshot shortcut capture also rejects a collision with a history shortcut");
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.Escape));
            buttons["recallKey"].PerformClick();
            Call(settings, "ConfigForm_KeyDown", settings, new KeyEventArgs(Keys.F2));
            buttons["previousImageKey"].PerformClick();
            buttons["previousImageKey"].Focus();
            bool consumed = (bool)Call(buttons["previousImageKey"], "PreProcessMessage",
                Message.Create(buttons["previousImageKey"].Handle, 0x100, (IntPtr)Keys.Left, IntPtr.Zero));
            check(consumed && values["previousImageKey"] == "Left" && Field<string>(settings, "historyCapturing") == null
                && buttons["previousImageKey"].Text.Contains("←"),
                "focused shortcut button captures Left through real WinForms preprocessing instead of moving focus");
            buttons["nextImageKey"].PerformClick();
            consumed = (bool)Call(settings, "ProcessCmdKey", Message.Create(settings.Handle, 0x100, (IntPtr)Keys.Right, IntPtr.Zero), Keys.Control | Keys.Right);
            check(consumed && values["nextImageKey"] == "Right" && Field<string>(settings, "historyCapturing") == null,
                "shortcut recording also accepts Ctrl+Right through command-key preprocessing");
            buttons["previousImageKey"].PerformClick();
            Call(settings, "ProcessCmdKey", Message.Create(settings.Handle, 0x100, (IntPtr)Keys.Right, IntPtr.Zero), Keys.Right);
            check(values["previousImageKey"] == "Left" && Field<string>(settings, "historyCapturing") == "previousImageKey"
                && Field<Label>(settings, "hintLabel").Text.Contains("重复"),
                "navigation-key recording rejects conflicts without ending capture or changing the old value");
            Call(settings, "ProcessCmdKey", Message.Create(settings.Handle, 0x100, (IntPtr)Keys.Escape, IntPtr.Zero), Keys.Escape);
            check(Field<string>(settings, "historyCapturing") == null && values["previousImageKey"] == "Left",
                "Escape cancels navigation-key capture and preserves its previous shortcut");
            using (var bitmap = new Bitmap(settings.Width, settings.Height))
            { settings.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "settings-history-shortcuts.png")); }
            settings.Close();
        }
    }

    static void ShortcutMigration(Action<bool, string> check)
    {
        var configType = App.GetType("KimNotes.utils.InitConfig");
        var field = configType.GetField("configMap", Flags);
        var original = field.GetValue(null);
        var legacy = new System.Collections.Generic.Dictionary<string, string>((System.Collections.Generic.Dictionary<string, string>)original);
        try
        {
            legacy["pasteShortcutVersion"] = "2"; legacy["previousImageKey"] = "F3"; legacy["nextImageKey"] = "F4";
            field.SetValue(null, legacy);
            check((string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "previousImageKey") == "Left" &&
                (string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "nextImageKey") == "Right", "previous release navigation defaults migrate to the requested Ctrl+Left/Right");
            legacy["previousImageKey"] = "G";
            check((string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "previousImageKey") == "G", "shortcut migration preserves custom key choices");
            legacy["pasteShortcutVersion"] = "3"; legacy["previousImageKey"] = "F3";
            check((string)Static("KimNotes.utils.InitConfig", "GetConfigValue", "previousImageKey") == "F3", "explicit function-key choices remain available after migration");
        }
        finally { field.SetValue(null, original); }
    }

    static void HistoryTypography(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "history-fonts"); Directory.CreateDirectory(folder);
        using (var editor = new RichTextBox())
        {
            var handle = editor.Handle;
            editor.Text = "便签列表内容\n小字号预览，保持紧凑";
            editor.SaveFile(Path.Combine(folder, "font.rtf"));
        }
        using (var history = new history(folder))
        {
            history.Show();
            var until=DateTime.UtcNow.AddSeconds(30);
            do {Application.DoEvents();System.Threading.Thread.Sleep(2);if(DateTime.UtcNow>until)throw new Exception("history background loading timed out");}
            while(Field<bool>(history,"previewLoading") || Field<Timer>(history,"refreshTimer").Enabled);
            bool fonts = true, fit = true;
            for (int round = 0; round < 4; round++)
            foreach (var screen in Screen.AllScreens)
            {
                history.Location = new Point(screen.WorkingArea.Left + 24, screen.WorkingArea.Top + 24);
                Application.DoEvents();
                var cards = Field<Panel>(history, "panel1").Controls.OfType<Panel>().ToArray();
                foreach (var card in cards)
                {
                    var preview = card.Controls.OfType<RichTextBox>().Single();
                    var date = card.Controls.OfType<Label>().Single();
                    fonts &= preview.Text.Contains("便签列表内容") && Math.Abs(preview.Font.SizeInPoints - 9f) < 0.05 && Math.Abs(date.Font.SizeInPoints - 8f) < 0.05;
                    int last = preview.GetFirstCharIndexFromLine(preview.Lines.Length - 1);
                    fit &= preview.GetPositionFromCharIndex(last).Y + preview.Font.Height <= preview.ClientSize.Height + 2;
                }
                if (round == 0) using (var bitmap = new Bitmap(history.Width, history.Height))
                { history.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "history-font-" + history.DeviceDpi + ".png")); }
            }
            check(fonts, "history preview stays at 9pt with 8pt dates across repeated real monitor moves instead of applying DPI twice");
            check(fit, "compact history preview height still fits all displayed lines after DPI transitions");
            history.Close();
        }
    }
}
