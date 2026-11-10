using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using KimNotes;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class WindowDragRunner
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    [STAThread] static int Main(string[] args)
    {
        string root = args[0];
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR", root);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var assembly = typeof(note).Assembly;
        assembly.GetType("KimNotes.utils.InitConfig").GetMethod("InitSettings", Flags).Invoke(null, null);
        for (int i = 0; i < 8; i++)
            assembly.GetType("KimNotes.utils.TodoStore").GetMethod("Add", Flags).Invoke(null,
                new object[] { "跨屏测试待办 " + i + "：检查文字、提醒标签与卡片背景", DateTime.Today.AddDays(1).AddHours(9), null, null });
        var folder = Path.Combine(root, "drag-notes"); Directory.CreateDirectory(folder);
        using (var r = new RichTextBox())
        {
            for (int i = 0; i < 24; i++)
            {
                r.Text = "跨屏测试便签 " + i + "\n往返拖动窗口，检查文字、图标与背景。";
                r.SaveFile(Path.Combine(folder, i + ".rtf"));
            }
        }
        int failed = 0;
        if (args.Length > 1 && args[1] == "Menu")
        {
            try { NoteMenuRegression.Run(root, (ok, message) => { Console.WriteLine((ok ? "PASS " : "FAIL ") + message); if (!ok) failed++; }); }
            catch (Exception error)
            {
                for (var current = error; current != null; current = current.InnerException)
                { Console.WriteLine(current.GetType().FullName + ": " + current.Message); Console.WriteLine(current.StackTrace); }
                return 1;
            }
            return failed == 0 ? 0 : 1;
        }
        foreach (var name in new[] { "Settings", "Todo", "History", "Editor", "Note" })
        {
            if (args.Length > 1 && args[1] != name) continue;
            using (Form form = name == "Settings" ? (Form)new KimNotes.settings.ConfigForm()
                : name == "Todo" ? new TodoListForm()
                : name == "History" ? new history(folder)
                : name == "Editor" ? (Form)Activator.CreateInstance(assembly.GetType("KimNotes.utils.TodoEditDialog"), Flags, null, new object[] { "跨屏测试待办", false }, null)
                : (Form)new note())
            {
                form.Show();
                var until = DateTime.UtcNow.AddMilliseconds(400);
                while (DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                if (args.Length > 1)
                {
                    var area = Screen.PrimaryScreen.WorkingArea;
                    form.Location = new System.Drawing.Point(area.Right - form.Width + 40, area.Top + 200);
                    Application.DoEvents();
                    var log = Path.Combine(root, "drag-results.log");
                    bool dragging = false;
                    var baselineDark = WindowDragRegression.DarkBlocks(form);
                    int frames = 0;
                    var auditFrames = new Timer { Interval = 16 };
                    Action recordFrame = () => File.AppendAllText(log, "FRAME " + frames + " " + form.Bounds
                        + " new-black=" + !WindowDragRegression.DarkBlocks(form).IsSubsetOf(baselineDark) + Environment.NewLine);
                    auditFrames.Tick += (s,e) => { frames++; recordFrame(); if (frames >= 12) auditFrames.Stop(); };
                    form.ResizeBegin += (s, e) => { auditFrames.Stop(); baselineDark = WindowDragRegression.DarkBlocks(form); dragging = true; File.AppendAllText(log, "BEGIN render=" + form.DeviceDpi + " monitor=" + GetDpiForWindow(form.Handle) + " " + form.Bounds + Environment.NewLine); };
                    form.DpiChanged += (s, e) => File.AppendAllText(log, "DPI " + e.DeviceDpiOld + " -> " + e.DeviceDpiNew + Environment.NewLine);
                    form.ResizeEnd += (s, e) => { dragging = false; File.AppendAllText(log, "END render=" + form.DeviceDpi + " monitor=" + GetDpiForWindow(form.Handle) + " " + form.Bounds + Environment.NewLine); frames = 0; recordFrame(); auditFrames.Start(); };
                    form.SizeChanged += (s, e) => { if (dragging) File.AppendAllText(log, "SIZE " + form.Size + Environment.NewLine); };
                    Application.Run(form);
                    auditFrames.Dispose();
                    continue;
                }
                WindowDragRegression.Check(form, name, (ok, message) => { Console.WriteLine((ok ? "PASS " : "FAIL ") + message); if (!ok) failed++; });
                form.Close();
            }
        }
        return failed == 0 ? 0 : 1;
    }
}
