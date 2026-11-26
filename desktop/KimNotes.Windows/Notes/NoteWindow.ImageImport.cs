using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow
    {
        private static readonly HashSet<string> ImageFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
        };

        private static bool HasImageFiles(IDataObject data)
        {
            try { return ImageFilePaths(data).Length != 0; }
            catch (ExternalException) { return false; }
        }

        private bool TryInsertImageFiles(IDataObject data, TextPointer dropPosition = null)
        {
            string[] paths;
            try { paths = ImageFilePaths(data); }
            catch (ExternalException error)
            {
                ReportClipboardUnavailable(error);
                return true;
            }
            if (paths.Length == 0) return false;

            var bitmaps = new List<BitmapSource>(paths.Length);
            foreach (var path in paths)
            {
                try { bitmaps.Add(OpenImageFile(path)); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                    error is FormatException || error is NotSupportedException || error is InvalidOperationException ||
                    error is ArgumentException || error is SecurityException || error is ExternalException)
                {
                    Program.LogError(error);
                }
            }

            // Decode the entire selection before editing so a failing file cannot clear the note's selection.
            if (bitmaps.Count != 0) PasteBitmaps(bitmaps, dropPosition);
            if (bitmaps.Count == 0) SaveStatus.Text = "图片无法打开";
            else if (bitmaps.Count != paths.Length) SaveStatus.Text = $"{paths.Length - bitmaps.Count} 张图片无法打开";
            return true;
        }

        private static string[] ImageFilePaths(IDataObject data)
        {
            var paths = data?.GetData(DataFormats.FileDrop, false) as string[];
            return paths == null ? Array.Empty<string>() : paths.Where(IsImageFilePath).ToArray();
        }

        private static bool IsImageFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!ImageFileExtensions.Contains(Path.GetExtension(path))) return false;
                var root = Path.GetPathRoot(path);
                return root != null && root.Length > 1 && root[root.Length - 1] != ':';
            }
            catch (ArgumentException) { return false; }
        }

        private static BitmapSource OpenImageFile(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) throw new FileFormatException("图片没有可导入的画面。");
                // WPF's editor snapshots an inline image on undo and cannot serialize its private decoder-frame type.
                var bitmap = new WriteableBitmap(decoder.Frames[0]);
                bitmap.Freeze();
                return bitmap;
            }
        }
    }
}
