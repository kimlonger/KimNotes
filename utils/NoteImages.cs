using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace KimNotes.utils
{
    internal static class NoteImages
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativeObject
        {
            internal int Size, Index;
            internal Guid ClassId;
            internal IntPtr Object, Storage, Site;
            internal int Width, Height;
            internal uint Aspect, Flags, User;
        }
        [DllImport("user32.dll",EntryPoint="SendMessageW")]
        private static extern IntPtr OleInterface(IntPtr window,int message,IntPtr unused,out IntPtr result);
        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window,IntPtr rectangle,IntPtr region,uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CountObjects(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadObject(IntPtr self,int index,ref NativeObject value,uint flags);

        internal struct PictureBoxInfo { internal int Index; internal Rectangle Bounds; }

        internal static List<PictureBoxInfo> PictureBoxes(RichTextBox editor)
        {
            var result = new List<PictureBoxInfo>();
            if (editor.IsDisposed || !editor.IsHandleCreated) return result;
            IntPtr ole;
            if (OleInterface(editor.Handle,0x043c,IntPtr.Zero,out ole)==IntPtr.Zero || ole==IntPtr.Zero) return result;
            try {
                var table=Marshal.ReadIntPtr(ole);
                var count=(CountObjects)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table,4*IntPtr.Size),typeof(CountObjects));
                var read=(ReadObject)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table,6*IntPtr.Size),typeof(ReadObject));
                int total=count(ole);
                for(int i=0;i<total;i++) {
                    var value=new NativeObject {Size=Marshal.SizeOf(typeof(NativeObject))};
                    // REO_GETOBJ_NO_INTERFACES: no object/site/storage references to release.
                    if(read(ole,i,ref value,0)!=0 || value.Width<=0 || value.Height<=0)continue;
                    var point=editor.GetPositionFromCharIndex(value.Index);
                    float scale=editor.DeviceDpi/2540f*editor.ZoomFactor;
                    var size=new Size(Math.Max(1,(int)Math.Round(value.Width*scale)),Math.Max(1,(int)Math.Round(value.Height*scale)));
                    result.Add(new PictureBoxInfo {Index=value.Index,Bounds=new Rectangle(point,size)});
                }
            } finally {Marshal.Release(ole);}
            return result;
        }

        internal static int PictureAt(RichTextBox editor,Point point)
        {
            foreach(var picture in PictureBoxes(editor))
                if(picture.Bounds.Contains(point) && SelectedPicture(editor,picture.Index)!=null) return picture.Index;
            return -1;
        }

        internal static bool CopySelection(RichTextBox editor)
        {
            if(editor.SelectionLength!=1 || SelectedPicture(editor,editor.SelectionStart)==null) return false;
            CopyPicture(editor,editor.SelectionStart);
            return true;
        }

        private static void CopyPicture(RichTextBox editor,int index)
        {
            try {
                using(var image=Extract(SelectedPicture(editor,index))) {
                    if(image==null)return;
                    // Persist a real bitmap, so browsers and other applications can paste it
                    // after the temporary extraction bitmap has been disposed.
                    var data=new DataObject(); data.SetImage(image);
                    Clipboard.SetDataObject(data,true,5,60);
                }
            } catch(Exception error) {
                Program.LogError(error);
                MessageBox.Show(editor.FindForm(),"图片暂时无法复制，请稍后重试。原图仍保存在便签中。","复制图片",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
        }

        internal static string SelectedPicture(RichTextBox editor, int index)
        {
            if (index < 0 || index >= editor.TextLength) return null;
            int start = editor.SelectionStart, length = editor.SelectionLength;
            try
            {
                editor.Select(index, 1);
                var rtf = editor.SelectedRtf;
                return rtf.Contains("\\pict") ? rtf : null;
            }
            finally { editor.Select(start, length); }
        }

        internal static Bitmap Extract(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return null;
            int marker = rtf.IndexOf("\\pict", StringComparison.Ordinal);
            if (marker < 0) return null;
            int start = rtf.LastIndexOf('{', marker), depth = 0, end = -1;
            for (int i = start; i >= 0 && i < rtf.Length; i++)
            {
                if (rtf[i] == '\\') { i++; continue; }
                if (rtf[i] == '{') depth++;
                if (rtf[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (start < 0 || end < 0) return null;
            string group = rtf.Substring(start, end - start + 1);
            group = Regex.Replace(group, @"\{\\\*\\blipuid[^}]*\}", "");
            group = Regex.Replace(group, @"\\[a-zA-Z]+-?\d* ?", "");
            int count=0;
            foreach(char c in group)if(HexValue(c)>=0)count++;
            if(count<16 || count%2!=0 || count>128*1024*1024)return null;
            var bytes=new byte[count/2];
            int high=-1,index=0;
            foreach(char c in group) {
                int value=HexValue(c);
                if(value<0)continue;
                if(high<0)high=value;
                else {bytes[index++]=(byte)((high<<4)|value);high=-1;}
            }
            // A WMF's reported canvas can be the recording monitor's full size.
            // Recover the embedded DIB pixels rather than rasterizing that canvas.
            if (rtf.Contains("\\wmetafile"))
            {
                var dib = ExtractDib(bytes);
                if (dib != null) return dib;
            }
            using (var stream = new MemoryStream(bytes))
            using (var image = Image.FromStream(stream))
            {
                if ((long)image.Width * image.Height > 64000000) throw new InvalidDataException("图片尺寸过大。");
                return new Bitmap(image);
            }
        }

        private static int HexValue(char c) => c>='0' && c<='9' ? c-'0' : c>='a' && c<='f' ? c-'a'+10 : c>='A' && c<='F' ? c-'A'+10 : -1;

        private static Bitmap ExtractDib(byte[] data)
        {
            for (int offset = 0; offset + 40 <= data.Length; offset++)
            {
                if (data[offset] != 40 || BitConverter.ToInt32(data, offset) != 40 || BitConverter.ToInt16(data, offset + 12) != 1) continue;
                int width = BitConverter.ToInt32(data, offset + 4), height = BitConverter.ToInt32(data, offset + 8);
                int bits = BitConverter.ToUInt16(data, offset + 14), compression = BitConverter.ToInt32(data, offset + 16);
                if (width <= 0 || height == 0 || height == int.MinValue || (long)width * Math.Abs(height) > 64000000 ||
                    (bits != 1 && bits != 4 && bits != 8 && bits != 16 && bits != 24 && bits != 32) || (compression != 0 && compression != 3)) continue;
                uint used = BitConverter.ToUInt32(data, offset + 32);
                if (used > 256) continue;
                int colors = bits <= 8 ? (used == 0 ? 1 << bits : (int)used) : (int)used;
                int header = 40 + colors * 4 + (compression == 3 ? 12 : 0);
                long length = header + ((long)width * bits + 31) / 32 * 4 * Math.Abs(height);
                if (length > data.Length - offset || length > 256L * 1024 * 1024) continue;
                using (var stream = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    {
                        writer.Write((ushort)0x4d42); writer.Write((int)length + 14);
                        writer.Write(0); writer.Write(header + 14);
                        writer.Write(data, offset, (int)length);
                    }
                    stream.Position = 0;
                    using (var image = Image.FromStream(stream)) return new Bitmap(image);
                }
            }
            return null;
        }

        internal static bool ResizeTo(RichTextBox editor,int index,Size pixels)
        {
            string rtf=SelectedPicture(editor,index);
            if(rtf==null || pixels.Width<1 || pixels.Height<1)return false;
            double units=1440d/(editor.DeviceDpi*editor.ZoomFactor);
            int width=Math.Max(120,Math.Min(100000,(int)Math.Round(pixels.Width*units)));
            int height=Math.Max(120,Math.Min(100000,(int)Math.Round(pixels.Height*units)));
            if(!Regex.IsMatch(rtf,@"\\picwgoal\d+") || !Regex.IsMatch(rtf,@"\\pichgoal\d+"))return false;
            rtf=Regex.Replace(rtf,@"\\picwgoal\d+","\\picwgoal"+width);
            rtf=Regex.Replace(rtf,@"\\pichgoal\d+","\\pichgoal"+height);
            editor.Select(index,1); editor.SelectedRtf=rtf; editor.Select(index,1);
            // Replacing an image's RTF does not always raise TextChanged.
            editor.Modified=true;
            return true;
        }

        internal static void Attach(RichTextBox editor, Form owner, Action changed)
        {
            var menu = new ContextMenuStrip();
            int imageIndex = -1;
            Point clicked = Point.Empty;
            bool mouseMenu=false;
            editor.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) {clicked = e.Location;mouseMenu=true;} };
            var interaction=new PictureInteraction(editor,owner,changed);
            Action<Action<Bitmap>> useImage = action =>
            {
                try { using (var image = Extract(SelectedPicture(editor, imageIndex))) if (image != null) action(image); }
                catch (Exception e) { Program.LogError(e); MessageBox.Show(owner, "图片操作未完成，请稍后重试；原图仍保存在便签中。", "图片操作", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            var preview = new ToolStripMenuItem("查看大图", null, (s, e) => useImage(image => ScreenshotHelper.PreviewImage(image)));
            var copy = new ToolStripMenuItem("复制图片", null, (s, e) => CopyPicture(editor,imageIndex));
            var delete = new ToolStripMenuItem("删除图片", null, (s, e) =>
            {
                if (SelectedPicture(editor, imageIndex) == null) return;
                editor.Select(imageIndex, 1); editor.SelectedText = ""; changed();
            });
            var textItems=new ToolStripItem[] {
                new ToolStripMenuItem("撤销", null, (s, e) => editor.Undo()),
                new ToolStripMenuItem("复制", null, (s, e) => {if(!CopySelection(editor))editor.Copy();}),
                new ToolStripMenuItem("粘贴", null, (s, e) => { if (owner is note n) n.PasteContent(); }),
                new ToolStripMenuItem("全选", null, (s, e) => editor.SelectAll()) };
            menu.Items.AddRange(new ToolStripItem[] { copy, preview, delete });
            menu.Items.AddRange(textItems);
            menu.Opening += (s, e) =>
            {
                imageIndex = mouseMenu ? PictureAt(editor,clicked) :
                    (editor.SelectionLength==1 && SelectedPicture(editor,editor.SelectionStart)!=null ? editor.SelectionStart : -1);
                bool hasImage = SelectedPicture(editor, imageIndex) != null;
                if(hasImage)editor.Select(imageIndex,1);
                preview.Visible = copy.Visible = delete.Visible = hasImage;
                foreach(var item in textItems)item.Visible=!hasImage;
            };
            menu.Closed += (s,e) => mouseMenu=false;
            editor.MouseDoubleClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                imageIndex = PictureAt(editor,e.Location);
                if (SelectedPicture(editor, imageIndex) != null) useImage(image => ScreenshotHelper.PreviewImage(image));
            };
            editor.ContextMenuStrip = menu;
            editor.Disposed += (s, e) => menu.Dispose();
        }

        private sealed class PictureInteraction
        {
            private readonly RichTextBox editor;
            private readonly Action changed;
            private readonly Panel[] edges=new Panel[4];
            private readonly Panel[] handles=new Panel[8];
            private readonly int[] dx={-1,0,1,1,1,0,-1,-1},dy={-1,-1,-1,0,1,1,1,0};
            private int index=-1,dragHandle=-1;
            private bool updating,dragging,frameQueued;
            private Point dragStart;
            private Rectangle original,preview;
            private static readonly Color Accent=Color.FromArgb(74,127,193);

            internal PictureInteraction(RichTextBox editor,Form owner,Action changed)
            {
                this.editor=editor;this.changed=changed;
                for(int i=0;i<edges.Length;i++) {
                    edges[i]=new Panel {BackColor=Accent,Visible=false,TabStop=false,Name="note-image-edge-"+i};
                    editor.Controls.Add(edges[i]);
                }
                for(int i=0;i<handles.Length;i++) {
                    int handle=i;
                    handles[i]=new Panel {BackColor=Color.White,Visible=false,TabStop=false,Name="note-image-handle-"+i,
                        Cursor=i%2==0 ? (i==0 || i==4 ? Cursors.SizeNWSE : Cursors.SizeNESW) : (i==1 || i==5 ? Cursors.SizeNS : Cursors.SizeWE),
                        AccessibleName=i%2==0 ? "拖拽角点，等比例缩放图片" : "拖拽边缘，调整图片宽度或高度"};
                    handles[i].Paint += (s,e) => {using(var pen=new Pen(Accent))e.Graphics.DrawRectangle(pen,0,0,((Control)s).Width-1,((Control)s).Height-1);};
                    handles[i].MouseDown += (s,e) => StartDrag(handle,e);
                    handles[i].MouseMove += (s,e) => MoveDrag(handle,e);
                    handles[i].MouseUp += (s,e) => EndDrag(handle,e);
                    handles[i].MouseCaptureChanged += (s,e) => {if(dragging && !handles[handle].Capture)CancelDrag();};
                    editor.Controls.Add(handles[i]);
                }
                editor.MouseDown += (s,e) => {
                    if(e.Button!=MouseButtons.Left || updating)return;
                    updating=true;
                    try {
                        int hit=PictureAt(editor,e.Location);
                        if(hit>=0) {editor.Select(hit,1);index=hit;} else index=-1;
                    } finally {updating=false;}
                    RefreshFrame();
                };
                editor.SelectionChanged += (s,e) => UpdateSelection();
                editor.TextChanged += (s,e) => UpdateSelection();
                editor.VScroll += (s,e) => {CancelDrag();RefreshFrame();};
                editor.HScroll += (s,e) => {CancelDrag();RefreshFrame();};
                editor.Resize += (s,e) => {CancelDrag();RefreshFrame();};
                editor.MouseWheel += (s,e) => QueueFrame();
                editor.Resize += (s,e) => QueueFrame();
                // The parent's DPI event can precede RichEdit's own DPI update.
                // Refresh after the child finishes scaling its image objects.
                editor.DpiChangedAfterParent += (s,e) => QueueFrame();
                DpiChangedEventHandler changedDpi=(s,e) => QueueFrame();
                owner.DpiChanged += changedDpi;
                editor.Disposed += (s,e) => owner.DpiChanged-=changedDpi;
                editor.KeyDown += (s,e) => {
                    if(e.KeyCode==Keys.Escape && dragging){CancelDrag();e.SuppressKeyPress=true;}
                    else if(e.KeyData==(Keys.Control|Keys.C) && CopySelection(editor))e.SuppressKeyPress=true;
                };
            }

            private void QueueFrame()
            {
                if(frameQueued || editor.IsDisposed || !editor.IsHandleCreated)return;
                frameQueued=true;
                editor.BeginInvoke((Action)(() => {frameQueued=false;if(!editor.IsDisposed){CancelDrag();RefreshFrame();}}));
            }

            private void UpdateSelection()
            {
                if(updating || dragging || editor.IsDisposed)return;
                updating=true;
                try {index=editor.SelectionLength==1 && SelectedPicture(editor,editor.SelectionStart)!=null ? editor.SelectionStart : -1;}
                finally {updating=false;}
                RefreshFrame();
            }

            private void RefreshFrame()
            {
                if(updating || dragging || editor.IsDisposed)return;
                Rectangle bounds=Rectangle.Empty;
                if(index>=0 && !editor.ReadOnly)foreach(var picture in PictureBoxes(editor))if(picture.Index==index){bounds=picture.Bounds;break;}
                DrawFrame(bounds);
            }

            private void DrawFrame(Rectangle bounds)
            {
                using(var dirty=FrameRegion()) {
                    bool visible=!bounds.IsEmpty && bounds.IntersectsWith(editor.ClientRectangle);
                    if(!visible) {foreach(var edge in edges)edge.Visible=false;foreach(var handle in handles)handle.Visible=false;}
                    else {
                        int stroke=Math.Max(1,(int)Math.Round(editor.DeviceDpi/96f));
                        edges[0].Bounds=new Rectangle(bounds.Left,bounds.Top,bounds.Width,stroke);
                        edges[1].Bounds=new Rectangle(bounds.Left,bounds.Bottom-stroke,bounds.Width,stroke);
                        edges[2].Bounds=new Rectangle(bounds.Left,bounds.Top,stroke,bounds.Height);
                        edges[3].Bounds=new Rectangle(bounds.Right-stroke,bounds.Top,stroke,bounds.Height);
                        foreach(var edge in edges){edge.Visible=true;edge.BringToFront();}
                        int size=Math.Max(7,(int)Math.Round(7*editor.DeviceDpi/96f));
                        for(int i=0;i<handles.Length;i++) {
                            int x=dx[i]<0 ? bounds.Left : dx[i]>0 ? bounds.Right : bounds.Left+bounds.Width/2;
                            int y=dy[i]<0 ? bounds.Top : dy[i]>0 ? bounds.Bottom : bounds.Top+bounds.Height/2;
                            handles[i].Bounds=new Rectangle(x-size/2,y-size/2,size,size);
                            handles[i].Visible=editor.ClientRectangle.IntersectsWith(handles[i].Bounds);
                            handles[i].BringToFront();
                        }
                    }
                    using(var current=FrameRegion())dirty.Union(current);
                    // Child HWND moves leave old pixels over RichEdit while a burst
                    // of mouse messages delays WM_PAINT. Redraw only old/new bands
                    // and their handles immediately, keeping the document intact.
                    if(!editor.IsDisposed && editor.IsHandleCreated) {
                        editor.Invalidate(dirty,true);
                        RedrawWindow(editor.Handle,IntPtr.Zero,IntPtr.Zero,0x0180); // UPDATENOW | ALLCHILDREN
                    }
                }
            }

            private Region FrameRegion()
            {
                var region=new Region();region.MakeEmpty();
                foreach(var edge in edges)if(edge.Visible)region.Union(Rectangle.Inflate(edge.Bounds,1,1));
                foreach(var handle in handles)if(handle.Visible)region.Union(Rectangle.Inflate(handle.Bounds,1,1));
                region.Intersect(editor.ClientRectangle);
                return region;
            }

            private void StartDrag(int handle,MouseEventArgs e)
            {
                if(e.Button!=MouseButtons.Left || index<0)return;
                original=Rectangle.Empty;
                foreach(var picture in PictureBoxes(editor))if(picture.Index==index){original=picture.Bounds;break;}
                if(original.IsEmpty)return;
                dragStart=editor.PointToClient(handles[handle].PointToScreen(e.Location));
                preview=original;dragging=true;dragHandle=handle;handles[handle].Capture=true;
            }

            private void MoveDrag(int handle,MouseEventArgs e)
            {
                if(!dragging || dragHandle!=handle)return;
                var point=editor.PointToClient(handles[handle].PointToScreen(e.Location));
                int x=point.X-dragStart.X,y=point.Y-dragStart.Y;
                int min=Math.Max(8,(int)Math.Round(12*editor.DeviceDpi/96f));
                int width=dx[handle]==0 ? original.Width : Math.Max(min,original.Width+x*dx[handle]);
                int height=dy[handle]==0 ? original.Height : Math.Max(min,original.Height+y*dy[handle]);
                if(dx[handle]!=0 && dy[handle]!=0) {
                    double sx=width/(double)original.Width,sy=height/(double)original.Height;
                    double scale=Math.Abs(sx-1)>=Math.Abs(sy-1) ? sx : sy;
                    scale=Math.Max(scale,Math.Max(min/(double)original.Width,min/(double)original.Height));
                    width=(int)Math.Round(original.Width*scale);height=(int)Math.Round(original.Height*scale);
                }
                int maximum=(int)Math.Round(4096*editor.DeviceDpi/96f);
                if(dx[handle]!=0 && dy[handle]!=0) {
                    double limit=Math.Min(1,Math.Min(maximum/(double)width,maximum/(double)height));
                    width=(int)Math.Round(width*limit);height=(int)Math.Round(height*limit);
                } else {width=Math.Min(maximum,width);height=Math.Min(maximum,height);}
                preview=new Rectangle(dx[handle]<0 ? original.Right-width : original.Left,dy[handle]<0 ? original.Bottom-height : original.Top,width,height);
                DrawFrame(preview);
            }

            private void EndDrag(int handle,MouseEventArgs e)
            {
                if(e.Button!=MouseButtons.Left || !dragging || dragHandle!=handle)return;
                dragging=false;dragHandle=-1;handles[handle].Capture=false;
                updating=true;
                try {
                    if(preview.Size!=original.Size && ResizeTo(editor,index,preview.Size))changed();
                } finally {updating=false;UpdateSelection();}
            }

            private void CancelDrag()
            {
                if(!dragging)return;
                dragging=false;
                if(dragHandle>=0)handles[dragHandle].Capture=false;
                dragHandle=-1;RefreshFrame();
            }
        }
    }
}
