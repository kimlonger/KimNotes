using System;
using System.Drawing;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    public static partial class ScreenshotHelper
    {
        private static void ShowScreenshotToolbar(Form form)
        {
            var state = form.Tag as ScreenshotState;
            if (state == null || state.SuppressHistory || state.IsAnnotating || form.IsDisposed) return;
            if (state.InlineToolbar != null) { state.InlineToolbar.Visible = true; return; }
            if (state.Toolbar == null || state.Toolbar.IsDisposed)
                state.Toolbar = new ScreenshotToolbarForm(action => RunScreenshotAction(form, action));
            state.Toolbar.Follow(form);
            if (!state.Toolbar.Visible) state.Toolbar.Show(form);
        }

        private static void RunScreenshotAction(Form form, string action)
        {
            var state = form.Tag as ScreenshotState;
            if (state == null || form.IsDisposed) return;
            try
            {
                if (action == "rect" || action == "arrow" || action == "mosaic" || action == "text")
                {
                    form.Activate();
                    if (!state.IsAnnotating) AnnotateImages(GetCurrentImage(form), form);
                    state.SelectAnnotationTool?.Invoke(action);
                    return;
                }
                if (action == "undo") { state.SelectAnnotationTool?.Invoke(action); return; }
                if (action == "close") { state.CleanupAnnotation?.Invoke(); form.Close(); return; }
                state.FinishAnnotation?.Invoke();
                switch (action)
                {
                    case "copy": Clipboard.SetImage(GetCurrentImage(form)); break;
                    case "check":
                        Clipboard.SetImage(GetCurrentImage(form));
                        state.IsColorPicking = false;
                        state.PickerToolTip.Hide(form);
                        var picture = GetPictureBox(form);
                        if (picture != null) picture.Cursor = Cursors.Default;
                        state.Toolbar?.Hide();
                        if (state.InlineToolbar != null) state.InlineToolbar.Visible = false;
                        form.TopMost = true;
                        state.CompleteCapture?.Invoke();
                        form.Activate();
                        break;
                    case "save": SaveWithDialog(GetCurrentImage(form)); break;
                    case "notes": InsertIntoLatestNote(GetCurrentImage(form)); form.Close(); break;
                    case "ocr": state.CompleteCapture?.Invoke(); ScanOCR(GetCurrentImage(form)); break;
                    case "eyedropper": StartColorPick(form); break;
                }
            }
            catch (Exception e)
            {
                Program.LogError(e);
                MessageBox.Show(form, e.Message, "截图操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
