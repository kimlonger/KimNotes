using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace KimNotes.utils
{
    /// <summary>
    /// 一条待办。数据独立存放在 %AppData%\KimNotes\todos.json，便签正文里不再写待办行。
    /// </summary>
    public class TodoItem
    {
        public int Id { get; set; }
        public string Text { get; set; }
        public DateTime? Due { get; set; }
        public bool Done { get; set; }
        public DateTime? DoneAt { get; set; }
        public string FromNote { get; set; }        // 来源便签文件名，null=手记
        public string FromNoteTitle { get; set; }   // 来源便签标题（首行），供列表显示
        public DateTime Created { get; set; }

        /// <summary>提醒时间已过但还没完成。</summary>
        public bool IsOverdue => !Done && Due.HasValue && Due.Value <= DateTime.Now;
    }

    /// <summary>
    /// 待办存储：内存单份 + JSON 落盘，任何改动都通过这里走，改完触发 Changed 让各界面刷新。
    /// </summary>
    internal static class TodoStore
    {
        private static readonly string Dir = Storage.Root;
        private static readonly string StorePath = Path.Combine(Dir, "todos.json");
        private static readonly string FiredPath = Path.Combine(Dir, "reminders-fired.json");

        private static List<TodoItem> items;
        private static int nextId;
        private static bool itemsDirty, firedDirty;

        /// <summary>数据变化（增/改/删/完成）后触发，供便签角标与待办清单刷新。</summary>
        public static event Action Changed;

        public static List<TodoItem> All
        {
            get { EnsureLoaded(); return items; }
        }

        public static int ActiveCount
        {
            get { EnsureLoaded(); return items.Count(t => !t.Done); }
        }

        /// <summary>未完成：提醒时间早的在前，没设提醒的排最后；同一时间新加的在前。</summary>
        public static List<TodoItem> Active()
        {
            EnsureLoaded();
            return items.Where(t => !t.Done)
                        .OrderBy(t => t.Due ?? DateTime.MaxValue)
                        .ThenByDescending(t => t.Id)
                        .ToList();
        }

        /// <summary>已完成：最近完成的在前。</summary>
        public static List<TodoItem> Done()
        {
            EnsureLoaded();
            return items.Where(t => t.Done)
                        .OrderByDescending(t => t.DoneAt ?? t.Created)
                        .ThenByDescending(t => t.Id)
                        .ToList();
        }

        public static TodoItem Find(int id)
        {
            EnsureLoaded();
            return items.FirstOrDefault(t => t.Id == id);
        }

        public static TodoItem Add(string text, DateTime? due, string fromNote, string fromNoteTitle)
        {
            EnsureLoaded();
            var item = new TodoItem
            {
                Id = nextId++,
                Text = (text ?? "").Trim(),
                Due = due,
                Done = false,
                FromNote = string.IsNullOrEmpty(fromNote) ? null : fromNote,
                FromNoteTitle = string.IsNullOrEmpty(fromNoteTitle) ? null : fromNoteTitle,
                Created = DateTime.Now
            };
            items.Add(item);
            // 建的时候就已经过点了（比如晚上八点选「今天 18:00」）：卡片标红即可，不再立刻弹窗
            if (item.Due.HasValue && item.Due.Value <= DateTime.Now) MarkFired(item);
            Save();
            return item;
        }

        public static void SetText(TodoItem item, string text)
        {
            if (item == null) return;
            item.Text = (text ?? "").Trim();
            Save();
        }

        public static void SetDue(TodoItem item, DateTime? due)
        {
            if (item == null) return;
            if (item.Due == due) return;
            item.Due = due;
            if (due.HasValue) Unfire(item);   // 换了时间要重新提醒
            Save();
        }

        public static void SetDone(TodoItem item, bool done)
        {
            if (item == null) return;
            item.Done = done;
            item.DoneAt = done ? (DateTime?)DateTime.Now : null;
            if (done) MarkFired(item);       // 已完成不再提醒
            else if (item.Due.HasValue && item.Due.Value > DateTime.Now) Unfire(item);
            Save();
        }

        private static void EnsureLoaded()
        {
            if (items != null) return;
            items = Storage.Incognito ? new List<TodoItem>() :
                Storage.ReadJson(StorePath, () => new List<TodoItem>()).Where(t => t != null).ToList();
            nextId = items.Count == 0 ? 1 : items.Max(t => t.Id) + 1;
        }

        private static void Save()
        {
            itemsDirty = true;
            try
            {
                Persist();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                System.Windows.Forms.MessageBox.Show("待办保存失败，当前内容仅保留在内存中：" + ex.Message,
                    "保存失败", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            }
            Changed?.Invoke();
        }

        private static void Persist()
        {
            // Persist task contents before dedupe state: a failed task write must
            // never mark the old on-disk reminder as already handled.
            if (!Storage.Incognito)
                Storage.WriteText(StorePath, JsonConvert.SerializeObject(items, Formatting.Indented));
            itemsDirty = false;
            if (firedDirty) SaveFired();
        }

        /// <summary>重试尚未落盘的数据。退出和应用配置前也使用同一写盘顺序。</summary>
        internal static bool TryFlush()
        {
            try
            {
                if (itemsDirty) Persist();
                else if (firedDirty) SaveFired();
                return true;
            }
            catch (Exception ex)
            {
                // Background retries must not show a new warning every thirty seconds.
                Program.LogError(ex);
                return false;
            }
        }

        // ---- 到点提醒去重：同一条待办的同一个提醒时间只弹一次 ----
        private static HashSet<string> fired;

        public static string FiredKey(TodoItem item) => item.Id + "|" + (item.Due?.Ticks.ToString() ?? "");

        private static HashSet<string> LoadFired()
        {
            if (fired != null) return fired;
            return fired = Storage.Incognito ? new HashSet<string>() :
                new HashSet<string>(Storage.ReadJson(FiredPath, () => new List<string>()));
        }

        private static void SaveFired()
        {
            if (!Storage.Incognito)
                Storage.WriteText(FiredPath, JsonConvert.SerializeObject(LoadFired().ToList()));
            firedDirty = false;
        }

        /// <summary>取出到点且没提醒过的未完成待办。弹窗成功后再调用 MarkReminded。</summary>
        public static List<TodoItem> TakeDue()
        {
            EnsureLoaded();
            TryFlush();
            var set = LoadFired();
            var now = DateTime.Now;
            var due = items.Where(t => !t.Done && t.Due.HasValue && t.Due.Value <= now && !set.Contains(FiredKey(t)))
                           .OrderBy(t => t.Due.Value)
                           .ToList();
            return due;
        }

        public static void MarkReminded(IEnumerable<TodoItem> reminders)
        {
            foreach (var item in reminders) LoadFired().Add(FiredKey(item));
            firedDirty = true;
            if (!itemsDirty) SaveFired();
        }

        private static void MarkFired(TodoItem item)
        {
            LoadFired().Add(FiredKey(item));
            firedDirty = true;
        }

        private static void Unfire(TodoItem item)
        {
            var set = LoadFired();
            // 该条待办的历史提醒记录一并清掉，避免换时间后 key 越积越多
            foreach (var key in set.Where(k => k.StartsWith(item.Id + "|")).ToList()) set.Remove(key);
            firedDirty = true;
        }
    }
}
