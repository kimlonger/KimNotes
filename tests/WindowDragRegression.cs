using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Exercise the interval between WM_MOVING and WM_EXITSIZEMOVE. Merely assigning
// Location and checking the final layout misses the user's boundary stalls.
internal static class WindowDragRegression
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint flags);

    // Read already-painted native pixels, not DrawToBitmap (which repairs the
    // window while capturing it and hides transient unpainted/black areas).
    internal static HashSet<Point> DarkBlocks(Form form)
    {
        var result = new HashSet<Point>();
        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            var source = GetDC(form.Handle);
            try
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    var dc = g.GetHdc();
                    try { if (!BitBlt(dc, 0, 0, bitmap.Width, bitmap.Height, source, 0, 0, 0x00cc0020)) throw new Exception("Cannot sample native frame"); }
                    finally { g.ReleaseHdc(dc); }
                }
            }
            finally { ReleaseDC(form.Handle, source); }
            for (int y = 12; y < bitmap.Height - 18; y += 8)
            for (int x = 12; x < bitmap.Width - 18; x += 8)
            {
                bool dark = true;
                for (int dy = 0; dy < 6 && dark; dy += 2)
                for (int dx = 0; dx < 6 && dark; dx += 2)
                {
                    var pixel = bitmap.GetPixel(x + dx, y + dy);
                    dark &= pixel.R < 24 && pixel.G < 24 && pixel.B < 24;
                }
                if (dark) result.Add(new Point(x, y));
            }
        }
        return result;
    }

    static IEnumerable<Control> Tree(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Tree(child)) yield return nested;
        }
    }

    internal static void Check(Form form, string name, Action<bool, string> check)
    {
        var screens = Screen.AllScreens;
        if (screens.Length < 2) return;
        bool stable = true, childrenStable = true, settled = true, noBlackFrames = true;
        bool wasTopMost = form.TopMost;
        form.TopMost = true; form.BringToFront(); Application.DoEvents();
        double longest = 0;
        int events = 0;
        DpiChangedEventHandler handler = (s, e) => events++;
        form.DpiChanged += handler;
        try
        {
            for (int round = 0; round < 4; round++)
            {
                var start = screens[round % screens.Length];
                var target = screens[(round + 1) % screens.Length];
                form.Location = new Point(start.WorkingArea.Left + 80, start.WorkingArea.Top + 80);
                Application.DoEvents();
                int oldDpi = form.DeviceDpi, oldEvents = events;
                var size = form.Size;
                var handle = form.Handle;
                var baselineDark = DarkBlocks(form);
                var children = Tree(form).Where(c => c.Visible).Select(c => new {
                    Control = c, Bounds = c.Bounds, FontSize = c.Font.SizeInPoints
                }).ToArray();
                SendMessage(form.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
                // WM_MOVING has a required RECT*, even in a synthetic move loop.
                var rectangle = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.WriteInt32(rectangle, 0, form.Left);
                    Marshal.WriteInt32(rectangle, 4, form.Top);
                    Marshal.WriteInt32(rectangle, 8, form.Right);
                    Marshal.WriteInt32(rectangle, 12, form.Bottom);
                    SendMessage(form.Handle, 0x0216, IntPtr.Zero, rectangle);
                }
                finally { Marshal.FreeHGlobal(rectangle); }
                foreach (var screen in new[] { target, start, target, start, round == 3 ? start : target })
                {
                    var position = new Point(screen.WorkingArea.Left + 80, screen.WorkingArea.Top + 80);
                    var clock = Stopwatch.StartNew();
                    form.Location = position;
                    Application.DoEvents();
                    longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds);
                    bool positionStable = form.Location == position && form.Size == size && events == oldEvents;
                    if (!positionStable) Console.WriteLine(name + " seam mismatch: position " + form.Location + " expected " + position
                        + "; size " + form.Size + " expected " + size + "; DPI events " + oldEvents + " -> " + events);
                    stable &= positionStable;
                    childrenStable &= children.All(c => !c.Control.IsDisposed && c.Control.Bounds == c.Bounds
                        && Math.Abs(c.Control.Font.SizeInPoints - c.FontSize) < 0.01);
                }
                var drop = form.Location;
                SendMessage(form.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
                settled &= form.Size == size && form.Location == drop && form.Handle == handle && events == oldEvents;
                noBlackFrames &= DarkBlocks(form).IsSubsetOf(baselineDark);
                Application.DoEvents();
                for (int frame = 0; frame < 6; frame++)
                {
                    System.Threading.Thread.Sleep(16);
                    Application.DoEvents();
                    noBlackFrames &= DarkBlocks(form).IsSubsetOf(baselineDark);
                    settled &= form.DeviceDpi == oldDpi && form.Size == size && form.Location == drop && form.Handle == handle && events == oldEvents;
                }
            }
        }
        finally { form.DpiChanged -= handler; form.TopMost = wasTopMost; }
        Console.WriteLine(name + " drag stress: max move dispatch " + longest.ToString("F1") + " ms; child stability=" + childrenStable);
        check(stable, name + " crosses monitor seams without resizing or full DPI relayout while moving");
        check(childrenStable, name + " keeps child positions and fonts stable throughout the drag");
        check(settled, name + " keeps exact pixel size, render DPI and native handle at release and in the following frames");
        check(noBlackFrames, name + " native surface has no new black blocks at release or during the next six frames");
        check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(form.Handle), (IntPtr)(-4)),
            name + " uses raw-pixel per-monitor rendering without Windows bitmap scaling or child DPI auto-resizing");
        check((GetWindowLong(form.Handle, -20) & 0x02000000) == 0,
            name + " releases startup-only whole-window composition before interactive dragging");
    }
}
