using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace KimNotes.utils
{
    /// <summary>
    /// 便签列表索引缓存：存每篇的首行标题+摘要+修改时间，
    /// 打开历史列表时未变更的便签直接读缓存，不再逐篇解析 RTF。
    /// </summary>
    internal class NoteIndex
    {
        public class Entry
        {
            public long Mtime { get; set; }
            public string Title { get; set; }
            public string Summary { get; set; }
        }

        private static readonly string IndexPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KimNotes",
            "index.json");

        private readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private bool dirty;

        public static NoteIndex Load()
        {
            var index = new NoteIndex();
            try
            {
                if (File.Exists(IndexPath))
                {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(IndexPath));
                    if (loaded != null)
                    {
                        foreach (var kv in loaded) index.map[kv.Key] = kv.Value;
                    }
                }
            }
            catch
            {
                // 索引损坏时当作空索引，重新解析即可
            }
            return index;
        }

        public bool TryGet(string fileName, DateTime mtime, out string title, out string summary)
        {
            title = null;
            summary = null;
            if (map.TryGetValue(fileName, out var entry) && entry != null && entry.Mtime == mtime.Ticks)
            {
                title = entry.Title;
                summary = entry.Summary;
                return true;
            }
            return false;
        }

        public void Set(string fileName, DateTime mtime, string title, string summary)
        {
            map[fileName] = new Entry { Mtime = mtime.Ticks, Title = title, Summary = summary };
            dirty = true;
        }

        public void Remove(string fileName)
        {
            if (map.Remove(fileName)) dirty = true;
        }

        public void PruneTo(IEnumerable<string> existingFiles)
        {
            var keep = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var key in new List<string>(map.Keys))
            {
                if (!keep.Contains(key))
                {
                    map.Remove(key);
                    dirty = true;
                }
            }
        }

        public void Save()
        {
            if (!dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IndexPath));
                File.WriteAllText(IndexPath, JsonConvert.SerializeObject(map));
                dirty = false;
            }
            catch
            {
                // 索引写失败不影响主功能，下次重新解析
            }
        }
    }
}
