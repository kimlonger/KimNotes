using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes
{
    public class AppWindow : Form
    {
        private bool presented, presenting, dpiRefreshQueued, movingWindow;
        protected bool IsMovingWindow => movingWindow;
        protected virtual bool KeepPixelLayout => true;
        // Lock from handle creation, including Load/restore-position. Otherwise a
        // note reopened on the other monitor would resize its RTF images before
        // the first visible frame even though live dragging keeps them unchanged.
        protected bool IsPixelLayoutLocked => KeepPixelLayout && IsHandleCreated;
        internal bool UsesFixedPixelLayout => KeepPixelLayout;
        private readonly Dictionary<Control, FixedPixelChild> pixelChildren = new Dictionary<Control, FixedPixelChild>();

        public AppWindow() { DoubleBuffered = true; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (KeepPixelLayout) foreach (Control child in Controls) WatchPixelChild(child);
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (KeepPixelLayout) WatchPixelChild(e.Control);
        }

        private void WatchPixelChild(Control control)
        {
            if (control.IsDisposed || pixelChildren.ContainsKey(control)) return;
            pixelChildren.Add(control, new FixedPixelChild(this, control));
            foreach (Control child in control.Controls) WatchPixelChild(child);
        }

        private sealed class FixedPixelChild : NativeWindow
        {
            private readonly AppWindow owner;
            private readonly Control control;
            internal FixedPixelChild(AppWindow owner, Control control)
            {
                this.owner = owner; this.control = control;
                control.HandleCreated += HandleCreated;
                control.ControlAdded += ControlAdded;
                control.Disposed += ControlDisposed;
                if (control.IsHandleCreated) AssignHandle(control.Handle);
            }
            private void HandleCreated(object sender, EventArgs e) { AssignHandle(control.Handle); }
            private void ControlAdded(object sender, ControlEventArgs e) { owner.WatchPixelChild(e.Control); }
            private void ControlDisposed(object sender, EventArgs e) { Detach(); owner.pixelChildren.Remove(control); }
            internal void Detach()
            {
                control.HandleCreated -= HandleCreated;
                control.ControlAdded -= ControlAdded;
                control.Disposed -= ControlDisposed;
                ReleaseHandle();
            }
            protected override void WndProc(ref Message m)
            {
                // Prevent WinForms from rescaling fonts before the parent sees
                // WM_DPICHANGED. Keep PMv2 raw pixels; no bitmap virtualization,
                // no deferred replay and no reset of a RichEdit document/undo stack.
                if (owner.IsPixelLayoutLocked && (m.Msg == 0x02E2 || m.Msg == 0x02E3))
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var child in pixelChildren.Values) child.Detach();
                pixelChildren.Clear();
            }
            base.Dispose(disposing);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Compose initial presentation only, not interactive movement.
                if (!presented) cp.ExStyle |= 0x02000000;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => !presented && !Modal;
        protected virtual void PrepareInitialFrame() { PerformLayout(); }
        protected virtual void PrepareDpiFrame() { PerformLayout(); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0216) movingWindow = true; // WM_MOVING
            if (m.Msg == 0x0232) movingWindow = false; // WM_EXITSIZEMOVE: no delayed scaling or repaint.
            if (IsPixelLayoutLocked)
            {
                if (m.Msg == 0x02E4 && m.LParam != IntPtr.Zero) // WM_GETDPISCALEDSIZE (fallback)
                {
                    Marshal.WriteInt32(m.LParam, 0, Width);
                    Marshal.WriteInt32(m.LParam, 4, Height);
                    m.Result = (IntPtr)1;
                    return;
                }
                if (m.Msg == 0x02E0) // WM_DPICHANGED: monitor DPI != card render scale.
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            movingWindow = false;
            base.OnHandleDestroyed(e);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // Only initial layout / opt-in monitor-scaled tools reach this path.
            if (dpiRefreshQueued || IsDisposed || Disposing || !IsHandleCreated) return;
            dpiRefreshQueued = true;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || Disposing || !IsHandleCreated) { dpiRefreshQueued = false; return; }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            dpiRefreshQueued = false;
                            if (IsDisposed || Disposing || !IsHandleCreated || !Visible) return;
                            PrepareDpiFrame();
                            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
                        }));
                    }
                    catch (InvalidOperationException) { dpiRefreshQueued = false; }
                }));
            }
            catch (InvalidOperationException) { dpiRefreshQueued = false; }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!value || presented || presenting) { base.SetVisibleCore(value); return; }
            presenting = true;
            try
            {
                base.SetVisibleCore(true);
                if (IsDisposed || Disposing || !Visible) return;
                PrepareInitialFrame();
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                PerformLayout();
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
                presented = true;
                UpdateStyles();
                if (!Modal && !ShowWithoutActivation) Activate();
            }
            finally { presenting = false; }
        }

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);
    }
}
