using System;
using System.Windows;
using System.Windows.Input;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow
    {
        private bool IsMoreOpen => MorePanelHost.Visibility == Visibility.Visible;
        internal void ApplyCardTheme() => DesktopCardTheme.Apply(themeId);

        private void ConnectMorePanel()
        {
            Activated += (sender, args) => ApplyCardTheme();
            PreviewMouseDown += (sender, args) =>
            {
                if (IsMoreOpen && !MorePanelHost.IsMouseOver) HideMorePanel();
            };
            PreviewKeyDown += (sender, args) =>
            {
                if (!IsMoreOpen || args.Key != Key.Escape) return;
                args.Handled = true;
                HideMorePanel();
                Editor.Focus();
            };
            Deactivated += (sender, args) => HideMorePanel();
            IsVisibleChanged += (sender, args) => { if (!IsVisible) HideMorePanel(); };
            Closed += (sender, args) => HideMorePanel();
        }

        private void MoreClick(object sender, RoutedEventArgs e)
        {
            if (workspace == null) return;
            if (IsMoreOpen) { HideMorePanel(); return; }

            if (MorePanelHost.Child == null)
                MorePanelHost.Child = new NoteQuickPanel(SelectTheme,
                    () => RunMoreAction(workspace.ShowCatalog),
                    () => RunMoreAction(workspace.ShowSettings));

            MorePanelHost.Visibility = Visibility.Visible;
            SetChromeVisible(true);
            RefreshMorePanel();
            if (MoreButton.IsKeyboardFocused) MorePanelHost.Focus();
        }

        private void RefreshMorePanel()
        {
            if (IsMoreOpen && MorePanelHost.Child is NoteQuickPanel panel)
                panel.ApplyTheme(themeId);
        }

        private void HideMorePanel()
        {
            if (!IsMoreOpen) return;
            MorePanelHost.Visibility = Visibility.Collapsed;
            SetChromeVisible(ToolsChrome.Visibility == Visibility.Visible);
        }

        private void RunMoreAction(Action action)
        {
            HideMorePanel();
            ApplyCardTheme();
            action();
        }

        private void SelectTheme(int id)
        {
            if (id == themeId) return;
            independentTheme = true;
            NoteDocumentColors.RestoreThemeInheritance(Editor.Document);
            ApplyNoteTheme(id);
            ApplyCardTheme();
            MarkEdited();
        }
    }
}
