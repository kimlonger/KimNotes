using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Settings
{
    internal sealed partial class AccountWindow
    {
        private const double CardInset = 14;
        private const double ActionHeight = 32;
        private const string ProfilePencil = "M4,20 L5,15 L16,4 L20,8 L9,19 Z M13,7 L17,11";
        private const string OpenEye = "M2,12 Q6,6 12,6 Q18,6 22,12 Q18,18 12,18 Q6,18 2,12 Z M9,12 A3,3 0 1 1 15,12 A3,3 0 1 1 9,12";
        private const string ClosedEye = "M3,10 Q12,20 21,10 M7,14 L5,17 M12,15 V19 M17,14 L19,17";
        private static readonly Style CredentialFrameStyle = CreateCredentialFrameStyle();
        private readonly RowDefinition secondaryActions = new RowDefinition();
        private readonly Button passwordToggle = IconAction("显示密码", ClosedEye);
        private readonly StackPanel accountContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        private Grid BuildCard()
        {
            accountContent.Children.Add(BuildIdentity());
            AddCredentials();
            accountContent.Children.Add(fields);
            accountContent.Children.Add(BuildStatus());
            accountContent.Children.Add(BuildActions());
            var viewport = new Grid();
            viewport.Children.Add(accountContent);
            return viewport;
        }

        private Grid BuildIdentity()
        {
            var identity = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            identity.ColumnDefinitions.Add(new ColumnDefinition());
            var portrait = new Grid
            {
                Width = 46, Height = 46, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Clip = new EllipseGeometry(new Point(23, 23), 23, 23)
            };
            portrait.SetResourceReference(Panel.BackgroundProperty, DesktopCardTheme.Field);
            portrait.Children.Add(avatar);
            portrait.Children.Add(fallback);
            identity.Children.Add(portrait);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(names, 1);
            var title = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
            profile.Width = profile.Height = 26;
            profile.Margin = new Thickness(3, 0, 0, 0);
            DockPanel.SetDock(profile, Dock.Right);
            title.Children.Add(profile);
            accountName.FontSize = 15;
            accountName.FontWeight = FontWeights.SemiBold;
            accountName.VerticalAlignment = VerticalAlignment.Center;
            accountName.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(accountName);
            names.Children.Add(title);
            accountInfo.FontSize = 11.5;
            accountInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            accountInfo.Margin = new Thickness(0, 3, 0, 0);
            names.Children.Add(accountInfo);
            identity.Children.Add(names);
            return identity;
        }

        private void AddCredentials()
        {
            StyleCredential(email, "邮箱");
            fields.Children.Add(Text("邮箱", Muted));
            var emailFrame = CredentialFrame(email);
            emailFrame.Margin = new Thickness(0, 4, 0, 8);
            fields.Children.Add(emailFrame);

            fields.Children.Add(Text("密码", Muted));
            var passwordRow = new Grid();
            passwordRow.ColumnDefinitions.Add(new ColumnDefinition());
            passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            passwordToggle.Margin = new Thickness(0, 0, 3, 0);
            passwordToggle.Click += (sender, args) => SetPasswordVisibility(!reveal, focus: true);
            var inputs = new Grid();
            StyleCredential(password, "密码");
            StyleCredential(visiblePassword, "密码");
            inputs.Children.Add(password);
            inputs.Children.Add(visiblePassword);
            passwordRow.Children.Add(inputs);
            Grid.SetColumn(passwordToggle, 1);
            passwordRow.Children.Add(passwordToggle);
            fields.Children.Add(CredentialFrame(passwordRow));
            fields.Children.Add(rememberPassword);
            AttachRememberedLoginMenu(emailFrame);
        }

        private void SetPasswordVisibility(bool visible, bool focus)
        {
            reveal = visible;
            if (reveal) visiblePassword.Text = password.Password;
            else
            {
                password.Password = visiblePassword.Text;
                visiblePassword.Clear();
            }
            string action = reveal ? "隐藏密码" : "显示密码";
            passwordToggle.Content = VectorIcon(reveal ? OpenEye : ClosedEye, Muted, 16);
            ((RoundedToolTip)passwordToggle.ToolTip).Content = action;
            AutomationProperties.SetName(passwordToggle, action);
            visiblePassword.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            password.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            if (!focus) return;
            if (reveal)
            {
                visiblePassword.Focus();
                visiblePassword.CaretIndex = visiblePassword.Text.Length;
            }
            else password.Focus();
        }

        private static Button IconAction(string label, string glyph, Action click = null)
        {
            var button = Button(label, click);
            button.Content = VectorIcon(glyph, Muted, 16);
            button.Width = 28;
            button.Height = 26;
            button.Padding = new Thickness(0);
            button.FocusVisualStyle = null;
            button.Background = button.BorderBrush = Brushes.Transparent;
            AutomationProperties.SetName(button, label);
            var hint = new RoundedToolTip { Content = label };
            hint.SetResourceReference(BackgroundProperty, DesktopCardTheme.Surface);
            hint.SetResourceReference(BorderBrushProperty, DesktopCardTheme.Line);
            hint.SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            button.ToolTip = hint;
            return button;
        }

        private static void StyleCredential(Control input, string label)
        {
            input.Padding = new Thickness(9, 0, 7, 0);
            input.BorderThickness = new Thickness(0);
            input.Background = Brushes.Transparent;
            input.FocusVisualStyle = null;
            input.VerticalContentAlignment = VerticalAlignment.Center;
            input.SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            AutomationProperties.SetName(input, label);
        }

        private static Border CredentialFrame(UIElement input) => new Border
        {
            Style = CredentialFrameStyle, Height = 32, Margin = new Thickness(0, 4, 0, 0), Child = input
        };

        private static Style CreateCredentialFrameStyle()
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.SearchSurface)));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.SearchBorder)));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(6)));
            var focus = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent)));
            style.Triggers.Add(focus);
            return style;
        }

        private Border BuildStatus()
        {
            statusSurface.Padding = new Thickness(11, 7, 11, 8);
            statusSurface.Margin = new Thickness(0, 10, 0, 0);
            statusSurface.CornerRadius = new CornerRadius(8);
            statusSurface.BorderThickness = new Thickness(1);
            statusSurface.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Field);
            statusSurface.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            var details = new StackPanel();
            syncLabel.FontWeight = FontWeights.SemiBold;
            syncLabel.Margin = new Thickness(0, 0, 0, 5);
            status.TextWrapping = TextWrapping.Wrap;
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            status.LineHeight = 16;
            status.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            status.MaxHeight = status.LineHeight * 3;
            var fullStatus = Text("", Muted);
            fullStatus.MaxWidth = 290;
            fullStatus.TextWrapping = TextWrapping.Wrap;
            fullStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = status });
            var hint = new RoundedToolTip { Content = fullStatus };
            hint.SetResourceReference(BackgroundProperty, DesktopCardTheme.Surface);
            hint.SetResourceReference(BorderBrushProperty, DesktopCardTheme.Line);
            hint.SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            status.ToolTip = hint;
            lastSync.FontSize = 11;
            lastSync.Margin = new Thickness(0, 7, 0, 0);
            details.Children.Add(syncLabel);
            details.Children.Add(status);
            details.Children.Add(lastSync);
            statusSurface.Child = details;
            return statusSurface;
        }

        private Grid BuildActions()
        {
            var buttons = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ActionHeight) });
            buttons.RowDefinitions.Add(secondaryActions);
            login.Height = synchronize.Height = signOut.Height = ActionHeight;
            Grid.SetColumnSpan(login, 3);
            Grid.SetColumn(synchronize, 2);
            Grid.SetRow(register, 1);
            register.Background = Brushes.Transparent;
            register.BorderBrush = Brushes.Transparent;
            register.SetResourceReference(ForegroundProperty, DesktopCardTheme.Accent);
            buttons.Children.Add(login);
            buttons.Children.Add(register);
            buttons.Children.Add(signOut);
            buttons.Children.Add(synchronize);
            return buttons;
        }

        private void SetAccountLayout(bool signedIn, bool hasSession)
        {
            accountContent.Margin = new Thickness(CardInset, signedIn ? 8 : 11, CardInset, signedIn ? 28 : 11);
            secondaryActions.Height = new GridLength(signedIn ? 0 : ActionHeight + 6);
            Grid.SetRow(signOut, signedIn ? 0 : 1);
            Grid.SetColumn(signOut, signedIn ? 0 : 2);
            Grid.SetColumnSpan(register, hasSession ? 1 : 3);
            register.Height = hasSession ? ActionHeight : 26;
        }
    }
}
