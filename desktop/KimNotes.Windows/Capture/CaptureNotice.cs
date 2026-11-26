using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureNotice : Window
    {
        internal CaptureNotice(string message, Window owner)
        {
            DesktopCardTheme.Ensure();
            Owner = owner?.IsVisible == true ? owner : null;
            WindowStartupLocation = Owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            Title = message;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;

            var caption = new TextBlock
            {
                Text = message, FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 270
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
            var surface = new Border
            {
                Child = caption, Padding = new Thickness(15, 9, 15, 9), CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1)
            };
            surface.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Surface);
            surface.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            AutomationProperties.SetLiveSetting(caption, AutomationLiveSetting.Polite);
            Content = surface;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (sender, args) => Close();
            Loaded += (sender, args) => timer.Start();
            Closed += (sender, args) => timer.Stop();
        }
    }
}
