using System;
using System.IO;
using System.Linq;
using System.Windows.Documents;
using KimNotes.utils;
using Newtonsoft.Json;

namespace KimNotes.Windows.Notes
{
    internal sealed class NoteRepository
    {
        internal string DirectoryPath => Path.GetFullPath(Path.Combine(InitConfig.GetConfigValue("notesPath"), "."));

        internal string[] VisibleFiles()
        {
            if (Storage.Incognito || !Directory.Exists(DirectoryPath)) return Array.Empty<string>();
            return Directory.EnumerateFiles(DirectoryPath, "*.rtf")
                .Where(CloudStore.CanShow).OrderByDescending(File.GetLastWriteTime).ToArray();
        }

        internal string Resolve(string file)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(DirectoryPath, file));
            if (Storage.Incognito || !string.Equals(Path.GetDirectoryName(full), DirectoryPath, StringComparison.OrdinalIgnoreCase)
                || !full.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) || !CloudStore.CanShow(full))
                throw new InvalidOperationException("当前无法打开这张便签。");
            return full;
        }

        internal FlowDocument Load(string path, bool repair = true)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到这张便签。", path);
            var candidates = new[] { path, path + ".bak" };
            Exception failure = null;
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    FlowDocument document;
                    using (var stream = File.OpenRead(candidate))
                        document = NoteDocumentRtf.Read(stream);
                    if (candidate != path)
                    {
                        if (!File.Exists(path)) throw new FileNotFoundException("找不到这张便签。", path);
                        if (repair && !Storage.Incognito)
                        {
                            string suffix = Guid.NewGuid().ToString("N");
                            string recovery = path + "." + suffix + ".tmp";
                            try
                            {
                                File.Copy(candidate, recovery);
                                File.Replace(recovery, path, path + ".damaged-" + suffix);
                            }
                            finally { if (File.Exists(recovery)) File.Delete(recovery); }
                        }
                    }
                    return document;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                {
                    failure = error;
                }
            }
            if (!File.Exists(path)) throw new FileNotFoundException("找不到这张便签。", path);
            throw new IOException("无法读取便签，原文件已保留。", failure);
        }

        internal string NewPath() => Path.Combine(DirectoryPath, $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.rtf");

        internal void Save(string path, FlowDocument document, string plainText, NoteMetadata metadata)
        {
            CloudStore.ClaimForLocalSave(path);
            if (!File.Exists(path)) metadata.CreatedOnMachine = Environment.MachineName;
            Storage.Write(path, temp =>
            {
                using (var stream = File.Create(temp)) NoteDocumentRtf.Write(document, stream);
            });
            SaveMetadata(path, metadata);
            NoteIndex.UpdateFromText(path, plainText, includesImageMarkers: true);
            CloudSync.Current?.Saved(path);
            Storage.NotifyNotesChanged();
        }

        internal void SaveMetadata(string path, NoteMetadata metadata)
        {
            if (metadata.CreatedOnMachine == null && File.Exists(path + ".meta.json"))
                metadata.CreatedOnMachine = Storage.Metadata(path).CreatedOnMachine;
            Storage.WriteText(path + ".meta.json", JsonConvert.SerializeObject(metadata));
        }

        internal void Delete(string path)
        {
            path = Resolve(path);
            if (!File.Exists(path)) return;
            var recycle = Path.Combine(Storage.Root, "recycle", Guid.NewGuid().ToString("N"), Path.GetFileName(path));
            Storage.Write(recycle, temp => File.Copy(path, temp));
            var sidecars = new[] { ".bak", ".meta.json", ".meta.json.bak" }.Where(suffix => File.Exists(path + suffix)).ToArray();
            foreach (var suffix in sidecars)
                Storage.Write(recycle + suffix, temp => File.Copy(path + suffix, temp));

            var cloud = CloudSync.Current;
            if (cloud == null) File.Delete(path);
            else cloud.DeleteFile(path);
            foreach (var suffix in sidecars)
            {
                try { File.Delete(path + suffix); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Program.LogError(error); }
            }
            var index = NoteIndex.Load();
            index.Remove(path);
            index.Remove(Path.GetFileName(path));
            index.Save();
            Storage.NotifyNotesChanged();
        }
    }
}
