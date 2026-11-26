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

        internal FlowDocument Load(string path)
        {
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
                    if (candidate != path && !Storage.Incognito)
                    {
                        if (File.Exists(path)) File.Copy(path, path + ".damaged-" + Guid.NewGuid().ToString("N"));
                        File.Copy(candidate, path, true);
                    }
                    return document;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                {
                    failure = error;
                }
            }
            throw new IOException("无法读取便签，原文件已保留。", failure);
        }

        internal string NewPath() => Path.Combine(DirectoryPath, $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.rtf");

        internal void Save(string path, FlowDocument document, string plainText, NoteMetadata metadata)
        {
            if (!File.Exists(path)) metadata.CreatedOnMachine = Environment.MachineName;
            Storage.Write(path, temp =>
            {
                using (var stream = File.Create(temp)) NoteDocumentRtf.Write(document, stream);
            });
            SaveMetadata(path, metadata);
            NoteIndex.UpdateFromText(path, plainText);
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
            if (File.Exists(path + ".meta.json"))
                Storage.Write(recycle + ".meta.json", temp => File.Copy(path + ".meta.json", temp));
            CloudSync.Current?.Deleted(path);
            File.Delete(path);
            Storage.NotifyNotesChanged();
        }
    }
}
