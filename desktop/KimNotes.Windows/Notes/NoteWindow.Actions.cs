using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using KimNotes.Windows.Runtime;
using KimNotes.Windows.Todos;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow
    {
        private void CaptureClick(object sender, RoutedEventArgs e)
        {
            if (workspace == null) return;
            ApplyCardTheme();
            workspace.Capture();
        }

        private async void TranslateClick(object sender, RoutedEventArgs e)
        {
            var source = Editor.Selection.Text;
            if (string.IsNullOrWhiteSpace(source) || !source.Any(char.IsLetter))
            {
                SaveStatus.Text = "请选中文字";
                return;
            }
            bool chinese = source.Any(c => c >= 0x3400 && c <= 0x9fff);
            var end = Editor.Selection.End;
            TranslateButton.IsEnabled = false;
            SaveStatus.Text = "翻译中";
            try
            {
                string translation = await Task.Run(() => new Translator().Translate(source, "auto", chinese ? "en" : "zh"));
                if (!IsLoaded) return;
                if (string.IsNullOrWhiteSpace(translation)) { SaveStatus.Text = "翻译失败"; return; }
                if (Editor.Selection.Text != source || Editor.Selection.End.CompareTo(end) != 0)
                { SaveStatus.Text = "选区已变化"; return; }
                InsertTranslation(end, translation);
            }
            catch (Exception error) { Program.LogError(error); SaveStatus.Text = "翻译失败"; }
            finally { TranslateButton.IsEnabled = true; }
        }

        private void InsertTranslation(TextPointer end, string translation)
        {
            var insertion = end.GetInsertionPosition(LogicalDirection.Backward);
            var lines = translation.Trim().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Editor.BeginChange();
            try
            {
                foreach (var line in lines)
                {
                    insertion = insertion.InsertLineBreak().GetPositionAtOffset(0, LogicalDirection.Forward);
                    if (line.Length != 0) insertion.InsertTextInRun(line);
                }
                Editor.Selection.Select(insertion, insertion);
            }
            finally { Editor.EndChange(); }
        }

        private void TodoClick(object sender, RoutedEventArgs e)
        {
            if (workspace == null) return;
            ApplyCardTheme();
            string content = GetTodoSelection();
            if (content == null)
            {
                workspace.ShowTodos();
                return;
            }
            var editor = new TodoEditor(content);
            editor.Confirmed += (sender, args) =>
            {
                if (!discarding && !SaveCurrentNoteSafe())
                {
                    args.Cancel = true;
                    return;
                }
                var task = TodoStore.AddDetailed(editor.TodoText, editor.Due, editor.Repeat,
                    editor.Priority, null, false, discarding ? null : path, content.Trim());
                ApplyCardTheme();
                workspace.OnTodoCreated(task);
            };
            editor.ShowBeside(this);
        }

        private string GetTodoSelection()
        {
            if (Editor.Selection.IsEmpty) return null;
            string text = Editor.Selection.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static void OpenNoteLink(object sender, RequestNavigateEventArgs e)
        {
            if (!e.Uri.IsAbsoluteUri || (e.Uri.Scheme != "https" && e.Uri.Scheme != "http")) return;
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { Program.LogError(error); }
        }

        private void AttachDocumentImageCommands()
        {
            DismissImageResize();
            for (var cursor = Editor.Document.ContentStart; cursor != null; cursor = cursor.GetNextContextPosition(LogicalDirection.Forward))
                if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement &&
                    cursor.GetAdjacentElement(LogicalDirection.Forward) is Image image)
                    AddImageCommands(image);
        }

        private void AddImageCommands(Image image)
        {
            if (!(image.Source is BitmapSource)) return;
            var menu = imageMenus.GetValue(image, CreateEditorMenu);
            // Undo can restore a serialized menu without any of its runtime click handlers.
            if (!ReferenceEquals(image.ContextMenu, menu)) image.ContextMenu = menu;
        }

        private readonly ConditionalWeakTable<Image, ContextMenu> imageMenus = new ConditionalWeakTable<Image, ContextMenu>();

        private static readonly (string Label, string Shortcut, RoutedCommand Command)[] EditorMenuActions =
        {
            ("复制", "Ctrl+C", ApplicationCommands.Copy),
            ("粘贴", "Ctrl+V", ApplicationCommands.Paste),
            ("剪切", "Ctrl+X", ApplicationCommands.Cut),
            ("删除", "Delete", EditingCommands.Delete)
        };

        private ContextMenu CreateEditorMenu(Image image = null)
        {
            var menu = DesktopTrayMenu.Create(EditorMenuActions.Select(action =>
                (action.Label, (Action)(() => RunEditorMenuCommand(action.Command, image)))).ToArray());
            AutomationProperties.SetName(menu, image == null ? "正文菜单" : "图片菜单");
            for (int i = 0; i < EditorMenuActions.Length; i++)
            {
                var item = (MenuItem)menu.Items[i];
                var action = EditorMenuActions[i];
                var header = new DockPanel();
                var shortcut = new TextBlock
                {
                    Text = action.Shortcut, FontSize = 11, Margin = new Thickness(15, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                };
                shortcut.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
                DockPanel.SetDock(shortcut, Dock.Right);
                header.Children.Add(shortcut);
                header.Children.Add(new TextBlock { Text = action.Label, VerticalAlignment = VerticalAlignment.Center });
                item.Header = header;
                item.MinHeight = 23;
                item.InputGestureText = action.Shortcut;
                AutomationProperties.SetName(item, action.Label);
                AutomationProperties.SetAcceleratorKey(item, action.Shortcut);
            }
            menu.Opened += (sender, args) => RefreshEditorMenu(menu);
            return menu;
        }

        private void RefreshEditorMenu(ContextMenu menu)
        {
            if (workspace != null) ApplyCardTheme();
            for (int i = 0; i < EditorMenuActions.Length; i++)
            {
                var item = (MenuItem)menu.Items[i];
                try { item.IsEnabled = CanRunEditorMenuCommand(EditorMenuActions[i].Command); }
                catch (ExternalException) { item.IsEnabled = false; }
            }
        }

        private bool CanRunEditorMenuCommand(RoutedCommand command) =>
            (command == ApplicationCommands.Paste || !Editor.Selection.IsEmpty) && command.CanExecute(null, Editor);

        private void RunEditorMenuCommand(RoutedCommand command, Image image)
        {
            if (image != null && (ImageContainer(image) == null || (image != selectedImage && !SelectImage(image)))) return;
            try
            {
                if (CanRunEditorMenuCommand(command)) command.Execute(null, Editor);
            }
            catch (ExternalException error) { ReportClipboardUnavailable(error); }
        }

        private void EditorImageClick(object sender, MouseButtonEventArgs args)
        {
            if (imageResize != null && args.OriginalSource is Visual source &&
                (source == imageResize || imageResize.IsAncestorOf(source))) return;
            if (args.OriginalSource is Image image && SelectImage(image)) args.Handled = true;
            else DismissImageResize();
        }

        private void PrepareImageMenu(object sender, MouseButtonEventArgs args)
        {
            FrameworkElement target = Editor;
            if (args.OriginalSource is Image image && SelectImage(image)) target = image;
            else if (Editor.Selection.IsEmpty || selectedImage != null)
            {
                var point = Editor.GetPositionFromPoint(args.GetPosition(Editor), true) ?? Editor.CaretPosition;
                DismissImageResize();
                Editor.Selection.Select(point, point);
            }
            PrepareEditorMenu(target, PlacementMode.MousePoint);
            args.Handled = true;
        }

        private ContextMenu PrepareEditorMenu(FrameworkElement target, PlacementMode placement)
        {
            var menu = target.ContextMenu;
            menu.PlacementTarget = target;
            menu.Placement = placement;
            menu.PlacementRectangle = target == Editor && placement == PlacementMode.Bottom
                ? Editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward) : Rect.Empty;
            return menu;
        }

        private TextElement ImageContainer(Image image)
        {
            if (image?.Parent is TextElement element && (element is InlineUIContainer || element is BlockUIContainer) &&
                element.ElementStart.IsInSameDocument(Editor.Document.ContentStart)) return element;
            return null;
        }

        private bool SelectImage(Image image)
        {
            var container = ImageContainer(image);
            if (container == null || !(image.Source is BitmapSource)) return false;
            AddImageCommands(image);
            Editor.Focus();
            DismissImageResize();
            Editor.Selection.Select(container.ElementStart, container.ElementEnd);
            selectedImage = image;
            imageResize = NoteImageAdorner.ShowOn(image, width => ResizeImageTo(image, width));
            return true;
        }

        private void DeleteSelectedImage(object sender, KeyEventArgs args)
        {
            if (selectedImage != null && args.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                args.Handled = DeleteImage(selectedImage);
                return;
            }
            if (args.Key != Key.Apps && (args.Key != Key.F10 || Keyboard.Modifiers != ModifierKeys.Shift)) return;
            var target = ImageContainer(selectedImage) == null ? (FrameworkElement)Editor : selectedImage;
            PrepareEditorMenu(target, PlacementMode.Bottom).IsOpen = true;
            args.Handled = true;
        }

        private bool DeleteImage(Image image)
        {
            var container = ImageContainer(image);
            if (container == null) return false;
            if (selectedImage == image) DismissImageResize();
            new TextRange(container.ElementStart, container.ElementEnd).Text = "";
            MarkEdited();
            return true;
        }

        private void ExecuteImageClipboardCommand(object sender, ExecutedRoutedEventArgs args)
        {
            if (args.Command == ApplicationCommands.Paste)
            {
                try { args.Handled = !Editor.IsReadOnly && TryInsertImageFiles(Clipboard.GetDataObject()); }
                catch (ExternalException error)
                {
                    args.Handled = true;
                    ReportClipboardUnavailable(error);
                }
                return;
            }
            if (args.Command != ApplicationCommands.Copy && args.Command != ApplicationCommands.Cut && args.Command != EditingCommands.Delete) return;
            var image = selectedImage;
            if (ImageContainer(image) == null) return;
            args.Handled = true;
            if (args.Command == EditingCommands.Delete) DeleteImage(image);
            else if (args.Command == ApplicationCommands.Cut) CutImage(image);
            else CopyImage(image);
        }

        private void CanExecuteImageFilePaste(object sender, CanExecuteRoutedEventArgs args)
        {
            if (args.Command != ApplicationCommands.Paste || Editor.IsReadOnly) return;
            try
            {
                // RichTextBox does not recognize Explorer's clipboard file list.
                if (!HasImageFiles(Clipboard.GetDataObject())) return;
                args.CanExecute = true;
                args.Handled = true;
            }
            catch (ExternalException) { }
        }

        private bool CutImage(Image image) => CopyImage(image) && DeleteImage(image);

        private bool CopyImage(Image image)
        {
            if (ImageContainer(image) == null || !(image.Source is BitmapSource bitmap)) return false;
            try { Clipboard.SetImage(bitmap); return true; }
            catch (ExternalException error)
            {
                ReportClipboardUnavailable(error);
                return false;
            }
        }

        private void ReportClipboardUnavailable(ExternalException error)
        {
            Program.LogError(error);
            SaveStatus.Text = "剪贴板忙，请重试";
        }

        private void ResizeImage(Image image, double factor)
        {
            double width = double.IsNaN(image.Width) ? image.ActualWidth : image.Width;
            ResizeImageTo(image, width * factor);
        }

        private void ResizeImageTo(Image image, double width)
        {
            double clamped = Math.Max(24, Math.Min(Editor.ActualWidth - 12, width));
            if (image.Width == clamped && double.IsNaN(image.Height)) return;
            image.Width = clamped;
            image.Height = double.NaN;
            MarkEdited();
        }

        private Image selectedImage;
        private NoteImageAdorner imageResize;

        private void DismissImageResize()
        {
            imageResize?.Dismiss();
            imageResize = null;
            selectedImage = null;
        }

        private void PasteImage(object sender, DataObjectPastingEventArgs e)
        {
            BitmapSource bitmap;
            try
            {
                if (TryInsertImageFiles(e.DataObject))
                {
                    e.CancelCommand();
                    return;
                }
                if (e.DataObject.GetDataPresent(DataFormats.Rtf)) return;
                bitmap = e.DataObject.GetData(DataFormats.Bitmap) as BitmapSource;
            }
            catch (ExternalException error)
            {
                e.CancelCommand();
                ReportClipboardUnavailable(error);
                return;
            }
            if (bitmap == null) return;
            e.CancelCommand();
            PasteBitmaps(new[] { bitmap }, null);
        }

        private void PasteBitmaps(IReadOnlyList<BitmapSource> bitmaps, TextPointer dropPosition)
        {
            if (bitmaps.Count == 0) return;
            var selection = Editor.Selection;
            bool replace = dropPosition == null && !selection.IsEmpty;
            var start = replace ? selection.Start.GetPositionAtOffset(0, LogicalDirection.Backward) : null;
            var end = (dropPosition ?? selection.End).GetPositionAtOffset(0, LogicalDirection.Backward);
            var replacedImage = replace ? selectedImage : null;
            Editor.BeginChange();
            try
            {
                var placement = end.GetInsertionPosition(LogicalDirection.Forward);
                if (placement == null) return;
                // Leave the original range alone until the new bitmaps have been inserted.
                var added = InsertBitmap(bitmaps[0], placement);
                for (int i = 1; i < bitmaps.Count; i++)
                    InsertBitmap(bitmaps[i], Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
                if (replacedImage != null) DeleteImage(replacedImage);
                else if (replace)
                {
                    var replacementStart = added.ElementStart;
                    new TextRange(start, end.CompareTo(replacementStart) < 0 ? end : replacementStart).Text = "";
                }
            }
            finally { Editor.EndChange(); }
        }

        private InlineUIContainer InsertBitmap(BitmapSource source, TextPointer placement)
        {
            var image = new Image
            {
                Source = source, Width = Math.Min(Math.Max(80, Editor.ActualWidth - 16), source.Width),
                Stretch = Stretch.Uniform
            };
            AddImageCommands(image);
            var inline = new InlineUIContainer(image, placement);
            Editor.CaretPosition = inline.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
            MarkEdited();
            return inline;
        }

        private void UpdateImageDragEffect(object sender, DragEventArgs e) => SetImageDropEffect(e);

        private bool SetImageDropEffect(DragEventArgs e)
        {
            if (!HasImageFiles(e.Data)) return false;
            e.Effects = !Editor.IsReadOnly && (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return e.Effects == DragDropEffects.Copy;
        }

        private void DropImages(object sender, DragEventArgs e)
        {
            if (!SetImageDropEffect(e)) return;
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor), true) ?? Editor.CaretPosition;
            TryInsertImageFiles(e.Data, position);
            Editor.Focus();
        }
    }
}
