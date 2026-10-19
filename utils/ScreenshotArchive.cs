using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace KimNotes.utils
{
    // Separate from exported screenshots: closing a floating window is reversible.
    internal static class ScreenshotArchive
    {
        private static readonly Dictionary<string, Bitmap> temporary = new Dictionary<string, Bitmap>();
        private static string Folder => Path.Combine(Storage.Root, "screenshot-history");
        internal static string[] List(bool incognito)
        {
            if (incognito) return temporary.Keys.OrderByDescending(k => k, StringComparer.Ordinal).ToArray();
            return Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.png").Select(Path.GetFileName).OrderByDescending(k => k, StringComparer.Ordinal).ToArray() : new string[0];
        }

        internal static string Save(Image image, bool incognito, string id = null)
        {
            if (image == null) return id;
            if (id == null) id = DateTime.UtcNow.Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N") + ".png";
            if (Path.GetFileName(id) != id) throw new InvalidDataException("无效的贴图记录。");
            if (incognito)
            {
                var copy = new Bitmap(image);
                if (temporary.TryGetValue(id, out var old)) old.Dispose();
                temporary[id] = copy;
                long bytes = 0; int count = 0;
                foreach (string key in List(true))
                {
                    count++;
                    var bitmap = temporary[key];
                    bytes += (long)bitmap.Width * bitmap.Height * 4;
                    if (key != id && (bytes > 128L * 1024 * 1024 || count > 20))
                    { temporary.Remove(key); bitmap.Dispose(); }
                }
            }
            else
            {
                Storage.Write(Path.Combine(Folder, id), temp => image.Save(temp, ImageFormat.Png));
                // Cache is bounded. The user's exported files are never removed.
                long bytes = 0; int count = 0;
                foreach (string key in List(false))
                {
                    string path = Path.Combine(Folder, key);
                    bytes += new FileInfo(path).Length;
                    if (++count > 50 || (count > 1 && bytes > 256L * 1024 * 1024))
                    { File.Delete(path); if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); }
                }
            }
            return id;
        }

        internal static void Delete(string id, bool incognito)
        {
            if (string.IsNullOrEmpty(id) || Path.GetFileName(id) != id)
                throw new InvalidDataException("无效的贴图记录。");
            if (incognito)
            {
                if (temporary.TryGetValue(id, out var image))
                {
                    temporary.Remove(id);
                    image.Dispose();
                }
                return;
            }
            // Only remove our history cache, never the user's exported image.
            string path = Path.Combine(Folder, id);
            File.Delete(path + ".bak");
            File.Delete(path);
        }

        internal static Bitmap Load(string id, bool incognito)
        {
            if (Path.GetFileName(id) != id) throw new InvalidDataException("无效的贴图记录。");
            if (incognito) return temporary.TryGetValue(id, out var image) ? new Bitmap(image) : null;
            foreach (var suffix in new[] { "", ".bak" })
            {
                try { using (var image = Image.FromFile(Path.Combine(Folder, id) + suffix)) return new Bitmap(image); }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is OutOfMemoryException) { }
            }
            return null;
        }
    }
}
