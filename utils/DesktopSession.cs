using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;

namespace KimNotes.utils
{
    internal static class DesktopSession
    {
        internal static bool Exiting;
        internal static bool Restoring;
        private static string PathName => Path.Combine(Storage.Root, "open-notes.json");
        private static string lastSaved;

        internal static List<string> Read()
        {
            if (Storage.Incognito || (!File.Exists(PathName) && !File.Exists(PathName + ".bak"))) return null;
            try
            {
                var directory = Path.GetFullPath(InitConfig.GetConfigValue("notesPath"));
                return Storage.ReadJson(PathName, () => new List<string>())
                    .Where(p => !string.IsNullOrEmpty(p) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), directory, StringComparison.OrdinalIgnoreCase)
                        && p.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) { Program.LogError(e); return null; }
        }

        internal static void Remember(note closing = null)
        {
            if (Storage.Incognito || Exiting || Restoring) return;
            try
            {
                var paths = Application.OpenForms.OfType<note>().Where(n => n != closing && !n.IsDisposed && n.Visible && n.FilePath != null)
                    .OrderBy(n => n.OpenedOrder).Select(n => n.FilePath).ToList();
                string text = Newtonsoft.Json.JsonConvert.SerializeObject(paths);
                if (text == lastSaved && File.Exists(PathName)) return;
                Storage.WriteText(PathName, text);
                lastSaved = text;
            }
            catch (Exception e) { Program.LogError(e); }
        }
    }
}
