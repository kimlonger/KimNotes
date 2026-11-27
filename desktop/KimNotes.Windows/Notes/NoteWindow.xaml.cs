using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using KimNotes.Windows.Presentation;
using Microsoft.Win32;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow : Window
    {
        public NoteWindow()
        {
            InitializeComponent();
            ApplyToolbarToolTips(NoteTheme.All[0]);
            Editor.ContextMenu = CreateEditorMenu();
            new NoteWindowChrome(this);
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, OpenSample));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, SaveCopy));
            Editor.SelectionChanged += (sender, args) =>
            {
                DismissImageResize();
                RefreshFormattingSelection();
            };
            Editor.TextChanged += (sender, args) => RefreshFormattingSelection();
            Editor.AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler((sender, args) =>
            {
                if (args.Command == EditingCommands.ToggleBold) RefreshFormattingSelection();
            }), true);
            Loaded += (sender, args) => Editor.Focus();
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void NewNoteClick(object sender, RoutedEventArgs e)
        {
            if (workspace != null) workspace.New();
            else new NoteWindow().Show();
        }

        private void TogglePinClick(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            if (workspace != null && path != null && !KimNotes.utils.Storage.Incognito)
                try
                {
                    workspace.Repository.SaveMetadata(path, NotePlacement.Capture(this, themeId));
                    KimNotes.utils.Storage.NotifyNotesChanged();
                }
                catch (IOException error) { Program.LogError(error); SaveStatus.Text = "置顶状态未保存"; }
        }

        private void ToggleStrikeClick(object sender, RoutedEventArgs e)
        {
            NoteFormatting.ToggleStrikethrough(Editor);
            Editor.Focus();
            RefreshFormattingSelection();
        }

        private void ToggleBoldClick(object sender, RoutedEventArgs e)
        {
            Editor.Focus();
            var command = EditingCommands.ToggleBold;
            if (command.CanExecute(null, Editor)) command.Execute(null, Editor);
            RefreshFormattingSelection();
        }

        private void ToggleBulletsClick(object sender, RoutedEventArgs e)
        {
            Editor.Focus();
            EditingCommands.ToggleBullets.Execute(null, Editor);
            RefreshFormattingSelection();
        }

        private void RefreshFormattingSelection()
        {
            var item = Editor.CaretPosition.Paragraph?.Parent as ListItem;
            var list = item?.Parent as List;
            BulletButton.IsChecked = list != null &&
                list.MarkerStyle >= TextMarkerStyle.Disc && list.MarkerStyle <= TextMarkerStyle.Box;
            BoldButton.IsChecked = Editor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight &&
                weight == FontWeights.Bold;
            StrikeButton.IsChecked = NoteFormatting.IsStrikethrough(Editor.Selection);
        }

        private void OpenSample(object sender, ExecutedRoutedEventArgs e)
        {
            if (workspace != null)
            {
                ApplyCardTheme();
                workspace.ShowCatalog();
                return;
            }
            var dialog = new OpenFileDialog
            {
                Title = "打开测试便签",
                Filter = "RTF 便签 (*.rtf)|*.rtf",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                using (var input = File.OpenRead(dialog.FileName))
                {
                    var document = NoteDocumentRtf.Read(input);
                    document.PagePadding = Editor.Document.PagePadding;
                    Editor.Document = document;
                }
                Editor.Focus();
            }
            catch (Exception error) when (IsFileError(error))
            {
                MessageBox.Show(this, "无法打开这份便签。请检查文件是否为有效的 RTF。",
                    "打开测试便签", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveCopy(object sender, ExecutedRoutedEventArgs e)
        {
            if (workspace != null) { SaveCurrentNoteSafe(); return; }
            var dialog = new SaveFileDialog
            {
                Title = "保存测试副本",
                Filter = "RTF 便签 (*.rtf)|*.rtf",
                DefaultExt = ".rtf",
                FileName = "便签副本.rtf",
                InitialDirectory = Path.GetTempPath(),
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                using (var content = new MemoryStream())
                {
                    NoteDocumentRtf.Write(Editor.Document, content);
                    content.Position = 0;
                    using (var file = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write))
                        content.CopyTo(file);
                }
            }
            catch (Exception error) when (IsFileError(error))
            {
                MessageBox.Show(this, "请另选可写入的位置或文件名。原文件会保留。",
                    "无法保存测试副本", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static bool IsFileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is FormatException;
        }
    }
}
