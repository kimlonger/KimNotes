using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using DrawingPoint = System.Drawing.Point;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow : ICloudNote
    {
        private NoteWorkspace workspace;
        private DispatcherTimer autoSave;
        private NoteEdgeTab edgeTab;
        private string path;
        private bool dirty;
        private bool replacingDocument;
        private bool discarding;
        private bool composing;
        private int themeId;
        private bool independentTheme;
        private DateTime saveAfterUtc;
        private DateTime savedStatusUntilUtc;
        private DateTime? inactiveSinceUtc;

        public string FilePath => path;
        public bool IsImeComposing => composing;
        internal NoteEdgeTab DockedTab => edgeTab;

        internal void Connect(NoteWorkspace owner, string filePath, string text)
        {
            workspace = owner;
            MoreButton.IsEnabled = TranslateButton.IsEnabled = CaptureButton.IsEnabled =
                TodoButton.IsEnabled = DesktopToolsButton.IsEnabled = true;
            ShowInTaskbar = InitConfig.ShowTaskbarIcons;
            path = filePath;
            if (path != null)
            {
                if (!File.Exists(path)) throw new FileNotFoundException("找不到这张便签。", path);
                var metadata = LoadSavedDocument();
                NotePlacement.Restore(this, metadata);
                Topmost = metadata.Pinned == true;
            }
            else
            {
                ApplyNoteTheme(NoteTheme.Current().Id);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Editor.Document.Blocks.Clear();
                    Editor.Document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
                    MarkEdited();
                }
            }

            Editor.TextChanged += (sender, args) => { if (!replacingDocument) MarkEdited(); };
            TextCompositionManager.AddPreviewTextInputStartHandler(Editor, (sender, args) => composing = true);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(Editor, (sender, args) => composing = true);
            TextCompositionManager.AddPreviewTextInputHandler(Editor, (sender, args) =>
            {
                if (!composing) return;
                composing = false;
                if (dirty) saveAfterUtc = DateTime.UtcNow.AddMilliseconds(700);
            });
            Editor.LostKeyboardFocus += (sender, args) => composing = false;
            DataObject.AddPastingHandler(Editor, PasteImage);
            ConnectMorePanel();
            ConnectToolsPopup();
            MouseEnter += (sender, args) => RevealChrome();
            Activated += (sender, args) => RevealChrome();
            AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler(OpenNoteLink));
            Closing += SaveBeforeClosing;
            Closed += (sender, args) =>
            {
                autoSave.Stop();
                CloseEdgeTab();
                DismissImageResize();
            };
            autoSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            autoSave.Tick += (sender, args) =>
            {
                UpdateChromeVisibility();
                DateTime now = DateTime.UtcNow;
                if (dirty && !composing && !IsMovingNote && now >= saveAfterUtc) SaveCurrentNoteSafe();
                if (SaveStatus.Text == "已保存" && now >= savedStatusUntilUtc) SaveStatus.Text = "";
            };
            autoSave.Start();
        }

        private void MarkEdited()
        {
            dirty = true;
            SaveStatus.Text = Storage.Incognito ? "仅本次保留" : "未保存";
            saveAfterUtc = DateTime.UtcNow.AddSeconds(2);
        }

        private void SaveBeforeClosing(object sender, CancelEventArgs e)
        {
            if (discarding || Storage.Incognito) return;
            if (!SaveCurrentNoteSafe()) { e.Cancel = true; return; }
            try
            {
                if (path != null) workspace.Repository.SaveMetadata(path, NotePlacement.Capture(this, themeId));
            }
            catch (Exception error)
            {
                Program.LogError(error);
                if (path != null) SaveStatus.Text = "窗口位置未保存";
            }
        }

        public bool SaveCurrentNoteSafe()
        {
            if (workspace == null || Storage.Incognito || !dirty) return true;
            if (composing) return false;
            string text = NoteDocumentRtf.PreviewText(Editor.Document);
            if (path == null && string.IsNullOrWhiteSpace(text) && !ContainsEmbeddedContent())
            {
                dirty = false;
                SaveStatus.Text = "";
                return true;
            }
            string target = path ?? workspace.Repository.NewPath();
            try
            {
                workspace.Repository.Save(target, Editor.Document, text, NotePlacement.Capture(this, themeId));
                path = target;
                independentTheme = true;
                dirty = false;
                savedStatusUntilUtc = DateTime.UtcNow.AddSeconds(4);
                SaveStatus.Text = "已保存";
                workspace.Remember();
                return true;
            }
            catch (Exception error)
            {
                if (File.Exists(target)) path = target;
                saveAfterUtc = DateTime.UtcNow.AddSeconds(5);
                Program.LogError(error);
                SaveStatus.Text = "保存失败";
                return false;
            }
        }

        private bool ContainsEmbeddedContent()
        {
            for (var cursor = Editor.Document.ContentStart; cursor != null; cursor = cursor.GetNextContextPosition(LogicalDirection.Forward))
                if (cursor.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement) return true;
            return false;
        }

        public void ReloadCloud()
        {
            if (workspace == null || path == null || !File.Exists(path)) return;
            LoadSavedDocument();
        }

        private NoteMetadata LoadSavedDocument()
        {
            var document = workspace.Repository.Load(path);
            var metadata = Storage.Metadata(path);
            replacingDocument = true;
            try
            {
                document.PagePadding = Editor.Document.PagePadding;
                Editor.Document = document;
                AttachDocumentImageCommands();
                ApplyNoteTheme(metadata.ThemeId);
                independentTheme = true;
                dirty = false;
            }
            finally { replacingDocument = false; }
            return metadata;
        }

        public void DiscardAndClose()
        {
            discarding = true;
            Close();
        }

        internal void Relocate(string oldDirectory, string newDirectory)
        {
            if (path != null && string.Equals(Path.GetDirectoryName(path), oldDirectory, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(newDirectory, Path.GetFileName(path));
        }

        internal void UpdateDefaultTheme()
        {
            if (!independentTheme) ApplyNoteTheme(NoteTheme.Current().Id);
        }

        private void ApplyNoteTheme(int id)
        {
            var theme = NoteTheme.All.FirstOrDefault(item => item.Id == id) ?? NoteTheme.Current();
            themeId = theme.Id;
            var bars = NoteThemeBrushes.NoteBars(theme);
            var logo = NoteThemeBrushes.LogoColors(theme);
            var tools = NoteThemeBrushes.ToolStates(theme);
            Resources["NoteBodyBrush"] = NoteThemeBrushes.Body(theme);
            Resources["NoteLogoTintBrush"] = logo.Wool;
            Resources["NoteLogoOutlineBrush"] = logo.Outline;
            Resources["NoteTextBrush"] = NoteThemeBrushes.Text(theme);
            Resources["NoteTitleBrush"] = bars.Title;
            Resources["NoteTitleRuleBrush"] = bars.TitleRule;
            Resources["NoteToolsBrush"] = bars.Tools;
            Resources["NoteToolsRuleBrush"] = bars.ToolsRule;
            Resources["NoteTitleTextBrush"] = NoteThemeBrushes.ChromeText(theme);
            Resources["NoteIconBrush"] = NoteThemeBrushes.Icon(theme);
            Resources["NoteIconHoverBrush"] = NoteThemeBrushes.Accent(theme);
            Resources["NoteIconDisabledBrush"] = tools.Disabled;
            Resources["NoteToolHoverBrush"] = tools.Hover;
            Resources["NoteToolSelectedBrush"] = tools.Selected;
            Resources["NoteToolPressedBrush"] = tools.Pressed;
            Resources["NoteToolFocusBrush"] = tools.Focus;
            Resources["NoteDividerBrush"] = NoteThemeBrushes.Divider(theme);
            ApplyToolbarToolTips(theme);
            edgeTab?.ApplyTheme(theme);
            RefreshMorePanel();
            if (IsActive) ApplyCardTheme();
        }

        private void ApplyToolbarToolTips(NoteTheme theme)
        {
            foreach (var button in ToolbarButtons.Children.OfType<ButtonBase>())
            {
                if (!(button.ToolTip is RoundedToolTip tip))
                {
                    tip = new RoundedToolTip { Content = button.ToolTip };
                    button.ToolTip = tip;
                }
                tip.Background = NoteThemeBrushes.Surface(theme);
                tip.BorderBrush = NoteThemeBrushes.Divider(theme);
                tip.Foreground = NoteThemeBrushes.ChromeText(theme);
            }
        }

        internal void InsertImage(BitmapSource source)
        {
            if (source == null) return;
            Reveal();
            InsertBitmap(source, Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
        }

        internal void Reveal()
        {
            if (edgeTab != null)
            {
                edgeTab.MoveNoteBeside(this);
                RevealChrome();
            }
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            CloseEdgeTab();
            Activate();
            Editor.Focus();
        }

        private void CloseEdgeTab()
        {
            var tab = edgeTab;
            edgeTab = null;
            tab?.Close();
        }

        internal bool DockAtEdge(DrawingPoint cursor, DrawingPoint travel)
        {
            if (workspace == null || !IsVisible || edgeTab != null || WindowState != WindowState.Normal) return false;
            var theme = NoteTheme.All.FirstOrDefault(item => item.Id == themeId) ?? NoteTheme.Current();
            string body = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
            string heading = NoteEdgeTab.FirstLineCaption(body);
            edgeTab = workspace.CreateEdgeTab(this, cursor, travel, heading, theme);
            if (edgeTab == null) return false;
            edgeTab.Show();
            Hide();
            return true;
        }

        private void UpdateChromeVisibility()
        {
            if (workspace?.IsCaptureActive?.Invoke() == true) return;
            if (!IsVisible || IsActive || IsMouseOver || IsMovingNote || IsMoreOpen || IsToolsPopupOpen)
            {
                RevealChrome();
                return;
            }
            var now = DateTime.UtcNow;
            if (!inactiveSinceUtc.HasValue) inactiveSinceUtc = now;
            if (now - inactiveSinceUtc.Value >= TimeSpan.FromSeconds(5)) SetChromeVisible(false);
        }

        private void RevealChrome()
        {
            inactiveSinceUtc = null;
            SetChromeVisible(true);
        }

        private void SetChromeVisible(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Hidden;
            var titleState = IsMoreOpen ? Visibility.Hidden : state;
            if (TitleChrome.Visibility != titleState) TitleChrome.Visibility = titleState;
            if (ToolsChrome.Visibility != state) ToolsChrome.Visibility = state;
        }
    }
}
