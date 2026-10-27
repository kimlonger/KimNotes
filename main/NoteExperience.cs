using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using KimNotes.utils;

namespace KimNotes
{
    public partial class note
    {
        private Label saveStatus;
        private DateTime lastEditUtc, dirtySinceUtc, savedStatusUntil;
        private bool experienceReady, restoringBounds;
        private bool interactingWithBounds;

        private void SetupExperience()
        {
            saveStatus = new Label { AutoSize = true, Font = captionTitle.Font, ForeColor = currentTheme.ChromeText, BackColor = currentTheme.Chrome };
            chromePanel.Controls.Add(saveStatus);
            HookInteraction(saveStatus);
            chromePanel.Resize += (s, e) => PositionSaveStatus();
            Shown += (s, e) =>
            {
                restoringBounds = true;
                try { RestoreNoteLayout(); }
                finally { restoringBounds = false; experienceReady = true; }
                DesktopSession.Remember();
            };
            Move += (s, e) => MarkLayoutDirty();
            ResizeBegin += (s, e) => interactingWithBounds = true;
            ResizeEnd += (s, e) => { interactingWithBounds = false; MarkLayoutDirty(); };
            SizeChanged += (s, e) => MarkLayoutDirty();
            FormClosed += (s, e) => DesktopSession.Remember(this);
            NoteImages.Attach(richTextBox1, this, () => { MarkEdited(); SaveCurrentNoteSafe(); });
        }

        private void PositionSaveStatus()
        {
            if (saveStatus == null) return;
            saveStatus.Location = new Point(captionTitle.Right + (int)(10 * DeviceDpi / 96f), (chromePanel.Height - saveStatus.Height) / 2);
            saveStatus.Visible = !compact && !string.IsNullOrEmpty(saveStatus.Text) && saveStatus.Right + 5 < moreBtn.Left;
        }

        private void SetSaveStatus(string text)
        {
            if (saveStatus == null) return;
            saveStatus.Text = trace ? "仅本次保留" : text;
            saveStatus.BackColor = currentTheme.Chrome;
            saveStatus.ForeColor = saveErrorShown ? Color.FromArgb(186, 67, 59) : currentTheme.ChromeText;
            PositionSaveStatus();
        }

        private void MarkEdited()
        {
            var now = DateTime.UtcNow;
            if (!hasUnsavedChanges) dirtySinceUtc = now;
            lastEditUtc = now;
            hasUnsavedChanges = true;
            SetSaveStatus("未保存");
        }

        private void MarkLayoutDirty()
        {
            if (!experienceReady || restoringBounds || WindowState != FormWindowState.Normal) return;
            metadataDirty = true;
            lastEditUtc = DateTime.UtcNow;
        }

        private void AutoSaveTick(DateTime now)
        {
            if (interactingWithBounds) return;
            if (trace) { if (hasUnsavedChanges) SetSaveStatus("仅本次保留"); return; }
            if ((hasUnsavedChanges || richTextBox1.Modified || metadataDirty) &&
                ((now - lastEditUtc).TotalSeconds >= 1 || (hasUnsavedChanges && (now - dirtySinceUtc).TotalSeconds >= 5)))
                SaveCurrentNoteSafe();
            else if (!hasUnsavedChanges && !metadataDirty && !saveErrorShown && now >= savedStatusUntil)
                SetSaveStatus("");
        }

        private NoteMetadata CurrentMetadata() => new NoteMetadata
        {
            ThemeId = currentTheme.Id,
            Bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds,
            Monitor = Screen.FromControl(this).DeviceName,
            Dpi = DeviceDpi,
            Pinned = TopMost
        };

        private void RestoreNoteLayout()
        {
            if (FilePath == null || trace) return;
            var metadata = Storage.Metadata(FilePath);
            if (metadata.Pinned.HasValue) { TopMost = metadata.Pinned.Value; button7.Selected = TopMost; }
            if (!metadata.Bounds.HasValue) return;
            var saved = metadata.Bounds.Value;
            if (saved.Width < 100 || saved.Height < 100) return;
            var monitor = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == metadata.Monitor) ?? Screen.FromRectangle(saved);
            var wa = monitor.WorkingArea;
            Location = new Point(wa.Left + 16, wa.Top + 16);
            // Bounds and saved DPI both use WinForms' current coordinate system.
            // Mixing a native monitor query into this calculation can shrink the
            // restored window during a monitor transition.
            float scale = DeviceDpi / (float)Math.Max(96, metadata.Dpi);
            int width = Math.Min(wa.Width, Math.Max(MinimumSize.Width, (int)Math.Round(saved.Width * scale)));
            int height = Math.Min(wa.Height, Math.Max(MinimumSize.Height, (int)Math.Round(saved.Height * scale)));
            Bounds = new Rectangle(Math.Max(wa.Left, Math.Min(saved.X, wa.Right - width)),
                Math.Max(wa.Top, Math.Min(saved.Y, wa.Bottom - height)), width, height);
            QueueDpiLayout(DeviceDpi);
        }
    }
}
