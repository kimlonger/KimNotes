using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace KimNotes.utils
{
    /// <summary>Disposable text previews; the original RTF remains the source of truth.</summary>
    internal class NoteIndex
    {
        public class Entry
        {
            public long Mtime { get; set; }
            public long Length { get; set; } = -1;
            public string Title { get; set; }
            public string Summary { get; set; }
            public string SearchText { get; set; }
        }

        private static readonly object cacheLock = new object();
        private static NoteIndex current;
        private readonly string indexPath;
        private readonly object gate = new object();
        private readonly object saveGate = new object();
        private readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private bool dirty;
        private long version;

        private NoteIndex(string path) { indexPath = path; }

        public static NoteIndex Load()
        {
            string path = Path.Combine(Storage.Root, "index.json");
            lock (cacheLock)
            {
                if (current != null && string.Equals(current.indexPath, path, StringComparison.OrdinalIgnoreCase)) return current;
                var index = new NoteIndex(path);
                try
                {
                    if (File.Exists(path))
                    {
                        var loaded = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path));
                        if (loaded != null) foreach (var kv in loaded) index.map[kv.Key] = kv.Value;
                    }
                }
                catch { /* A damaged cache can always be rebuilt from note files. */ }
                return current = index;
            }
        }

        public bool TryGet(string fileName, DateTime mtime, out string title, out string summary, out string searchText)
        {
            Entry entry;
            bool found = TryGet(fileName, mtime, -1, out entry);
            title = entry?.Title; summary = entry?.Summary; searchText = entry?.SearchText;
            return found;
        }

        internal bool TryGet(string fileName, DateTime mtime, long length, out Entry entry)
        {
            lock (gate)
            {
                if (map.TryGetValue(fileName, out entry) && entry != null && entry.Mtime == mtime.Ticks
                    && (length < 0 || entry.Length == length) && entry.SearchText != null) return true;
                entry = null;
                return false;
            }
        }

        public void Set(string fileName, DateTime mtime, string title, string summary, string searchText)
        {
            long length;
            try { length = new FileInfo(fileName).Length; } catch { length = -1; }
            Set(fileName, new Entry { Mtime = mtime.Ticks, Length = length, Title = title, Summary = summary, SearchText = searchText });
        }

        internal void Set(string fileName, Entry entry)
        {
            lock (gate) { map[fileName] = entry; dirty = true; version++; }
        }

        // Called only after a note has been saved atomically. Keep this memory-only so
        // typing/autosave never rewrites or serializes the full index on the UI thread.
        internal static void UpdateFromText(string path, string plainText)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) return;
                Load().Set(path, FromText(plainText, file.LastWriteTime, file.Length));
            }
            catch { /* Indexing must not turn a successful save into an error. */ }
        }

        internal static Entry FromText(string text, DateTime modified, long length)
        {
            text = text ?? "";
            var lines = new List<string>();
            using (var reader = new StringReader(text))
            {
                string line;
                while (lines.Count < 5 && (line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    line = line.TrimEnd();
                    // Native previews must stay small even for a pasted single-line document.
                    if (line.Length > 256)
                    {
                        int end = char.IsHighSurrogate(line[255]) ? 255 : 256;
                        line = line.Substring(0, end) + "…";
                    }
                    lines.Add(line);
                }
            }
            return new Entry {
                Mtime = modified.Ticks, Length = length,
                Title = lines.Count > 0 ? lines[0] : "（空便签）",
                Summary = string.Join("\n", lines.Skip(1)), SearchText = text.ToLowerInvariant()
            };
        }

        internal static Entry ReadPreview(RichTextBox reader, string path, DateTime modified, long length)
        {
            // Never ask RichEdit to decode bitmaps/OLE objects for a text preview.
            reader.Clear();
            // Keep the original RTF bytes/code page intact; decoding an ANSI RTF as
            // UTF-8 before RichEdit reads \ansicpg corrupts accented/CJK characters.
            var bytes = Encoding.GetEncoding(28591);
            using (var text = new MemoryStream(bytes.GetBytes(TextOnlyRtf(bytes.GetString(File.ReadAllBytes(path))))))
                reader.LoadFile(text, RichTextBoxStreamType.RichText);
            return FromText(reader.Text, modified, length);
        }

        internal static string TextOnlyRtf(string rtf)
        {
            var output = new StringBuilder(rtf.Length < 65536 ? rtf.Length : 65536);
            for (int i = 0; i < rtf.Length;)
            {
                if (rtf[i] == '{' && IsPictureGroup(rtf, i))
                {
                    int depth = 1;
                    i++;
                    while (i < rtf.Length && depth > 0)
                    {
                        if (rtf[i] == '\\') { SkipControl(rtf, ref i); continue; }
                        if (rtf[i] == '{') depth++;
                        else if (rtf[i] == '}') depth--;
                        i++;
                    }
                    output.Append("{\\uc1 [\\u22270?\\u29255?]}");
                    continue;
                }
                if (rtf[i] == '\\')
                {
                    int start = i;
                    SkipControl(rtf, ref i);
                    output.Append(rtf, start, i - start);
                }
                else output.Append(rtf[i++]);
            }
            return output.ToString();
        }

        private static bool IsPictureGroup(string text, int start)
        {
            int i = start + 1;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i + 1 < text.Length && text[i] == '\\' && text[i + 1] == '*')
            { i += 2; while (i < text.Length && char.IsWhiteSpace(text[i])) i++; }
            if (i >= text.Length || text[i++] != '\\') return false;
            int word = i;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            var destination = text.Substring(word, i - word);
            return destination == "pict" || destination == "object" || destination == "shppict" || destination == "nonshppict";
        }

        private static void SkipControl(string text, ref int position)
        {
            position++;
            if (position >= text.Length) return;
            if (!char.IsLetter(text[position]))
            {
                if (text[position] == '\'' && position + 2 < text.Length) position += 3;
                else position++;
                return;
            }
            int start = position;
            while (position < text.Length && char.IsLetter(text[position])) position++;
            bool binary = position - start == 3 && string.CompareOrdinal(text, start, "bin", 0, 3) == 0;
            if (position < text.Length && text[position] == '-') position++;
            int numberStart = position;
            while (position < text.Length && char.IsDigit(text[position])) position++;
            int count = 0;
            if (binary) int.TryParse(text.Substring(numberStart, position - numberStart), out count);
            if (position < text.Length && text[position] == ' ') position++;
            if (binary && count > 0) position += Math.Min(count, text.Length - position);
        }

        public void Remove(string fileName) { lock (gate) if (map.Remove(fileName)) { dirty = true; version++; } }

        public void PruneTo(IEnumerable<string> existingFiles)
        {
            var keep = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                foreach (var key in new List<string>(map.Keys))
                    if (!keep.Contains(key)) { map.Remove(key); dirty = true; version++; }
            }
        }

        public void Save()
        {
            lock (saveGate)
            {
                if (Storage.Incognito) return;
                Dictionary<string, Entry> snapshot;
                long savedVersion;
                lock (gate)
                {
                    if (!dirty) return;
                    snapshot = new Dictionary<string, Entry>(map, StringComparer.OrdinalIgnoreCase);
                    savedVersion = version;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(indexPath));
                    Storage.WriteText(indexPath, JsonConvert.SerializeObject(snapshot));
                    lock (gate) if (version == savedVersion) dirty = false;
                }
                catch { /* Rebuild later; original documents remain intact. */ }
            }
        }
    }
}
