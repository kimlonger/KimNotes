using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KimNotes.Windows.Presentation
{
    internal static class CardActionMenu
    {
        private static readonly ResourceDictionary Styles = new ResourceDictionary
        {
            Source = new Uri("/KimNotes.WpfPreview;component/Themes/DesktopTrayMenu.xaml", UriKind.Relative)
        };
        internal static readonly Brush DeleteInk = CardWindow.Brush("#C34943");

        internal static ContextMenu Create(FrameworkElement anchor, string accessibleName, bool anchorBelow = true)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = anchor, StaysOpen = false,
                MinWidth = 101, Style = (Style)Styles["TrayPopup"]
            };
            DesktopCardTheme.UseOn(menu);
            if (anchorBelow)
            {
                menu.Placement = PlacementMode.Custom;
                menu.CustomPopupPlacementCallback = PlaceBelowAnchor;
            }
            AutomationProperties.SetName(menu, accessibleName);
            return menu;
        }

        internal static MenuItem Item(string label, UIElement icon, Action action, Brush foreground = null)
        {
            var item = CreateItem(label, icon, foreground);
            item.Click += (sender, args) => action();
            return item;
        }

        internal static MenuItem Item(string label, UIElement icon, ICommand command, Brush foreground = null)
        {
            var item = CreateItem(label, icon, foreground);
            item.Command = command;
            return item;
        }

        private static MenuItem CreateItem(string label, UIElement icon, Brush foreground)
        {
            if (foreground == null || foreground == DeleteInk) ApplyIconColor(icon,
                foreground == DeleteInk ? DesktopCardTheme.Danger : DesktopCardTheme.Icon);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border { Child = icon, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            if (foreground == DeleteInk) caption.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Danger);
            else if (foreground != null) caption.Foreground = foreground;
            content.Children.Add(caption);

            var item = new MenuItem { Header = content, MinHeight = 21, FontSize = 11.67, Style = (Style)Styles["TrayCommand"] };
            AutomationProperties.SetName(item, label);
            return item;
        }

        private static void ApplyIconColor(UIElement icon, string resource)
        {
            void Apply(Shape shape)
            {
                if (shape.Stroke != null) shape.SetResourceReference(Shape.StrokeProperty, resource);
                else if (shape.Fill != null) shape.SetResourceReference(Shape.FillProperty, resource);
            }
            if (icon is Shape direct) Apply(direct);
            else if (icon is Viewbox wrapper && wrapper.Child is Panel drawing)
                foreach (var shape in drawing.Children.OfType<Shape>()) Apply(shape);
        }

        private static CustomPopupPlacement[] PlaceBelowAnchor(Size popup, Size anchor, Point offset)
        {
            const int gap = 3;
            double x = anchor.Width - popup.Width + offset.X;
            return new[]
            {
                new CustomPopupPlacement(new Point(x, anchor.Height + gap + offset.Y), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(x, -popup.Height - gap + offset.Y), PopupPrimaryAxis.Horizontal)
            };
        }
    }
}
