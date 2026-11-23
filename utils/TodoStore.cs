using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace KimNotes.utils
{
    public enum RepeatRule { None, Daily, Weekdays, Weekly }
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
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? DeletedAt { get; set; }
        public string FromNote { get; set; }        // 来源便签文件名，null=手记
        public string FromNoteTitle { get; set; }   // 来源便签标题（首行），供列表显示
        public DateTime Created { get; set; }
        public RepeatRule Repeat { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        internal string CloudId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        internal string CloudOwnerId { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        internal long CloudRevision { get; set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
        internal long CloudSyncedRevision { get; set; }

        internal TodoItem Copy() => (TodoItem)MemberwiseClone();

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
            get { return VisibleItems().ToList(); }
        }

        public static int ActiveCount
        {
            get { return VisibleItems().Count(t => !t.Done); }
        }

        /// <summary>未完成：提醒时间早的在前，没设提醒的排最后；同一时间新加的在前。</summary>
        public static List<TodoItem> Active()
        {
            return VisibleItems().Where(t => !t.Done)
                        .OrderBy(t => t.Due ?? DateTime.MaxValue)
                        .ThenByDescending(t => t.Id)
                        .ToList();
        }

        /// <summary>已完成：最近完成的在前。</summary>
        public static List<TodoItem> Done()
        {
            return VisibleItems().Where(t => t.Done)
                        .OrderByDescending(t => t.DoneAt ?? t.Created)
                        .ThenByDescending(t => t.Id)
                        .ToList();
        }

        public static TodoItem Find(int id)
        {
            return VisibleItems().FirstOrDefault(t => t.Id == id);
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
            MarkCloudChanged(item);
            // 建的时候就已经过点了（比如晚上八点选「今天 18:00」）：卡片标红即可，不再立刻弹窗
            if (item.Due.HasValue && item.Due.Value <= DateTime.Now) MarkFired(item);
            Save();
            return item;
        }

        public static void SetText(TodoItem item, string text)
        {
            if (item == null || item.DeletedAt.HasValue) return;
            text = (text ?? "").Trim();
            if (item.Text == text) return;
            item.Text = text;
            MarkCloudChanged(item);
            Save();
        }

        public static void SetDue(TodoItem item, DateTime? due)
        {
            if (item == null || item.DeletedAt.HasValue) return;
            if (item.Due == due) return;
            item.Due = due;
            if (!due.HasValue) item.Repeat = RepeatRule.None;
            if (due.HasValue) Unfire(item);   // 换了时间要重新提醒
            MarkCloudChanged(item);
            Save();
        }

        public static void SetDone(TodoItem item, bool done)
        {
            if (item == null || item.DeletedAt.HasValue) return;
            if (done && item.Repeat != RepeatRule.None && item.Due.HasValue)
            {
                item.Due = NextOccurrence(item.Due.Value, item.Repeat, DateTime.Now);
                item.Done = false;
                item.DoneAt = DateTime.Now;
                Unfire(item);
                MarkCloudChanged(item);
                Save();
                return;
            }
            if (item.Done == done) return;
            item.Done = done;
            item.DoneAt = done ? (DateTime?)DateTime.Now : null;
            if (done) MarkFired(item);       // 已完成不再提醒
            else if (item.Due.HasValue && item.Due.Value > DateTime.Now) Unfire(item);
            MarkCloudChanged(item);
            Save();
        }

        public static void DeleteCompleted(TodoItem item)
        {
            if (item == null || !item.Done || item.DeletedAt.HasValue) return;
            item.DeletedAt = DateTime.UtcNow;
            MarkCloudChanged(item);
            Save();
        }

        private static void ClaimForCloud(TodoItem item, string userId)
        {
            item.CloudOwnerId = userId;
            item.CloudId = Guid.NewGuid().ToString();
            item.CloudRevision = 1;
            item.CloudSyncedRevision = 0;
        }

        private static void MarkCloudChanged(TodoItem item)
        {
            if (item.CloudId != null) item.CloudRevision++;
            else if (CloudStore.UserId != null && !Storage.Incognito && !item.DeletedAt.HasValue)
                ClaimForCloud(item, CloudStore.UserId);
        }

        private static bool NeedsCloudUpload(TodoItem item, string userId) =>
            item.CloudOwnerId == null ? !item.DeletedAt.HasValue :
            item.CloudOwnerId == userId && item.CloudRevision > item.CloudSyncedRevision;

        internal static bool HasPendingCloudChanges(string userId)
        {
            EnsureLoaded();
            return items.Any(item => NeedsCloudUpload(item, userId));
        }

        internal static List<TodoItem> PrepareCloudChanges(string userId)
        {
            EnsureLoaded();
            var pending = items.Where(item => NeedsCloudUpload(item, userId)).ToList();
            foreach (var item in pending.Where(item => item.CloudOwnerId == null))
            {
                ClaimForCloud(item, userId);
                itemsDirty = true;
            }
            // Both the record identity and the revision must survive a failed request or a restart.
            if (!TryFlush()) throw new IOException("待办尚未保存，暂缓同步。");
            return pending.Select(item => item.Copy()).ToList();
        }

        internal static void AcknowledgeCloudChanges(string userId, IReadOnlyList<TodoItem> sent)
        {
            var revisions = sent.ToDictionary(item => item.CloudId, item => item.CloudRevision);
            foreach (var item in items)
            {
                if (item.CloudOwnerId != userId || item.CloudId == null ||
                    !revisions.TryGetValue(item.CloudId, out var revision) || revision <= item.CloudSyncedRevision)
                    continue;
                // A newer edit may already exist while the request is in flight.
                item.CloudSyncedRevision = revision;
                itemsDirty = true;
            }
            if (!TryFlush()) throw new IOException("待办同步状态尚未保存。");
        }

        private static IEnumerable<TodoItem> VisibleItems()
        {
            EnsureLoaded();
            return items.Where(t => !t.DeletedAt.HasValue);
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
            => TakeDueAt(DateTime.Now);

        internal static List<TodoItem> TakeDueAt(DateTime now)
        {
            var visible = VisibleItems();
            TryFlush();
            var set = LoadFired();
            bool changed = false;
            foreach (var item in visible.Where(t => !t.Done && t.Due.HasValue && t.Repeat != RepeatRule.None && t.Due.Value <= now))
            {
                var latest = LatestOccurrence(item.Due.Value, item.Repeat, now);
                if (latest != item.Due.Value)
                {
                    item.Due = latest;
                    Unfire(item);
                    MarkCloudChanged(item);
                    changed = true;
                }
            }
            if (changed) Save();
            var due = visible.Where(t => !t.Done && t.Due.HasValue && t.Due.Value <= now && !set.Contains(FiredKey(t)))
                           .OrderBy(t => t.Due.Value)
                           .ToList();
            return due;
        }

        public static void SetSchedule(TodoItem item, DateTime? due, RepeatRule repeat)
        {
            if (item == null || item.DeletedAt.HasValue) return;
            if (!Enum.IsDefined(typeof(RepeatRule), repeat)) repeat = RepeatRule.None;
            if (!due.HasValue) repeat = RepeatRule.None;
            if (due.HasValue && repeat == RepeatRule.Weekdays)
                while (IsWeekend(due.Value)) due = due.Value.AddDays(1);
            if (item.Due == due && item.Repeat == repeat) return;
            item.Due = due; item.Repeat = repeat;
            MarkCloudChanged(item);
            Unfire(item); Save();
        }

        private static bool IsWeekend(DateTime date) => date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;

        internal static DateTime NextOccurrence(DateTime anchor, RepeatRule repeat, DateTime now)
        {
            int step = repeat == RepeatRule.Weekly ? 7 : 1;
            long jumps = Math.Max(1L, (long)Math.Floor((now - anchor).TotalDays / step) + 1);
            var next = anchor.AddDays(jumps * step);
            if (repeat == RepeatRule.Weekdays) while (IsWeekend(next)) next = next.AddDays(1);
            return next;
        }

        private static DateTime LatestOccurrence(DateTime anchor, RepeatRule repeat, DateTime now)
        {
            int step = repeat == RepeatRule.Weekly ? 7 : 1;
            int jumps = Math.Max(0, (int)Math.Floor((now - anchor).TotalDays / step));
            var latest = anchor.AddDays(jumps * step);
            if (repeat == RepeatRule.Weekdays) while (IsWeekend(latest) && latest > anchor) latest = latest.AddDays(-1);
            return latest;
        }

        internal static int NextCheckInterval(DateTime now)
        {
            DateTime next = now.AddSeconds(30);
            foreach (var item in VisibleItems().Where(t => !t.Done && t.Due.HasValue))
            {
                var time = item.Due.Value;
                if (LoadFired().Contains(FiredKey(item)))
                {
                    if (item.Repeat == RepeatRule.None) continue;
                    time = NextOccurrence(time, item.Repeat, now);
                }
                if (time < next) next = time;
            }
            return (int)Math.Max(1000, Math.Min(30000, (next - now).TotalMilliseconds));
        }

        public static string RepeatLabel(RepeatRule repeat)
        {
            switch (repeat) { case RepeatRule.Daily: return "每天"; case RepeatRule.Weekdays: return "工作日"; case RepeatRule.Weekly: return "每周"; default: return "不重复"; }
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
