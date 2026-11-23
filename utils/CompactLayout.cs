using System;
using System.Drawing;

namespace KimNotes.utils
{
    internal static class CompactLayout
    {
        internal const int Width = 336;
        internal const int Height = 448;
        internal const int NoteToolSize = 30;
        internal const int NoteFramePadding = 5;
        internal const int EditorWidth = 320;
        internal const int EditorHeight = 420;
        internal const int ReminderHeight = 280;

        internal static int NoteToolbarMargin(float scale) => Math.Max(4, (int)Math.Round(8 * scale));

        internal static int NoteToolbarGap(float scale) => Math.Max(2, (int)Math.Round(5 * scale));

        internal static Size DefaultNoteSize(float scale, Size workArea)
        {
            const int toolbarButtons = 8;
            int width = toolbarButtons * (int)Math.Round(NoteToolSize * scale)
                + (toolbarButtons - 1) * NoteToolbarGap(scale) + 2 * NoteToolbarMargin(scale)
                + 2 * (int)Math.Round(NoteFramePadding * scale);
            return FitNoteSize(width, workArea);
        }

        internal static Size FitNoteSize(int width, Size workArea)
        {
            int height = (int)Math.Round(width * (double)Height / Width);
            return new Size(Math.Min(width, workArea.Width), Math.Min(height, workArea.Height));
        }
    }
}
