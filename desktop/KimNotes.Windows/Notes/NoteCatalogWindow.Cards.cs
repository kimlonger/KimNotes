using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using static KimNotes.Windows.Notes.NoteCatalogVisuals;

namespace KimNotes.Windows.Notes
{
    internal sealed partial class NoteCatalogWindow
    {
        private DataTemplate CardTemplate()
        {
            const double previewLineHeight = 16;
            const int previewLineLimit = 3;
            var appearance = new Style(typeof(Border));
            appearance.Setters.Add(new Setter(Border.BackgroundProperty, new Binding(nameof(Entry.Fill))));
            var hovered = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hovered.Setters.Add(new Setter(Border.BackgroundProperty, new Binding(nameof(Entry.SelectedFill))));
            appearance.Triggers.Add(hovered);

            var selected = new MultiDataTrigger();
            selected.Conditions.Add(new Condition(new Binding(nameof(ListBoxItem.IsSelected))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
            }, true));
            selected.Conditions.Add(new Condition(new Binding(nameof(ListBox.IsMouseOver))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBox), 1)
            }, false));
            selected.Setters.Add(new Setter(Border.BackgroundProperty, new Binding(nameof(Entry.SelectedFill))));
            appearance.Triggers.Add(selected);
            var card = Visual<Border>(Border.StyleProperty, appearance,
                Border.CornerRadiusProperty, new CornerRadius(6),
                FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 4), Border.PaddingProperty, new Thickness(14, 8, 11, 9));
            card.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(Entry.Details)));

            var content = Visual<StackPanel>();
            card.AppendChild(content);
            content.AppendChild(CardHeader());
            var preview = Visual<TextBlock>(TextBlock.TextWrappingProperty, TextWrapping.Wrap,
                TextBlock.FontSizeProperty, 12d, TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis,
                TextBlock.LineHeightProperty, previewLineHeight, TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight,
                FrameworkElement.MaxHeightProperty, previewLineHeight * previewLineLimit,
                FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
            preview.SetBinding(TextBlock.TextProperty, new Binding(nameof(Entry.Preview)));
            preview.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Entry.MutedInk)));
            preview.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(Entry.PreviewVisibility)));
            content.AppendChild(preview);
            return new DataTemplate { VisualTree = card };
        }

        private FrameworkElementFactory CardHeader()
        {
            var header = Visual<DockPanel>(FrameworkElement.MinHeightProperty, 18d);
            var trailing = Visual<Grid>(DockPanel.DockProperty, Dock.Right, FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
            var date = Visual<TextBlock>(FrameworkElement.StyleProperty, CardDate, TextBlock.FontSizeProperty, 10.5,
                FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            date.SetBinding(TextBlock.TextProperty, new Binding(nameof(Entry.Date)));
            date.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Entry.MutedInk)));
            trailing.AppendChild(date);

            var menu = Visual<Button>(FrameworkElement.StyleProperty, CardMenuTrigger,
                FrameworkElement.WidthProperty, 22d, FrameworkElement.HeightProperty, 18d,
                FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right,
                FrameworkElement.ToolTipProperty, "便签操作", AutomationProperties.NameProperty, "便签操作");
            menu.SetBinding(Control.ForegroundProperty, new Binding(nameof(Entry.MutedInk)));
            menu.AddHandler(Button.ClickEvent, new RoutedEventHandler(OpenEntryMenu));
            var dots = Visual<Path>(Path.DataProperty, MoreDots,
                FrameworkElement.WidthProperty, 11d, FrameworkElement.HeightProperty, 4d, Path.StretchProperty, Stretch.Uniform,
                UIElement.IsHitTestVisibleProperty, false);
            dots.SetBinding(Path.FillProperty, new Binding(nameof(Entry.MutedInk)));
            menu.AppendChild(dots);
            trailing.AppendChild(menu);
            header.AppendChild(trailing);

            var title = Visual<TextBlock>(TextBlock.FontSizeProperty, 13d,
                FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center, TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Entry.Name)));
            title.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Entry.TitleInk)));
            header.AppendChild(title);
            return header;
        }
    }
}
