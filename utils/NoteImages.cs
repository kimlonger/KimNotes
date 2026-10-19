using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace KimNotes.utils
{
    internal static class NoteImages
    {
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
            var hex = new StringBuilder();
            foreach (char c in group) if (Uri.IsHexDigit(c)) hex.Append(c);
            if (hex.Length < 16 || hex.Length % 2 != 0 || hex.Length > 64 * 1024 * 1024) return null;
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.ToString(i * 2, 2), 16);
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

        internal static bool Resize(RichTextBox editor, int index, double factor)
        {
            string rtf = SelectedPicture(editor, index);
            if (rtf == null || factor <= 0 || factor > 4) return false;
            rtf = Regex.Replace(rtf, @"\\(picwgoal|pichgoal)(\d+)", match => "\\" + match.Groups[1].Value +
                Math.Max(120, Math.Min(100000, (int)Math.Round(int.Parse(match.Groups[2].Value) * factor))));
            editor.Select(index, 1);
            editor.SelectedRtf = rtf;
            editor.Select(index + 1, 0);
            return true;
        }

        internal static void Attach(RichTextBox editor, Form owner, Action changed)
        {
            var menu = new ContextMenuStrip();
            int imageIndex = -1;
            Point clicked = Point.Empty;
            editor.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) clicked = e.Location; };
            Action<Action<Bitmap>> useImage = action =>
            {
                try { using (var image = Extract(SelectedPicture(editor, imageIndex))) if (image != null) action(image); }
                catch (Exception e) { Program.LogError(e); MessageBox.Show(owner, "图片操作未完成，请稍后重试；原图仍保存在便签中。", "图片操作", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            var preview = new ToolStripMenuItem("查看大图", null, (s, e) => useImage(image => ScreenshotHelper.PreviewImage(image)));
            var copy = new ToolStripMenuItem("复制图片", null, (s, e) => useImage(image => Clipboard.SetImage(image)));
            var save = new ToolStripMenuItem("图片另存为…", null, (s, e) => useImage(image =>
            {
                using (var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png|JPEG 图片|*.jpg", FileName = "便签图片.png" })
                    if (dialog.ShowDialog(owner) == DialogResult.OK) ScreenshotHelper.ExportImage(image, dialog.FileName);
            }));
            var resize = new ToolStripMenuItem("调整图片大小");
            foreach (double factor in new[] { 0.75, 1.25 })
            {
                double amount = factor;
                resize.DropDownItems.Add(amount < 1 ? "缩小" : "放大", null, (s, e) => { if (Resize(editor, imageIndex, amount)) changed(); });
            }
            resize.DropDownItems.Add("适应便签宽度", null, (s, e) =>
            {
                string rtf = SelectedPicture(editor, imageIndex);
                if (rtf == null) return;
                var width = Regex.Match(rtf, @"\\picwgoal(\d+)");
                if (width.Success)
                {
                    double goal = Math.Max(96, editor.ClientSize.Width - 24) * 1440d / editor.DeviceDpi;
                    double factor = Math.Min(4, goal / int.Parse(width.Groups[1].Value));
                    if (Resize(editor, imageIndex, factor)) changed();
                }
            });
            var delete = new ToolStripMenuItem("删除这张图片", null, (s, e) =>
            {
                if (SelectedPicture(editor, imageIndex) == null) return;
                editor.Select(imageIndex, 1); editor.SelectedText = ""; changed();
            });
            menu.Items.AddRange(new ToolStripItem[] { preview, copy, save, resize, delete, new ToolStripSeparator(),
                new ToolStripMenuItem("撤销", null, (s, e) => editor.Undo()),
                new ToolStripMenuItem("复制文字", null, (s, e) => editor.Copy()),
                new ToolStripMenuItem("粘贴", null, (s, e) => { if (owner is note n) n.PasteContent(); }),
                new ToolStripMenuItem("全选", null, (s, e) => editor.SelectAll()) });
            menu.Opening += (s, e) =>
            {
                imageIndex = editor.GetCharIndexFromPosition(clicked);
                bool hasImage = SelectedPicture(editor, imageIndex) != null;
                preview.Visible = copy.Visible = save.Visible = resize.Visible = delete.Visible = hasImage;
            };
            editor.MouseDoubleClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                imageIndex = editor.GetCharIndexFromPosition(e.Location);
                if (SelectedPicture(editor, imageIndex) != null) useImage(image => ScreenshotHelper.PreviewImage(image));
            };
            editor.ContextMenuStrip = menu;
            editor.Disposed += (s, e) => menu.Dispose();
        }
    }
}
