using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    public static partial class ScreenshotHelper
    {
        private static WeakReference<Form> recentViewer = new WeakReference<Form>(null);
        private static string recentHistoryId;

        private static bool RememberScreenshot(Form form)
        {
            var state = form.Tag as ScreenshotState;
            if (state == null || state.SuppressHistory) return true;
            try
            {
                if (state.HistoryDirty || state.HistoryId == null)
                    state.HistoryId = ScreenshotArchive.Save(GetCurrentImage(form), state.Incognito, state.HistoryId);
                state.HistoryDirty = false;
                recentHistoryId = state.HistoryId;
                recentViewer.SetTarget(form);
                return true;
            }
            catch (Exception e) { Program.LogError(e); return false; }
        }

        public static Form RecallLatest()
        {
            return OpenHistory(0, true);
        }

        public static Form NavigateHistory(int direction) => OpenHistory(direction, false);

        private static void DestroyScreenshot(Form form)
        {
            var state = form.Tag as ScreenshotState;
            if (state == null || form.IsDisposed) return;
            try
            {
                string id = state.HistoryId;
                if (!state.SuppressHistory && id != null) ScreenshotArchive.Delete(id, state.Incognito);
                var viewers = Application.OpenForms.Cast<Form>().Where(f => ReferenceEquals(f, form)
                    || (id != null && !state.SuppressHistory && f.Tag is ScreenshotState other
                        && !other.SuppressHistory && other.HistoryId == id && other.Incognito == state.Incognito)).ToArray();
                // Closing normally saves history. Suppress that for every view of
                // the deleted record so a later close cannot resurrect the image.
                foreach (var viewer in viewers) ((ScreenshotState)viewer.Tag).SuppressHistory = true;
                if (id != null && recentHistoryId == id) recentHistoryId = null;
                if (recentViewer.TryGetTarget(out var recent) && viewers.Contains(recent)) recentViewer.SetTarget(null);
                foreach (var viewer in viewers) viewer.Close();
            }
            catch (Exception e)
            {
                Program.LogError(e);
                MessageBox.Show(form, "贴图未能销毁，请重试。\n" + e.Message, "销毁贴图失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static Form OpenHistory(int direction, bool latest)
        {
            bool incognito = Storage.Incognito;
            Form viewer;
            recentViewer.TryGetTarget(out viewer);
            if (viewer == null || viewer.IsDisposed || (viewer.Tag as ScreenshotState)?.Incognito != incognito) viewer = null;
            if ((viewer?.Tag as ScreenshotState)?.IsAnnotating == true || (viewer?.Tag as ScreenshotState)?.IsColorPicking == true)
            { viewer.Activate(); return viewer; }
            if (viewer != null && !RememberScreenshot(viewer)) return viewer;
            var entries = ScreenshotArchive.List(incognito);
            if (entries.Length == 0) return null;
            int index = latest ? 0 : Array.IndexOf(entries, viewer == null ? recentHistoryId : ((ScreenshotState)viewer.Tag).HistoryId);
            if (index < 0) index = 0;
            if (!latest) index = Math.Max(0, Math.Min(entries.Length - 1, index + direction));
            using (var image = ScreenshotArchive.Load(entries[index], incognito))
            {
                if (image == null) return viewer;
                if (viewer == null)
                {
                    var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                    viewer = CreateScreenshotForm(new Bitmap(image), new Rectangle(wa.Left + 24, wa.Top + 24, image.Width, image.Height));
                    var state = (ScreenshotState)viewer.Tag;
                    state.HistoryId = entries[index];
                    state.HistoryDirty = false;
                    state.Incognito = incognito;
                    state.AutoMenu = false;
                    state.ImagePath = InitConfig.GetConfigValue("imagesPath");
                    state.Scale = Math.Min(1f, Math.Min((wa.Width - 48f) / image.Width, (wa.Height - 48f) / image.Height));
                    viewer.Show();
                }
                else ReplaceHistoryImage(viewer, image, entries[index]);
                recentHistoryId = entries[index];
                recentViewer.SetTarget(viewer);
                viewer.Activate();
                UpdateHistoryTitle(viewer, entries);
                return viewer;
            }
        }

        private static void ReplaceHistoryImage(Form form, Image image, string id)
        {
            var state = (ScreenshotState)form.Tag;
            var pb = GetPictureBox(form);
            var previous = pb.Image;
            pb.Image = new Bitmap(image);
            previous?.Dispose();
            state.HistoryId = id;
            state.HistoryDirty = false;
            state.OriginalSize = image.Size;
            var wa = Screen.FromControl(form).WorkingArea;
            state.Scale = Math.Min(1f, Math.Min((wa.Width - 32f) / image.Width, (wa.Height - 32f) / image.Height));
            ApplyScale(form, pb, state);
            ClampToScreen(form);
            pb.Invalidate();
        }

        private static void UpdateHistoryTitle(Form form, string[] entries)
        {
            int index = Array.IndexOf(entries, ((ScreenshotState)form.Tag).HistoryId);
            form.Text = "贴图 " + (index + 1) + " / " + entries.Length;
            ((ScreenshotState)form.Tag).PickerToolTip.SetToolTip(GetPictureBox(form), form.Text + " · ← 上一张 / → 下一张");
        }

        private static void NavigateWindow(Form form, int direction)
        {
            recentViewer.SetTarget(form);
            NavigateHistory(direction);
        }

        internal static Form PreviewImage(Image image)
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            var form = CreateScreenshotForm(new Bitmap(image), new Rectangle(wa.Left + 32, wa.Top + 32, image.Width, image.Height));
            var state = (ScreenshotState)form.Tag;
            state.SuppressHistory = true;
            state.Incognito = true;
            state.AutoMenu = false;
            state.Scale = Math.Min(1f, Math.Min((wa.Width - 64f) / image.Width, (wa.Height - 64f) / image.Height));
            form.Show();
            return form;
        }
    }
}
