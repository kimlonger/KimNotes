using System.IO;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    public partial class note
    {
        internal void ReloadCloud()
        {
            if (FilePath == null || !File.Exists(FilePath)) return;
            int start = richTextBox1.SelectionStart, length = richTextBox1.SelectionLength;
            suppressTextChangedTracking = true;
            try {
                LoadRtfWithBackup(FilePath);
                var metadata = Storage.Metadata(FilePath);
                ApplyTheme(NoteTheme.All.FirstOrDefault(t => t.Id == metadata.ThemeId) ?? NoteTheme.Current());
                richTextBox1.Select(System.Math.Min(start,richTextBox1.TextLength),System.Math.Min(length,System.Math.Max(0,richTextBox1.TextLength-start)));
                hasUnsavedChanges = false; richTextBox1.Modified = false; metadataDirty = false;
                SetSaveStatus("已同步");
            } finally { suppressTextChangedTracking = false; }
        }
    }
}
