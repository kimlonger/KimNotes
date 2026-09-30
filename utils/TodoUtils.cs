using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace KimNotes.utils
{
    /// <summary>
    /// 待办行约定格式：☐/☑ 开头，可选 @日期 时间 作为提醒时间。
    /// 例：☐ 交报告 @2026-09-30 18:00 ；☑ 买牛奶（无时间=纯清单项）
    /// </summary>
    internal static class TodoUtils
    {
        public const char BoxOpen = '☐';
        public const char BoxDone = '☑';

        public class TodoItem
        {
            public string FileName;
            public int LineIndex;      // 行号（0 起）
            public bool Checked;
            public string Text;
            public DateTime? Due;
        }

        private static readonly Regex LineRegex = new Regex(
            @"^\s*(?<box>[☐☑])\s*(?<text>.*?)(?:\s*@(?<date>\d{4}-\d{1,2}-\d{1,2}|\d{1,2}-\d{1,2})\s+(?<time>\d{1,2}:\d{2}))?\s*$",
            RegexOptions.Compiled);

        public static bool TryParseLine(string line, out bool checked_, out string text, out DateTime? due)
        {
            checked_ = false;
            text = null;
            due = null;
            if (string.IsNullOrEmpty(line)) return false;

            var m = LineRegex.Match(line);
            if (!m.Success) return false;

            checked_ = m.Groups["box"].Value == BoxDone.ToString();
            text = m.Groups["text"].Value.Trim();

            if (m.Groups["date"].Success)
            {
                due = ParseDue(m.Groups["date"].Value, m.Groups["time"].Success ? m.Groups["time"].Value : null);
            }
            return true;
        }

        private static DateTime? ParseDue(string datePart, string timePart)
        {
            int year, month, day;
            var dp = datePart.Split('-');
            if (dp.Length == 3)
            {
                year = int.Parse(dp[0]); month = int.Parse(dp[1]); day = int.Parse(dp[2]);
            }
            else if (dp.Length == 2)
            {
                year = DateTime.Now.Year; month = int.Parse(dp[0]); day = int.Parse(dp[1]);
            }
            else return null;

            int hour = 0, minute = 0;
            if (!string.IsNullOrEmpty(timePart))
            {
                var tp = timePart.Split(':');
                if (tp.Length == 2) { hour = int.Parse(tp[0]); minute = int.Parse(tp[1]); }
            }

            try { return new DateTime(year, month, day, hour, minute, 0); }
            catch { return null; }
        }

        public static string FormatLine(bool checked_, string text, DateTime? due)
        {
            string s = (checked_ ? BoxDone : BoxOpen) + " " + text;
            if (due.HasValue) s += " @" + due.Value.ToString("yyyy-MM-dd HH:mm");
            return s;
        }

        // 扫描缓存：file -> (mtime, items)，未变更的便签不重复解析
        private class ScanCacheEntry
        {
            public long Mtime;
            public List<TodoItem> Items;
        }
        private static readonly Dictionary<string, ScanCacheEntry> scanCache =
            new Dictionary<string, ScanCacheEntry>(StringComparer.OrdinalIgnoreCase);

        public static List<TodoItem> ScanAll(string folder)
        {
            var result = new List<TodoItem>();
            if (!Directory.Exists(folder)) return result;

            using (var reader = new RichTextBox())
            {
                foreach (var file in Directory.GetFiles(folder, "*.rtf"))
                {
                    string name = Path.GetFileName(file);
                    long mtime = File.GetLastWriteTime(file).Ticks;

                    if (scanCache.TryGetValue(name, out var entry) && entry.Mtime == mtime)
                    {
                        result.AddRange(entry.Items);
                        continue;
                    }

                    var items = ParseFile(reader, file, name);
                    scanCache[name] = new ScanCacheEntry { Mtime = mtime, Items = items };
                    result.AddRange(items);
                }
            }
            return result;
        }

        private static List<TodoItem> ParseFile(RichTextBox reader, string file, string name)
        {
            var items = new List<TodoItem>();
            try
            {
                reader.Clear();
                reader.LoadFile(file, RichTextBoxStreamType.RichText);
                var lines = reader.Text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (TryParseLine(lines[i], out bool chk, out string text, out DateTime? due))
                    {
                        items.Add(new TodoItem
                        {
                            FileName = name,
                            LineIndex = i,
                            Checked = chk,
                            Text = text,
                            Due = due
                        });
                    }
                }
            }
            catch { }
            return items;
        }

        public static void Invalidate(string fileName)
        {
            scanCache.Remove(fileName);
        }

        // ---- 已提醒去重状态 ----
        private static readonly string FiredPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KimNotes", "reminders-fired.json");

        public static HashSet<string> LoadFired()
        {
            try
            {
                if (File.Exists(FiredPath))
                {
                    var list = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(FiredPath));
                    if (list != null) return new HashSet<string>(list);
                }
            }
            catch { }
            return new HashSet<string>();
        }

        public static void SaveFired(HashSet<string> fired)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FiredPath));
                File.WriteAllText(FiredPath, JsonConvert.SerializeObject(fired.ToList()));
            }
            catch { }
        }

        public static string FiredKey(TodoItem item)
        {
            return item.FileName + "|" + item.LineIndex + "|" + item.Text + "|" + (item.Due?.Ticks.ToString() ?? "");
        }
    }
}
