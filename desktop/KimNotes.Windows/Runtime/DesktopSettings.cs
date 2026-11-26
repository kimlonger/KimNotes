using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KimNotes.utils;
using KimNotes.Windows.Notes;
using Microsoft.Win32;

namespace KimNotes.Windows.Runtime
{
    internal sealed class DesktopSettings
    {
        private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyStartupName = "小羊便签";
        private readonly NoteWorkspace notes;
        private readonly GlobalHotkeys hotkeys;
        private readonly CloudSync cloud;
        private readonly Action<bool> applyWindows;
        private readonly Action applyTransfer;

        internal DesktopSettings(NoteWorkspace notes, GlobalHotkeys hotkeys, CloudSync cloud,
            Action<bool> applyWindows, Action applyTransfer)
        {
            this.notes = notes; this.hotkeys = hotkeys; this.cloud = cloud;
            this.applyWindows = applyWindows; this.applyTransfer = applyTransfer;
        }

        internal string Apply(Dictionary<string, string> changes)
        {
            var before = InitConfig.Snapshot();
            var existingHotkeys = GlobalHotkeys.Read();
            string formerDirectory = notes.Repository.DirectoryPath;
            string formerImages = InitConfig.GetConfigValue("imagesPath");
            bool wasIncognito = Storage.Incognito;
            bool show = InitConfig.ShowTaskbarIcons;
            bool directoryChanged = false;
            bool privacyChanged = false;
            bool keysChanged = false;
            bool persisted = false;
            var startupBefore = new Dictionary<string, (object Value, RegistryValueKind Kind)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var updated = new Dictionary<string, string>(changes);
                string Target(string key) => updated.TryGetValue(key, out var proposed) ? proposed : InitConfig.GetConfigValue(key);
                string notesPath = Path.GetFullPath(Target("notesPath"));
                string imagesPath = Path.GetFullPath(Target("imagesPath"));
                bool newPrivacy = bool.Parse(Target("checkBox3"));
                directoryChanged = !string.Equals(formerDirectory, notesPath, StringComparison.OrdinalIgnoreCase);
                privacyChanged = newPrivacy != wasIncognito;
                if ((directoryChanged || privacyChanged) && cloud.IsCloudOperationActive)
                    return "云端操作正在进行，结束后可保存这项更改。";
                if ((directoryChanged || privacyChanged) && (!CloudSync.FlushOpen() || !TodoStore.TryFlush()))
                    return "有未保存的内容，请先处理保存失败再切换。";
                if (directoryChanged) Storage.CopyNotes(formerDirectory, notesPath);
                if (!string.Equals(formerImages, imagesPath, StringComparison.OrdinalIgnoreCase))
                    Directory.CreateDirectory(imagesPath);
                updated["notesPath"] = notesPath;
                updated["imagesPath"] = imagesPath;
                var keys = existingHotkeys.Keys.ToDictionary(key => key, Target);
                keysChanged = keys.Any(pair => pair.Value != existingHotkeys[pair.Key]);
                if (keysChanged) hotkeys.Apply(keys);
                if (updated.ContainsKey("checkBox1"))
                {
                    bool enableStartup = bool.Parse(Target("checkBox1"));
                    var entries = StartupEntries(Storage.Root, typeof(DesktopSettings).Assembly.Location, enableStartup);
                    using (var registry = Registry.CurrentUser.CreateSubKey(StartupKey))
                    {
                        foreach (var entry in entries)
                        {
                            var old = registry.GetValue(entry.Key, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            startupBefore.Add(entry.Key, (old, old == null ? RegistryValueKind.Unknown : registry.GetValueKind(entry.Key)));
                            SetStartupEntry(registry, entry.Key, entry.Value, RegistryValueKind.String);
                        }
                    }
                }
                updated["pasteShortcutVersion"] = "3";
                InitConfig.SetConfigValues(updated);
                persisted = true;
                if (privacyChanged)
                {
                    cloud.ApplyPrivacyMode();
                    TodoStore.ResetForPrivacyChange();
                }
                applyWindows(InitConfig.ShowTaskbarIcons);
                applyTransfer();
                notes.UpdateDefaultTheme();
                if (directoryChanged) notes.RelocateNotes(formerDirectory, notesPath);
                if (privacyChanged)
                {
                    DesktopSession.Restoring = true;
                    try { notes.CloseNotesWithoutSaving(); }
                    finally { DesktopSession.Restoring = false; }
                    if (newPrivacy) notes.New();
                    else notes.Restore();
                }
                Storage.NotifyNotesChanged();
                return null;
            }
            catch (Exception error)
            {
                try
                {
                    if (persisted)
                    {
                        string changedDirectory = notes.Repository.DirectoryPath;
                        InitConfig.Restore(before);
                        if (privacyChanged)
                        {
                            cloud.ApplyPrivacyMode();
                            TodoStore.ResetForPrivacyChange();
                        }
                        if (directoryChanged) notes.RelocateNotes(changedDirectory, formerDirectory);
                        applyWindows(show);
                        notes.UpdateDefaultTheme();
                        applyTransfer();
                    }
                    if (keysChanged) hotkeys.Apply(existingHotkeys);
                    if (startupBefore.Count > 0)
                        using (var registry = Registry.CurrentUser.CreateSubKey(StartupKey))
                            foreach (var pair in startupBefore) SetStartupEntry(registry, pair.Key, pair.Value.Value, pair.Value.Kind);
                }
                catch (Exception rollbackError) { Program.LogError(rollbackError); }
                Program.LogError(error);
                return error.Message;
            }
        }

        internal static IReadOnlyDictionary<string, string> StartupEntries(string dataRoot, string executablePath, bool enabled)
        {
            var entries = new Dictionary<string, string>
            {
                [typeof(DesktopSettings).Assembly.GetName().Name] = enabled ? "\"" + executablePath + "\"" : null
            };
            if (DesktopInstance.UsesLegacyProfile(dataRoot)) entries.Add(LegacyStartupName, null);
            return entries;
        }

        private static void SetStartupEntry(RegistryKey registry, string name, object value, RegistryValueKind kind)
        {
            if (value == null) registry.DeleteValue(name, false);
            else registry.SetValue(name, value, kind);
        }
    }
}
