using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KimNotes.Windows.Notes
{
    internal static class NoteDocumentRtf
    {
        internal static FlowDocument Read(Stream input)
        {
            input.Position = FindHeader(input);
            var document = new FlowDocument();
            var content = new TextRange(document.ContentStart, document.ContentEnd);
            if (input.Position == 0) content.Load(input, DataFormats.Rtf);
            else
            {
                // WPF rewinds seekable streams, so omit any leading bytes from its copy.
                using var normalized = new MemoryStream();
                input.CopyTo(normalized);
                normalized.Position = 0;
                content.Load(normalized, DataFormats.Rtf);
            }
            NoteDocumentColors.RestoreThemeInheritance(document);
            return document;
        }

        private static long FindHeader(Stream input)
        {
            int value = input.ReadByte();
            if (value == 0xef && input.ReadByte() == 0xbb && input.ReadByte() == 0xbf)
                value = input.ReadByte();
            while (value == ' ' || (value >= '\t' && value <= '\r')) value = input.ReadByte();

            long offset = input.Position - 1;
            // The WPF loader can otherwise treat invalid note bytes as literal text.
            foreach (char expected in "{\\rtf")
            {
                if (value != expected) throw new IOException("文件缺少有效的 RTF 标记。");
                value = input.ReadByte();
            }
            return offset;
        }

        internal static string PreviewText(FlowDocument document)
        {
            var start = document.ContentStart;
            StringBuilder text = null;
            for (var cursor = start; cursor != null; cursor = cursor.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (cursor.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.EmbeddedElement ||
                    !(cursor.GetAdjacentElement(LogicalDirection.Forward) is Image)) continue;
                if (text == null) text = new StringBuilder();
                text.Append(new TextRange(start, cursor).Text).Append("[图片]");
                start = cursor.GetNextContextPosition(LogicalDirection.Forward);
            }
            var tail = new TextRange(start, document.ContentEnd).Text;
            return text == null ? tail : text.Append(tail).ToString();
        }

        internal static void Write(FlowDocument document, Stream output)
        {
            // Persist automatic text in a neutral ink that stays legible on older clients.
            var foreground = FlowDocument.ForegroundProperty;
            bool inherited = document.ReadLocalValue(foreground) == DependencyProperty.UnsetValue;
            if (inherited) document.SetValue(foreground, Brushes.Black);
            try
            {
                var content = new TextRange(document.ContentStart, document.ContentEnd);
                content.Save(output, DataFormats.Rtf);
            }
            finally
            {
                if (inherited) document.ClearValue(foreground);
            }
        }
    }
}
