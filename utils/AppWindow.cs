using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes
{
    // Publish a complete native child-control frame before activation can run
    // another window's save handler. Parent double buffering alone cannot cover
    // the separate HWNDs used by text fields, labels and toolbar controls.
    public class AppWindow : Form
    {
        private bool presented, presenting;
        public AppWindow() { DoubleBuffered = true; }

        protected override CreateParams CreateParams
        {
            get { var cp=base.CreateParams;cp.ExStyle|=0x02000000;return cp; } // WS_EX_COMPOSITED
        }
        protected override bool ShowWithoutActivation => !presented && !Modal;

        protected virtual void PrepareInitialFrame() { PerformLayout(); }

        protected override void SetVisibleCore(bool value)
        {
            if(!value || presented || presenting) {base.SetVisibleCore(value);return;}
            presenting=true;
            try {
                base.SetVisibleCore(true);
                if(IsDisposed || Disposing || !Visible)return;
                PrepareInitialFrame();
                if(IsDisposed || Disposing || !IsHandleCreated)return;
                PerformLayout();
                // Flush the initial frame synchronously: do not wait for the
                // message loop to paint individual child rectangles later.
                RedrawWindow(Handle,IntPtr.Zero,IntPtr.Zero,0x0185); // INVALIDATE | ERASE | UPDATENOW | ALLCHILDREN
                presented=true;
                if(!Modal && !ShowWithoutActivation)Activate();
            }
            finally {presenting=false;}
        }

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window,IntPtr rectangle,IntPtr region,uint flags);
    }
}
