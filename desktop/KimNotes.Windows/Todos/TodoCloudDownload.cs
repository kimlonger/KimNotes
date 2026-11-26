using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace KimNotes.utils
{
    internal sealed class TodoCloudEntry
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? DueAt { get; set; }
        public bool Done { get; set; }
        public DateTime? DoneAt { get; set; }
        public RepeatRule Repeat { get; set; }
        public long Revision { get; set; }
        public DateTime? DeletedAt { get; set; }
        public TodoPriority? Priority { get; set; }
        public string Notes { get; set; }
        public bool? Pinned { get; set; }
    }

    internal static class TodoCloudDownload
    {
        internal static async Task Pull(CloudApi api, CloudSession owner, CancellationToken cancel)
        {
            var entries = new Dictionary<string, TodoCloudEntry>(StringComparer.OrdinalIgnoreCase);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            do
            {
                string path = "/todos?includeDeleted=1";
                if (cursor != null) path += "&cursor=" + Uri.EscapeDataString(cursor);
                var page = await api.Json(path, token: owner.Token, cancel: cancel);
                if (!(page["todos"] is JArray todos)) throw new InvalidDataException("待办下载响应无效。");
                foreach (var token in todos)
                {
                    var item = token.ToObject<TodoCloudEntry>();
                    if (item == null || !Guid.TryParse(item.Id, out var id) || item.Revision < 0 || item.Revision == long.MaxValue ||
                        (item.DeletedAt.HasValue ? item.Revision == 0 : !item.CreatedAt.HasValue ||
                            string.IsNullOrWhiteSpace(item.Text) || item.Repeat < RepeatRule.None || item.Repeat > RepeatRule.Weekly) ||
                        item.Priority.HasValue && (item.Priority < TodoPriority.Normal || item.Priority > TodoPriority.Urgent))
                        throw new InvalidDataException("待办下载包含无效记录。");
                    item.Id = id.ToString();
                    if (!entries.TryGetValue(item.Id, out var previous) || item.Revision >= previous.Revision)
                        entries[item.Id] = item;
                }
                cursor = page.Value<string>("nextCursor");
                if (string.IsNullOrEmpty(cursor)) break;
                if (!cursors.Add(cursor)) throw new InvalidDataException("待办下载分页重复。");
            } while (true);
            cancel.ThrowIfCancellationRequested();
            TodoStore.MergeDownloaded(owner.UserId, entries.Values);
        }
    }

    internal static partial class TodoStore
    {
        internal static bool HasPendingLegacySync(string owner)
        {
            EnsureLoaded();
            return items.Any(item => item.CloudOwnerId == owner && item.CloudRevisionUnknown &&
                item.CloudRevision > item.CloudSyncedRevision);
        }

        internal static bool HasUnsyncedDetails(string owner)
        {
            EnsureLoaded();
            return items.Any(item => item.CloudOwnerId == owner && item.CloudDetailsPending && !item.DeletedAt.HasValue);
        }

        internal static void MergeDownloaded(string owner, IEnumerable<TodoCloudEntry> downloaded)
        {
            if (owner != CloudStore.UserId || Storage.Incognito) return;
            EnsureLoaded();
            var known = items.Where(item => item.CloudOwnerId == owner && item.CloudId != null)
                .ToDictionary(item => item.CloudId, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (var incoming in downloaded)
            {
                if (known.TryGetValue(incoming.Id, out var existing))
                {
                    changed |= ApplyDownloaded(existing, incoming);
                }
                else if (!incoming.DeletedAt.HasValue)
                {
                    var item = new TodoItem
                    {
                        Id = nextId++, CloudId = incoming.Id, CloudOwnerId = owner,
                        DownloadedFromCloud = true, CloudRevisionUnknown = incoming.Revision == 0
                    };
                    ApplyDownloaded(item, incoming);
                    items.Add(item);
                    known.Add(incoming.Id, item);
                    changed = true;
                }
            }
            if (!changed) return;
            itemsDirty = true;
            Changed?.Invoke();
            if (!TryFlush()) throw new IOException("已下载的待办暂未写入磁盘。");
        }

        private static bool ApplyDownloaded(TodoItem item, TodoCloudEntry incoming)
        {
            long revision = incoming.Revision;
            if (revision > 0 && revision < item.CloudSyncedRevision) return false;

            if (incoming.DeletedAt.HasValue)
            {
                if (item.DeletedAt == incoming.DeletedAt && item.CloudRevision == revision && !item.CloudRevisionUnknown) return false;
                item.DeletedAt = incoming.DeletedAt;
                item.CloudSyncedRevision = item.CloudRevision = revision;
                item.CloudRevisionUnknown = false;
                return true;
            }

            if (item.DeletedAt.HasValue || item.CloudRevision > item.CloudSyncedRevision)
            {
                // Keep local edits through a refresh. A versioned response lets the next upload
                // apply the edit on top of the current server revision.
                if (revision == 0 || revision == item.CloudSyncedRevision && !item.CloudRevisionUnknown) return false;
                item.CloudRevision = Math.Max(item.CloudRevision, revision + 1);
                item.CloudSyncedRevision = revision;
                item.CloudRevisionUnknown = false;
                return true;
            }

            var created = incoming.CreatedAt.Value.ToLocalTime();
            var due = incoming.DueAt?.ToLocalTime();
            var doneAt = incoming.DoneAt?.ToLocalTime();
            bool receivesDetails = !item.CloudDetailsPending && incoming.Priority.HasValue && incoming.Pinned.HasValue;
            var priority = receivesDetails ? incoming.Priority.Value : item.Priority;
            var notes = receivesDetails ? NormalizeNotes(incoming.Notes) : item.Notes;
            var pinned = receivesDetails ? incoming.Pinned.Value : item.Pinned;
            bool dataChanged = item.Text != incoming.Text || item.Created != created || item.Due != due ||
                item.Done != incoming.Done || item.DoneAt != doneAt || item.Repeat != incoming.Repeat ||
                item.Priority != priority || item.Notes != notes || item.Pinned != pinned;
            bool revisionChanged = revision > 0 && (revision != item.CloudSyncedRevision || item.CloudRevisionUnknown);
            if (revisionChanged)
            {
                item.CloudSyncedRevision = item.CloudRevision = revision;
                item.CloudRevisionUnknown = false;
            }
            if (dataChanged)
            {
                item.Text = incoming.Text;
                item.Created = created;
                item.Due = due;
                item.Done = incoming.Done;
                item.DoneAt = doneAt;
                item.Repeat = incoming.Repeat;
                item.Priority = priority;
                item.Notes = notes;
                item.Pinned = pinned;
            }
            return dataChanged || revisionChanged;
        }
    }
}
