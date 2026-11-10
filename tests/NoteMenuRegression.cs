using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using KimNotes;

internal static class NoteMenuRegression
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint flags);
    static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);

    // Capture existing pixels. DrawToBitmap would repair the defect being tested.
    static Bitmap Capture(Form form)
    {
        var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        var source = GetDC(form.Handle);
        try
        {
            using (var graphics = Graphics.FromImage(image))
            {
                var target = graphics.GetHdc();
                try { if (!BitBlt(target, 0, 0, image.Width, image.Height, source, 0, 0, 0x00cc0020)) throw new Exception("Cannot capture note menu surface"); }
                finally { graphics.ReleaseHdc(target); }
            }
        }
        finally { ReleaseDC(form.Handle, source); }
        return image;
    }

    static double Difference(Bitmap actual, Bitmap expected, int height)
    {
        int different = 0, count = 0;
        for (int y = 12; y < Math.Min(height, actual.Height - 12); y += 2)
        for (int x = 12; x < actual.Width - 12; x += 2)
        {
            var a = actual.GetPixel(x, y); var b = expected.GetPixel(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 30) different++;
            count++;
        }
        return count == 0 ? 0 : different / (double)count;
    }

    internal static void Run(string root, Action<bool, string> check)
    {
        var app = typeof(note).Assembly;
        var contextField = app.GetType("Program").GetField("AppContext", Flags);
        var originalContext = contextField.GetValue(null);
        var context = FormatterServices.GetUninitializedObject(app.GetType("KimNotes.MyApplicationContext"));
        contextField.SetValue(null, context);
        try
        {
            using (var form = new note())
            {
                form.TopMost = true;
                form.Show(); Application.DoEvents();
                var editor = Field<RichTextBox>(form, "richTextBox1");
                editor.Text = "菜单关闭后，这段文字和标题栏应立即完整显示。\n待办、便签列表和设置都要验证。";
                Call(form, "SaveCurrentNoteSafe");
                int round = 0;
                foreach (var screen in Screen.AllScreens)
                foreach (var name in new[] { "Todo", "List", "Set" })
                {
                    form.Location = new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 60);
                    form.Activate();
                    Call(form, "Touch");
                    Call(form, "AdvanceChromeAnimation", DateTime.UtcNow.AddSeconds(2));
                    Call(form, "ToggleMore");
                    Application.DoEvents();
                    var menu = Field<Panel>(form, "morePanel");
                    var row = Field<Panel>(form, "moreRow" + name);
                    int coveredHeight = menu.Bottom;
                    var size = form.Size;
                    typeof(Control).GetMethod("OnClick", Flags).Invoke(row, new object[] { EventArgs.Empty });
                    using (var immediate = Capture(form))
                    {
                        Application.DoEvents();
                        using (var settled = Capture(form))
                        {
                            // Only after taking both unassisted frames, obtain a fully painted reference.
                            RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
                            using (var expected = Capture(form))
                            {
                                var prefix = Path.Combine(root, "menu-" + name + "-" + round++);
                                immediate.Save(prefix + "-immediate.png");
                                settled.Save(prefix + "-settled.png");
                                expected.Save(prefix + "-reference.png");
                                double firstDiff = Difference(immediate, expected, coveredHeight);
                                double settledDiff = Difference(settled, expected, coveredHeight);
                                Console.WriteLine(name + " menu pixel differences: immediate=" + firstDiff.ToString("P2") + "; settled=" + settledDiff.ToString("P2"));
                                check(!menu.Visible && form.Size == size && firstDiff < 0.005 && settledDiff < 0.005,
                                    name + " restores the note title and document before opening or activating its window on " + screen.DeviceName);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (var name in new[] { "todoForm", "historyForm", "configForm" })
                Field<Form>(context, name)?.Close();
            contextField.SetValue(null, originalContext);
        }
    }
}
