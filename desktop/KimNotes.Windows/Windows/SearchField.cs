using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KimNotes.Windows.Presentation
{
    internal static class SearchField
    {
        internal const int Height = 32;

        internal static Border Create(TextBox input, string label, TextBlock count)
        {
            var dock = new DockPanel();
            var glyph = new Path
            {
                Data = Geometry.Parse("M15.5,10 A5.5,5.5 0 1 1 4.5,10 A5.5,5.5 0 1 1 15.5,10 M14,14 L20,20"),
                Width = 13, Height = 13, Stretch = Stretch.Uniform,
                StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0), IsHitTestVisible = false
            };
            glyph.SetResourceReference(Shape.StrokeProperty, DesktopCardTheme.Icon);
            dock.Children.Add(glyph);

            count.FontSize = 10.5;
            count.VerticalAlignment = VerticalAlignment.Center;
            count.Margin = new Thickness(8, 0, 10, 0);
            count.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
            DockPanel.SetDock(count, Dock.Right);
            dock.Children.Add(count);

            var field = new Grid();
            var placeholder = new TextBlock
            {
                Text = label, IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
            input.Padding = new Thickness(8, 0, 4, 0);
            input.BorderThickness = new Thickness(0);
            input.BorderBrush = input.Background = Brushes.Transparent;
            input.FocusVisualStyle = null;
            input.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
            input.VerticalContentAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(input, label);
            input.TextChanged += (sender, args) =>
                placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            field.Children.Add(placeholder);
            field.Children.Add(input);
            dock.Children.Add(field);
            var surface = new Border
            {
                Height = Height, CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), Child = dock
            };
            surface.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.SearchSurface);
            surface.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.SearchBorder);
            surface.MouseLeftButtonDown += (sender, args) => input.Focus();
            return surface;
        }
    }
}
