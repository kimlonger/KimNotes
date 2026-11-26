using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KimNotes.Windows.Notes
{
    internal partial class NoteWindow
    {
        private readonly DispatcherTimer toolsHover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        private readonly DispatcherTimer toolsLeave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        private NoteToolsPopup toolsPopup;
        private bool toolsOpenedByClick;
        private bool IsToolsPopupOpen => toolsPopup?.IsOpen == true;

        private void ConnectToolsPopup()
        {
            DesktopToolsButton.MouseEnter += (sender, args) =>
            {
                toolsLeave.Stop();
                if (workspace.CanOpenTools && !IsToolsPopupOpen) toolsHover.Start();
            };
            DesktopToolsButton.MouseLeave += (sender, args) =>
            {
                toolsHover.Stop();
                if (IsToolsPopupOpen && !toolsOpenedByClick) toolsLeave.Start();
            };
            toolsHover.Tick += (sender, args) =>
            {
                toolsHover.Stop();
                if (IsVisible && DesktopToolsButton.IsMouseOver && workspace.CanOpenTools) OpenToolsPopup(byClick: false);
            };
            toolsLeave.Tick += (sender, args) =>
            {
                toolsLeave.Stop();
                if (!toolsOpenedByClick && !DesktopToolsButton.IsMouseOver && toolsPopup?.Child.IsMouseOver != true)
                    CloseToolsPopup();
            };
            PreviewMouseDown += (sender, args) => { if (!DesktopToolsButton.IsMouseOver) CloseToolsPopup(); };
            PreviewKeyDown += CloseToolsOnEscape;
            Deactivated += (sender, args) => CloseToolsPopup();
            IsVisibleChanged += (sender, args) => { if (!IsVisible) CloseToolsPopup(); };
            LocationChanged += (sender, args) => CloseToolsPopup();
            Closed += (sender, args) => CloseToolsPopup();
        }

        private void DesktopToolsClick(object sender, RoutedEventArgs args)
        {
            if (workspace == null) return;
            if (!workspace.CanOpenTools)
            {
                CloseToolsPopup();
                ApplyCardTheme();
                workspace.ShowTools();
                return;
            }
            if (IsToolsPopupOpen && toolsOpenedByClick) CloseToolsPopup();
            else OpenToolsPopup(byClick: true);
        }

        private void OpenToolsPopup(bool byClick)
        {
            toolsHover.Stop();
            if (!IsVisible) return;
            ApplyCardTheme();
            toolsOpenedByClick = byClick;
            HideMorePanel();
            RevealChrome();
            var popup = GetToolsPopup();
            DesktopToolsButton.SetResourceReference(Control.BackgroundProperty, "NoteToolSelectedBrush");
            DesktopToolsButton.SetResourceReference(Control.ForegroundProperty, "NoteIconHoverBrush");
            ToolTipService.SetIsEnabled(DesktopToolsButton, false);
            popup.IsOpen = true;
            if (byClick && !DesktopToolsButton.IsMouseOver)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (popup.IsOpen) popup.FileTransfer.Focus(); }));
        }

        private NoteToolsPopup GetToolsPopup()
        {
            if (toolsPopup != null) return toolsPopup;
            toolsPopup = new NoteToolsPopup(DesktopToolsButton, () =>
            {
                CloseToolsPopup();
                ApplyCardTheme();
                workspace.ShowTools();
            });
            toolsPopup.Child.MouseEnter += (sender, args) => toolsLeave.Stop();
            toolsPopup.Child.MouseLeave += (sender, args) => { if (!toolsOpenedByClick) toolsLeave.Start(); };
            toolsPopup.Child.PreviewKeyDown += CloseToolsOnEscape;
            return toolsPopup;
        }

        private void CloseToolsOnEscape(object sender, KeyEventArgs args)
        {
            if (!IsToolsPopupOpen || args.Key != Key.Escape) return;
            CloseToolsPopup();
            DesktopToolsButton.Focus();
            args.Handled = true;
        }

        private void CloseToolsPopup()
        {
            toolsHover.Stop();
            toolsLeave.Stop();
            if (!IsToolsPopupOpen) return;
            toolsPopup.IsOpen = false;
            toolsOpenedByClick = false;
            DesktopToolsButton.ClearValue(Control.BackgroundProperty);
            DesktopToolsButton.ClearValue(Control.ForegroundProperty);
            ToolTipService.SetIsEnabled(DesktopToolsButton, true);
        }
    }
}
