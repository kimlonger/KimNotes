using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KimNotes;

internal static class ImeRegression
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);

    sealed class StreamWatch : NativeWindow, IDisposable
    {
        internal int Reads;
        internal StreamWatch(Control control) { AssignHandle(control.Handle); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x044A) Reads++; // EM_STREAMOUT also commits pending pinyin.
            base.WndProc(ref message);
        }
        public void Dispose() { ReleaseHandle(); }
    }

    internal static void Run(Action<bool, string> check)
    {
        using (var n = new note())
        {
            n.Show(); n.SetText("已经保存的中文"); Call(n, "SaveCurrentNoteSafe");
            var editor = Field<RichTextBox>(n, "richTextBox1");
            var path = (string)typeof(note).GetProperty("FilePath", Flags).GetValue(n, null);
            var before = File.ReadAllBytes(path);
            editor.AppendText("\n等待选词后的内容");
            using (var watch = new StreamWatch(editor))
            {
                SendMessage(editor.Handle, 0x010D, IntPtr.Zero, IntPtr.Zero);
                watch.Reads = 0;
                Call(n, "AutoSaveTick", DateTime.UtcNow.AddMinutes(1));
                bool direct = (bool)Call(n, "SaveCurrentNoteSafe");
                bool flushed = (bool)typeof(note).Assembly.GetType("KimNotes.utils.CloudSync").GetMethod("FlushOpen", Flags).Invoke(null, null);
                check(!direct && !flushed && File.ReadAllBytes(path).SequenceEqual(before),
                    "unfinished IME input defers timer saves and cloud flushes without replacing the saved document");
                check(watch.Reads == 0 && Field<bool>(n, "hasUnsavedChanges") && !Field<bool>(n, "saveErrorShown"),
                    "waiting beyond the save ceiling does not stream the document, clear edits or report a save error during composition");
                SendMessage(editor.Handle, 0x010E, IntPtr.Zero, IntPtr.Zero);
            }
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddMilliseconds(100));
            check(File.ReadAllBytes(path).SequenceEqual(before), "the idle-save interval restarts when IME composition ends");
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2.1));
            using (var saved = new RichTextBox())
            {
                saved.LoadFile(path);
                check(saved.Text.Contains("等待选词后的内容") && !Field<bool>(n, "hasUnsavedChanges"),
                    "committed Chinese text is saved normally after the idle interval");
            }

            // RichEdit can set Modified for undetermined text without EN_CHANGE.
            // This is the real application's early-save trigger on Windows 11.
            editor.Modified = true;
            typeof(note).GetField("lastEditUtc", Flags).SetValue(n, DateTime.UtcNow.AddMinutes(-1));
            using (var watch = new StreamWatch(editor))
            {
                SendMessage(editor.Handle, 0x010D, IntPtr.Zero, IntPtr.Zero);
                watch.Reads = 0;
                Call(n, "AutoSaveTick", DateTime.UtcNow);
                check(watch.Reads == 0 && editor.Modified,
                    "native Modified without TextChanged cannot force-save and commit undetermined pinyin");
                SendMessage(editor.Handle, 0x010E, IntPtr.Zero, IntPtr.Zero);
            }
            Call(n, "AutoSaveTick", DateTime.UtcNow.AddSeconds(2.1));
            check(!editor.Modified, "autosave resumes after a native-only IME edit completes");
            SendMessage(editor.Handle, 0x010D, IntPtr.Zero, IntPtr.Zero);
            SendMessage(editor.Handle, 0x010E, IntPtr.Zero, IntPtr.Zero);
            check(!Field<bool>(n, "hasUnsavedChanges"), "ending an unchanged composition does not mark the document dirty");
            n.Close();
        }
    }
}
