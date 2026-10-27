using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace KimNotes.utils
{
    internal static class CloudDocuments
    {
        private const int MaxRtf = 32 * 1024 * 1024;
        private const int MaxRestoredRtf = 128 * 1024 * 1024;
        private const int MaxImage = 10 * 1024 * 1024;
        private static readonly Regex Marker = new Regex(@"\{\\\*\\kimimage ([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\}", RegexOptions.Compiled);
        private static readonly Regex Properties = new Regex(@"^(?:\\(?:picwgoal|pichgoal|picscalex|picscaley|piccropl|piccropr|piccropt|piccropb)-?\d{1,7})*$", RegexOptions.Compiled);
        private static readonly Regex Property = new Regex(@"\\(picwgoal|pichgoal|picscalex|picscaley|piccropl|piccropr|piccropt|piccropb)(-?\d+)", RegexOptions.Compiled);
        internal static string Pack(string rtf)
        {
            Validate(rtf);
            using (var stream = new MemoryStream()) {
                using (var gzip = new GZipStream(stream, CompressionMode.Compress, true)) {
                    var bytes = Encoding.UTF8.GetBytes(rtf); gzip.Write(bytes, 0, bytes.Length);
                }
                var packed = Convert.ToBase64String(stream.ToArray());
                if (packed.Length > 2 * 1024 * 1024) throw new InvalidDataException("便签富文本超过云端限制；本机内容仍保留。");
                return packed;
            }
        }
        internal static string Unpack(string packed)
        {
            if (packed == null || packed.Length > 2 * 1024 * 1024) throw new InvalidDataException("云端富文本过大。");
            using (var stream = new MemoryStream(Convert.FromBase64String(packed)))
            using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
            using (var output = new MemoryStream()) {
                var bytes = new byte[16384]; int read;
                while ((read = gzip.Read(bytes, 0, bytes.Length)) > 0) {
                    if (output.Length + read > MaxRtf) throw new InvalidDataException("云端富文本过大。");
                    output.Write(bytes, 0, read);
                }
                var rtf = new UTF8Encoding(false, true).GetString(output.ToArray()); Validate(rtf); return rtf;
            }
        }
        private static void Validate(string rtf, int limit = MaxRtf)
        {
            if (rtf == null || !rtf.StartsWith(@"{\rtf", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(rtf) > limit ||
                Regex.IsMatch(rtf, @"\\(?:object|objdata|objclass|filetbl|bin)(?:[^a-zA-Z]|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("此便签包含不支持同步的嵌入对象，或超过安全还原限制；本机内容仍保留。");
        }
        private static void ValidateTemplate(string rtf)
        {
            int depth = 0;
            for (int i = 0; i < rtf.Length; i++) {
                if (rtf[i] == '\\') { i++; continue; }
                if (rtf[i] == '{' && ++depth > 256 || rtf[i] == '}' && --depth < 0) throw new InvalidDataException("云端图片模板的分组无效。");
            }
            if (depth != 0) throw new InvalidDataException("云端图片模板的分组无效。");
        }
        private sealed class Picture
        {
            internal int Start, Length;
            internal string Rtf;
        }
        private static List<Picture> PictureGroups(string rtf)
        {
            var result = new List<Picture>();
            int at = 0;
            while ((at = rtf.IndexOf(@"\pict", at, StringComparison.Ordinal)) >= 0) {
                int next = at + 5;
                if (next < rtf.Length && char.IsLetter(rtf[next])) { at = next; continue; }
                int start = rtf.LastIndexOf('{', at), depth = 0, end = -1;
                if (start < 0) throw new InvalidDataException("便签图片格式无效。");
                for (int i = start; i < rtf.Length; i++) {
                    if (rtf[i] == '\\') { i++; continue; }
                    if (rtf[i] == '{') depth++;
                    if (rtf[i] == '}') depth--;
                    if (rtf[i] == '}' && depth == 0) { end = i; break; }
                }
                if (end < 0) throw new InvalidDataException("便签图片格式无效。");
                result.Add(new Picture { Start = start, Length = end - start + 1, Rtf = rtf.Substring(start, end - start + 1) });
                at = end + 1;
                if (result.Count > 32) throw new InvalidDataException("一张便签最多同步 32 张图片；本机原图仍保留。");
            }
            return result;
        }
        internal static IEnumerable<string> Pictures(string rtf) => PictureGroups(rtf).Select(p => p.Rtf);
        // A dedicated STA owns every temporary RichEdit control. No bitmap encoding, RTF
        // parsing or multi-megabyte hex conversion runs on the desktop message loop.
        private static Task<T> OnSta<T>(Func<T> action, CancellationToken cancel)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                try { cancel.ThrowIfCancellationRequested(); var value = action(); cancel.ThrowIfCancellationRequested(); result.TrySetResult(value); }
                catch (OperationCanceledException) { result.TrySetCanceled(); }
                catch (Exception error) { result.TrySetException(error); }
            }) { IsBackground = true, Name = "KimNotes cloud pictures" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
        }
        private sealed class Source
        {
            internal string Rtf, Text;
            internal int Theme;
            internal List<Picture> Pictures;
        }
        internal static Task<byte[]> NativeBytes(string rtf,CancellationToken cancel) => OnSta(()=> {
            using(var editor=new RichTextBox())
            using(var stream=new MemoryStream()) {
                var handle=editor.Handle;editor.Rtf=rtf;
                editor.SaveFile(stream,RichTextBoxStreamType.RichText);
                return stream.ToArray();
            }
        },cancel);
        private static string PictureProperties(string picture)
        {
            var values = new Dictionary<string, string>();
            foreach (Match match in Property.Matches(picture)) {
                if (values.ContainsKey(match.Groups[1].Value)) throw new InvalidDataException("便签图片的尺寸设置重复。");
                values.Add(match.Groups[1].Value, match.Groups[2].Value);
            }
            string properties = string.Concat(values.Select(v => "\\" + v.Key + v.Value));
            ValidateProperties(properties); return properties;
        }
        private static void ValidateProperties(string properties)
        {
            if (properties == null || properties.Length > 256 || !Properties.IsMatch(properties)) throw new InvalidDataException("云端图片的显示参数无效。");
            var seen = new HashSet<string>();
            foreach (Match match in Property.Matches(properties)) {
                int n;
                if (!seen.Add(match.Groups[1].Value) || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ||
                    n < -1000000 || n > 1000000 || (match.Groups[1].Value.EndsWith("goal", StringComparison.Ordinal) || match.Groups[1].Value.StartsWith("picscale", StringComparison.Ordinal)) && n <= 0)
                    throw new InvalidDataException("云端图片的显示参数无效。");
            }
        }
        private static byte[] PngBytes(string picture)
        {
            // Preserve an existing PNG stream byte-for-byte (including alpha and metadata).
            // WMF pictures use their embedded DIB pixels, never a screenshot-sized canvas.
            if (picture.Contains(@"\pngblip")) {
                string content = Regex.Replace(picture, @"\{\\\*\\blipuid[^}]*\}", "");
                content = Regex.Replace(content, @"\\[a-zA-Z]+-?\d* ?", "");
                var hex = new StringBuilder();
                foreach (char c in content) if (Uri.IsHexDigit(c)) hex.Append(c);
                if (hex.Length < 16 || hex.Length % 2 != 0 || hex.Length / 2 > MaxImage) throw new InvalidDataException("单张原图超过 10 MB，暂未同步；本机原图仍保留。");
                var data = new byte[hex.Length / 2];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)((Hex(hex[i * 2]) << 4) | Hex(hex[i * 2 + 1]));
                using (var stream = new MemoryStream(data)) using (var image = Image.FromStream(stream)) CheckImage(image);
                return data;
            }
            using (var image = NoteImages.Extract(picture)) using (var stream = new MemoryStream()) {
                if (image == null) throw new InvalidDataException("便签图片无法读取；本机原图仍保留。");
                CheckImage(image); image.Save(stream, ImageFormat.Png);
                if (stream.Length > MaxImage) throw new InvalidDataException("单张原图超过 10 MB，暂未同步；本机原图仍保留。");
                return stream.ToArray();
            }
        }
        private static int Hex(char c) => c <= '9' ? c - '0' : (c | 32) - 'a' + 10;
        private static void CheckImage(Image image)
        {
            if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > 40000000) throw new InvalidDataException("便签原图超过 4000 万像素；本机原图仍保留。");
        }
        private static string LosslessPicture(Image image, string properties)
        {
            // GDI+ EMF -> WMF conversion quantizes raster colors on each round-trip.
            // A standard META_STRETCHDIB record embeds the original BMP/DIB pixels
            // directly, so loading/syncing a note never recompresses or resamples it.
            if (image.Width > short.MaxValue || image.Height > short.MaxValue) throw new InvalidDataException("图片单边超过 32767 像素，暂无法还原为桌面富文本；原图仍保留。");
            using (var bmp = new MemoryStream()) using (var wmf = new MemoryStream()) {
                image.Save(bmp, ImageFormat.Bmp);
                byte[] data = bmp.ToArray();
                if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') throw new InvalidDataException("图片无损转换失败。");
                int dibLength = data.Length - 14, stretchWords = 14 + (dibLength + 1) / 2;
                using (var writer = new BinaryWriter(wmf, Encoding.UTF8, true)) {
                    writer.Write((ushort)1); writer.Write((ushort)9); writer.Write((ushort)0x300);
                    writer.Write(9 + 4 + 5 + 5 + stretchWords + 3); writer.Write((ushort)0);
                    writer.Write(stretchWords); writer.Write((ushort)0);
                    writer.Write(4); writer.Write((ushort)0x0103); writer.Write((ushort)8);
                    writer.Write(5); writer.Write((ushort)0x020b); writer.Write((short)0); writer.Write((short)0);
                    writer.Write(5); writer.Write((ushort)0x020c); writer.Write((short)image.Height); writer.Write((short)image.Width);
                    writer.Write(stretchWords); writer.Write((ushort)0x0f43); writer.Write(0x00cc0020); writer.Write((ushort)0);
                    writer.Write((short)image.Height); writer.Write((short)image.Width); writer.Write((short)0); writer.Write((short)0);
                    writer.Write((short)image.Height); writer.Write((short)image.Width); writer.Write((short)0); writer.Write((short)0);
                    writer.Write(data, 14, dibLength); if (dibLength % 2 != 0) writer.Write((byte)0);
                    writer.Write(3); writer.Write((ushort)0);
                }
                byte[] bytes = wmf.ToArray(); const string digits = "0123456789abcdef";
                var hex = new char[bytes.Length * 2];
                for (int i = 0; i < bytes.Length; i++) { hex[i * 2] = digits[bytes[i] >> 4]; hex[i * 2 + 1] = digits[bytes[i] & 15]; }
                return @"{\pict\wmetafile8\picw" + (int)Math.Round(image.Width * 2540d / 96d) + @"\pich" + (int)Math.Round(image.Height * 2540d / 96d) + properties + " " + new string(hex) + "}";
            }
        }
        internal static async Task<JObject> Export(string path, CloudApi api, CloudSession session, CloudState state, Action persist, CancellationToken cancel)
        {
            var source = await OnSta(() => {
                using (var editor = new RichTextBox()) {
                    var handle = editor.Handle; editor.LoadFile(path);
                    string rtf = editor.Rtf; Validate(rtf, MaxRestoredRtf);
                    if (rtf.Contains(@"\kimimage")) throw new InvalidDataException("本机便签存在未还原的云端图片标记。");
                    return new Source { Rtf = rtf, Text = editor.Text.Replace("\r\n", "\n"), Pictures = PictureGroups(rtf), Theme = Storage.Metadata(path).ThemeId };
                }
            }, cancel);
            if (Encoding.UTF8.GetByteCount(source.Text) > 256 * 1024) throw new InvalidDataException("便签文字超过云端 256 KB 限制。");
            bool external = source.Pictures.Count > 0 && await api.SupportsInlineImages(cancel);
            string legacy = null;
            if (!external) {
                try { legacy = await Task.Run(() => Pack(source.Rtf), cancel); }
                catch (InvalidDataException) when (source.Pictures.Count > 0) { throw new InvalidDataException("云端服务尚未支持独立图片同步，此便签暂缓同步；本机原图仍保留，请更新云端服务后重试。"); }
            }
            var attachments = new List<string>(); var references = new JArray();
            var template = new StringBuilder(); int previous = 0;
            foreach (var picture in source.Pictures) {
                cancel.ThrowIfCancellationRequested();
                var data = await Task.Run(() => PngBytes(picture.Rtf), cancel);
                string hash = await Task.Run(() => CloudStore.Hash(data), cancel);
                string id;
                if (!state.Images.TryGetValue(hash, out id)) {
                    id = await api.Upload(data, session.Token, cancel);
                    state.Images[hash] = id; persist();
                }
                if (!attachments.Contains(id)) attachments.Add(id);
                if (external) {
                    string marker = Guid.NewGuid().ToString();
                    template.Append(source.Rtf, previous, picture.Start - previous).Append(@"{\*\kimimage ").Append(marker).Append('}');
                    previous = picture.Start + picture.Length;
                    references.Add(new JObject { ["marker"] = marker, ["attachmentId"] = id, ["properties"] = PictureProperties(picture.Rtf) });
                }
            }
            string packed = legacy;
            if (external) {
                template.Append(source.Rtf, previous, source.Rtf.Length - previous);
                packed = await Task.Run(() => Pack(template.ToString()), cancel);
            }
            string title = source.Text.Split('\n').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? "未命名便签";
            if (title.Length > 240) title = title.Substring(0, 240);
            var doc = new JObject { ["title"] = title, ["text"] = source.Text, ["themeId"] = source.Theme,
                ["rtfGzip"] = packed, ["attachments"] = new JArray(attachments) };
            if (external) doc["rtfImages"] = references;
            return doc;
        }
        internal static async Task<string> Import(JObject doc, CloudApi api, string token, CancellationToken cancel)
        {
            string rich = null;
            if (!string.IsNullOrEmpty(doc.Value<string>("rtfGzip"))) rich = await Task.Run(() => Unpack(doc.Value<string>("rtfGzip")), cancel);
            else if (!string.IsNullOrEmpty(doc.Value<string>("rtf"))) { rich = doc.Value<string>("rtf"); Validate(rich); }
            var refs = doc["rtfImages"] as JArray;
            if (doc["rtfImages"] != null && refs == null) throw new InvalidDataException("云端图片引用格式无效。");
            if (refs != null && refs.Count > 0) {
                if (rich == null || refs.Count > 32) throw new InvalidDataException("云端图片模板无效。");
                ValidateTemplate(rich);
                var matches = new List<Match>();
                for (Match match = Marker.Match(rich); match.Success; match = match.NextMatch()) {
                    if (matches.Count == 32) throw new InvalidDataException("云端图片标记过多。");
                    matches.Add(match);
                }
                var seen = new HashSet<string>(); var images = new Dictionary<string, byte[]>();
                var attachments = new HashSet<string>(doc["attachments"]?.Values<string>() ?? Enumerable.Empty<string>());
                var properties = new Dictionary<string, string>();
                foreach (var item in refs) {
                    var obj = item as JObject; Guid value;
                    string marker = obj?.Value<string>("marker"), id = obj?.Value<string>("attachmentId"), props = obj?.Value<string>("properties");
                    if (!Guid.TryParseExact(marker, "D", out value) || marker != value.ToString() || !Guid.TryParseExact(id, "D", out value) || !seen.Add(marker) || !attachments.Contains(id) || matches.Count(m => m.Groups[1].Value == marker) != 1)
                        throw new InvalidDataException("云端图片引用无效或不完整。");
                    ValidateProperties(props); properties[marker] = props;
                }
                if (matches.Count != refs.Count || Marker.Replace(rich, "").Contains(@"\kimimage") || Pictures(rich).Any()) throw new InvalidDataException("云端图片模板含重复、缺失或混合的图片。");
                long downloadBudget = 0, pixelBudget = Encoding.UTF8.GetByteCount(rich);
                var pixels = new Dictionary<string, long>();
                foreach (var item in refs) {
                    cancel.ThrowIfCancellationRequested(); string id = item.Value<string>("attachmentId");
                    if (!images.ContainsKey(id)) {
                        var bytes = await api.Download(id, token, cancel);
                        downloadBudget += bytes.Length;
                        if (bytes.Length > MaxImage || downloadBudget > MaxRestoredRtf) throw new InvalidDataException("云端原图超过安全还原限制。");
                        pixels[id] = await Task.Run(() => {
                            using (var stream = new MemoryStream(bytes)) using (var image = Image.FromStream(stream)) { CheckImage(image); return (long)image.Width * image.Height; }
                        }, cancel);
                        images[id] = bytes;
                    }
                    pixelBudget += pixels[id] * 8 + 65536;
                    if (pixelBudget > MaxRestoredRtf) throw new InvalidDataException("还原后的便签富文本超过 128 MB；云端和本机原图仍保留。");
                }
                string template = rich;
                return await OnSta(() => {
                    var groups = new Dictionary<string, string>(); long budget = Encoding.UTF8.GetByteCount(template);
                    foreach (var item in refs) {
                        cancel.ThrowIfCancellationRequested(); string marker = item.Value<string>("marker"), id = item.Value<string>("attachmentId");
                        using (var stream = new MemoryStream(images[id])) using (var image = Image.FromStream(stream)) {
                            CheckImage(image);
                            // RichEdit needs a lossless WMF/DIB representation. Check the decoded
                            // allocation before rendering highly compressed, huge PNGs.
                            if ((long)image.Width * image.Height * 8 + budget > MaxRestoredRtf) throw new InvalidDataException("还原后的便签富文本超过 128 MB；云端和本机原图仍保留。");
                            string group = LosslessPicture(image, properties[marker]);
                            budget += group.Length;
                            if (budget > MaxRestoredRtf) throw new InvalidDataException("还原后的便签富文本超过 128 MB。");
                            groups[marker] = group;
                        }
                    }
                    string restored = Marker.Replace(template, m => groups[m.Groups[1].Value]); Validate(restored, MaxRestoredRtf); return restored;
                }, cancel);
            }
            if (rich != null) {
                if (rich.Contains(@"\kimimage")) throw new InvalidDataException("云端图片引用缺失；暂缓接收此便签。");
                return rich;
            }
            // Text-only web notes and old attachment-only notes remain readable.
            var legacyImages = new List<byte[]>(); long legacyBytes = 0;
            foreach (var id in doc["attachments"]?.Values<string>() ?? Enumerable.Empty<string>()) {
                if (legacyImages.Count >= 32) throw new InvalidDataException("云端图片过多。");
                var bytes = await api.Download(id, token, cancel);
                legacyBytes += bytes.Length;
                if (bytes.Length > MaxImage || legacyBytes > MaxRestoredRtf) throw new InvalidDataException("云端原图超过安全还原限制。");
                legacyImages.Add(bytes);
            }
            return await OnSta(() => {
                using (var editor = new RichTextBox()) {
                    var handle = editor.Handle;
                    editor.Text = (doc.Value<string>("title") ?? "未命名便签") + "\n\n" + (doc.Value<string>("text") ?? "");
                    long budget = Encoding.UTF8.GetByteCount(editor.Rtf);
                    foreach (var bytes in legacyImages) using (var stream = new MemoryStream(bytes)) using (var image = Image.FromStream(stream)) {
                        cancel.ThrowIfCancellationRequested(); CheckImage(image);
                        budget += (long)image.Width * image.Height * 8 + 65536;
                        if (budget > MaxRestoredRtf) throw new InvalidDataException("还原后的便签富文本超过 128 MB。");
                        editor.Select(editor.TextLength, 0); editor.SelectedText = "\n";
                        double scale = Math.Min(1d, Math.Max(96, editor.ClientSize.Width - 24) / (double)image.Width);
                        string props = @"\picwgoal" + Math.Max(1, (int)Math.Round(image.Width * scale * 1440d / 96d)) + @"\pichgoal" + Math.Max(1, (int)Math.Round(image.Height * scale * 1440d / 96d));
                        editor.SelectedRtf = @"{\rtf1\ansi" + LosslessPicture(image, props) + "}";
                    }
                    string rtf = editor.Rtf; Validate(rtf, MaxRestoredRtf); return rtf;
                }
            }, cancel);
        }
    }
}


