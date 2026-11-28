using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KimNotes.utils;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Settings
{
    internal sealed partial class AccountWindow
    {
        private const string ForgetIcon = "M6,6 L18,18 M18,6 L6,18";
        private readonly CheckBox rememberPassword = new CheckBox
        {
            Content = "记住密码", IsChecked = false, Margin = new Thickness(0, 9, 0, 0)
        };
        private readonly Popup rememberedLoginMenu = new Popup
        {
            Placement = PlacementMode.Bottom, VerticalOffset = 3, StaysOpen = true, AllowsTransparency = true
        };
        private readonly StackPanel rememberedLoginRows = new StackPanel();
        private int highlightedLogin = -1;
        private bool fillingRememberedLogin, hasRememberedPassword;

        private void AttachRememberedLoginMenu(Border anchor)
        {
            rememberPassword.SetResourceReference(ForegroundProperty, DesktopCardTheme.Icon);
            var surface = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(4), Child = rememberedLoginRows, SnapsToDevicePixels = true
            };
            TextElement.SetFontFamily(surface, FontFamily);
            TextElement.SetFontSize(surface, FontSize);
            DesktopCardTheme.UseOn(surface);
            surface.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Surface);
            surface.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.SearchBorder);
            rememberedLoginMenu.PlacementTarget = anchor;
            rememberedLoginMenu.Child = surface;

            email.TextChanged += (sender, args) =>
            {
                if (fillingRememberedLogin) return;
                ClearRememberedPassword();
                RefreshRememberedLoginMenu();
            };
            email.GotKeyboardFocus += (sender, args) => RefreshRememberedLoginMenu();
            email.LostKeyboardFocus += (sender, args) => CloseRememberedLoginMenu();
            email.PreviewKeyDown += OnRememberedLoginKeyDown;
            Deactivated += (sender, args) => CloseRememberedLoginMenu();
            PreviewMouseDown += (sender, args) =>
            {
                if (!anchor.IsMouseOver && !rememberedLoginRows.IsMouseOver) CloseRememberedLoginMenu();
            };
        }

        private void RefreshRememberedLoginMenu()
        {
            string prefix = email.Text.Trim();
            CloseRememberedLoginMenu();
            if (closed || Storage.Incognito || !email.IsEnabled || !email.IsKeyboardFocusWithin || prefix.Length == 0)
                return;

            foreach (var entry in RememberedLogins.Read().Where(candidate =>
                candidate.Email.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                AddRememberedLoginRow(entry.Email);

            if (rememberedLoginRows.Children.Count == 0) return;
            rememberedLoginMenu.Width = ((FrameworkElement)rememberedLoginMenu.PlacementTarget).ActualWidth;
            rememberedLoginMenu.IsOpen = true;
        }

        private void AddRememberedLoginRow(string address)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = Text(address);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            var choose = new Border
            {
                Height = 28, Padding = new Thickness(7, 0, 3, 0), Cursor = Cursors.Hand,
                Background = Brushes.Transparent, Child = text
            };
            choose.MouseLeftButtonUp += (sender, args) => SelectRememberedLogin(address);
            var forget = IconAction("删除记住的密码", ForgetIcon, () => ForgetRememberedLogin(address));
            forget.Focusable = false;
            AutomationProperties.SetName(forget, "删除 " + address + " 的记住密码");
            Grid.SetColumn(forget, 1);
            row.Children.Add(choose);
            row.Children.Add(forget);
            var frame = new Border { Child = row, CornerRadius = new CornerRadius(4), Tag = address };
            frame.MouseEnter += (sender, args) => HighlightRememberedLogin(rememberedLoginRows.Children.IndexOf(frame));
            rememberedLoginRows.Children.Add(frame);
        }

        private void OnRememberedLoginKeyDown(object sender, KeyEventArgs args)
        {
            if (!rememberedLoginMenu.IsOpen && args.Key == Key.Down) RefreshRememberedLoginMenu();
            if (!rememberedLoginMenu.IsOpen) return;
            int count = rememberedLoginRows.Children.Count;
            switch (args.Key)
            {
                case Key.Down:
                    HighlightRememberedLogin((highlightedLogin + 1) % count);
                    break;
                case Key.Up:
                    HighlightRememberedLogin(highlightedLogin <= 0 ? count - 1 : highlightedLogin - 1);
                    break;
                case Key.Enter when highlightedLogin >= 0:
                    SelectRememberedLogin(HighlightedAddress());
                    break;
                case Key.Delete when highlightedLogin >= 0 && Keyboard.Modifiers == ModifierKeys.Shift:
                    ForgetRememberedLogin(HighlightedAddress());
                    break;
                case Key.Escape:
                    CloseRememberedLoginMenu();
                    break;
                default:
                    return;
            }
            args.Handled = true;
        }

        private void HighlightRememberedLogin(int index)
        {
            if (highlightedLogin >= 0)
                ((Border)rememberedLoginRows.Children[highlightedLogin]).ClearValue(Border.BackgroundProperty);
            highlightedLogin = index;
            if (index >= 0)
                ((Border)rememberedLoginRows.Children[index]).SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Tint);
        }

        private string HighlightedAddress() => (string)((Border)rememberedLoginRows.Children[highlightedLogin]).Tag;

        private void SelectRememberedLogin(string address)
        {
            CloseRememberedLoginMenu();
            if (Storage.Incognito || closed || !email.IsEnabled) return;
            var entry = RememberedLogins.Read().FirstOrDefault(candidate =>
                string.Equals(candidate.Email, address, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            fillingRememberedLogin = true;
            try
            {
                email.Text = entry.Email;
                email.CaretIndex = email.Text.Length;
                if (reveal) SetPasswordVisibility(false, focus: false);
                password.Password = entry.Password;
                hasRememberedPassword = true;
                password.Focus();
            }
            finally { fillingRememberedLogin = false; }
        }

        private void ForgetRememberedLogin(string address)
        {
            if (Storage.Incognito || closed || !email.IsEnabled) return;
            RememberedLogins.Remove(address);
            RefreshRememberedLoginMenu();
        }

        private void ClearRememberedPassword()
        {
            if (!hasRememberedPassword) return;
            hasRememberedPassword = false;
            password.Clear();
            visiblePassword.Clear();
            if (reveal) SetPasswordVisibility(false, focus: false);
        }

        private void CloseRememberedLoginMenu()
        {
            rememberedLoginMenu.IsOpen = false;
            highlightedLogin = -1;
            rememberedLoginRows.Children.Clear();
        }

        private void ResetRememberedLogin()
        {
            rememberPassword.IsChecked = false;
            ClearRememberedPassword();
            CloseRememberedLoginMenu();
        }
    }
}
