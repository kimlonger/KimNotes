using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace KimNotes.utils
{
    internal static class Storage
    {
        private static readonly System.Collections.Generic.HashSet<string> damagedMetadata =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Override is also used by the regression runner; real user data is never touched by tests.
        public static string Root => Environment.GetEnvironmentVariable("KIMNOTES_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KimNotes");

        public static bool Incognito => bool.TryParse(InitConfig.GetConfigValue("checkBox3"), out var value) && value;

        public static event Action NotesChanged;
        public static void NotifyNotesChanged() => NotesChanged?.Invoke();

        public static void Write(string path, Action<string> writer)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                writer(temp);
                using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static void WriteText(string path, string text) => Write(path, temp => File.WriteAllText(temp, text));

        public static T ReadJson<T>(string path, Func<T> empty)
        {
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return empty();
            Exception failure = null;
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    var result = JsonConvert.DeserializeObject<T>(File.ReadAllText(candidate));
                    if (result == null) throw new InvalidDataException("文件内容为空：" + candidate);
                    // Recover before the next write can replace the good backup with damaged data.
                    if (candidate != path && !Incognito)
                    {
                        if (File.Exists(path)) File.Copy(path, path + ".damaged-" + Guid.NewGuid().ToString("N"));
                        File.Copy(candidate, path, true);
                    }
                    return result;
                }
                catch (Exception ex) { failure = ex; }
            }
            throw new InvalidDataException("数据文件损坏，已保留原文件，请从备份恢复：" + path, failure);
        }

        public static NoteMetadata Metadata(string notePath)
        {
            string path = notePath + ".meta.json";
            Func<NoteMetadata> fallback = () => new NoteMetadata { ThemeId = NoteTheme.Current().Id };
            try
            {
                var result = ReadJson(path, fallback);
                damagedMetadata.Remove(path);
                return result;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                // A color sidecar must not prevent opening intact note contents.
                // Keep evidence before a later explicit color change replaces it.
                if (damagedMetadata.Add(path))
                {
                    Program.LogError(ex);
                    if (!Incognito && File.Exists(path))
                        try { File.Copy(path, path + ".damaged-" + Guid.NewGuid().ToString("N")); }
                        catch (Exception copyError) { Program.LogError(copyError); }
                }
                return fallback();
            }
        }

        public static void CopyNotes(string source, string destination)
        {
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(destination);
            if (!Directory.Exists(source)) return;
            var files = Directory.GetFiles(source).Where(file =>
                file.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".rtf.bak", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".rtf.meta.json", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".rtf.meta.json.bak", StringComparison.OrdinalIgnoreCase)).ToArray();
            // Check the entire batch first. A destination note is never silently overwritten.
            foreach (var file in files)
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(target) && !File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(target)))
                    throw new IOException("目标目录已有同名文件，请选择其他目录：" + target);
            }
            foreach (var file in files)
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target)) Write(target, temp => File.Copy(file, temp));
            }
        }
    }

    internal sealed class NoteMetadata
    {
        public int ThemeId { get; set; }
    }
}
