using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Notes
{
    internal static class NoteDocumentColors
    {
        private static readonly HashSet<Color> AutomaticInks = BuildAutomaticInks();

        internal static void RestoreThemeInheritance(FlowDocument document)
        {
            ClearAutomaticInk(document);
            for (var cursor = document.ContentStart; cursor != null; cursor = cursor.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart &&
                    cursor.GetAdjacentElement(LogicalDirection.Forward) is TextElement element)
                    ClearAutomaticInk(element);
            }
        }

        private static void ClearAutomaticInk(DependencyObject element)
        {
            // RTF stores the formerly inherited theme text as an explicit color on import.
            if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush &&
                brush.Opacity == 1 && AutomaticInks.Contains(brush.Color))
                element.ClearValue(TextElement.ForegroundProperty);
        }

        private static HashSet<Color> BuildAutomaticInks()
        {
            var colors = new HashSet<Color>
            {
                Colors.Black,
                Color.FromRgb(0x1D, 0x1D, 0x1F), // Former Apple light and dot paper themes.
                Color.FromRgb(0x25, 0x2F, 0x3A), // Former grid paper theme.
                Color.FromRgb(0x4A, 0x3D, 0x1E)  // Former ruled paper theme.
            };
            foreach (var theme in NoteTheme.All)
            {
                colors.Add(Color.FromRgb(theme.Text.R, theme.Text.G, theme.Text.B));
                if (NoteThemeBrushes.Text(theme) is SolidColorBrush ink) colors.Add(ink.Color);
            }
            return colors;
        }
    }
}
