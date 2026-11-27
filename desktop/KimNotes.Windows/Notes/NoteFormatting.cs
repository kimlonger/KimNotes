using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace KimNotes.Windows.Notes
{
    internal static class NoteFormatting
    {
        internal static void ToggleStrikethrough(RichTextBox editor)
        {
            var selection = editor.Selection;
            if (selection.IsEmpty)
            {
                SetStrikethrough(selection, !HasStrikethrough(Decorations(selection)));
                return;
            }

            var textRuns = new List<TextRange>();
            bool allStruck = true;
            foreach (var run in SelectedTextRuns(selection))
            {
                textRuns.Add(run);
                allStruck &= HasStrikethrough(Decorations(run));
            }

            // Make a single undo unit for a selection containing independently formatted runs.
            editor.BeginChange();
            try
            {
                foreach (var run in textRuns)
                    SetStrikethrough(run, !allStruck);
            }
            finally
            {
                editor.EndChange();
            }
        }

        internal static bool IsStrikethrough(TextSelection selection)
        {
            object value = selection.GetPropertyValue(Inline.TextDecorationsProperty);
            if (value != DependencyProperty.UnsetValue) return HasStrikethrough(value as TextDecorationCollection);

            // Runs can all be struck even when another decoration, such as underlining, differs.
            bool foundText = false;
            foreach (var run in SelectedTextRuns(selection))
            {
                if (!HasStrikethrough(Decorations(run))) return false;
                foundText = true;
            }
            return foundText;
        }

        private static IEnumerable<TextRange> SelectedTextRuns(TextSelection selection)
        {
            TextPointer end = selection.End;
            for (TextPointer start = selection.Start; start != null && start.CompareTo(end) < 0;)
            {
                var next = start.GetNextContextPosition(LogicalDirection.Forward);
                if (next == null || next.CompareTo(end) > 0) next = end;
                if (start.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                    yield return new TextRange(start, next);
                start = next;
            }
        }

        private static TextDecorationCollection Decorations(TextRange range)
        {
            return range.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        }

        private static bool HasStrikethrough(TextDecorationCollection decorations)
        {
            if (decorations == null) return false;
            foreach (var decoration in decorations)
                if (decoration.Location == TextDecorationLocation.Strikethrough) return true;
            return false;
        }

        private static void SetStrikethrough(TextRange range, bool enabled)
        {
            var updated = new TextDecorationCollection();
            var present = Decorations(range);
            if (present != null)
                foreach (var decoration in present)
                    if (decoration.Location != TextDecorationLocation.Strikethrough)
                        updated.Add(decoration);
            if (enabled) updated.Add(TextDecorations.Strikethrough[0]);
            updated.Freeze();
            range.ApplyPropertyValue(Inline.TextDecorationsProperty, updated);
        }
    }
}
