using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Notes
{
    internal static class NoteCatalogVisuals
    {
        internal const string History = "M20,12 A8,8 0 1 1 4,12 A8,8 0 1 1 20,12 M12,7 V12 L15,14";
        internal const string Open = "M19,13 V20 H4 V5 H11 M10,14 L20,4 M14,4 H20 V10";
        internal const string Delete = "M4,7 H20 M9,7 V4 H15 V7 M6,7 L7.5,20 H16.5 L18,7 M10,10.5 L10.5,16.5 M14,10.5 L13.5,16.5";
        internal const string Copy = "M9,9 H20 V20 H9 Z M15,9 V4 H4 V15 H9";
        internal static readonly Geometry MoreDots = Geometry.Parse(
            "M3,2 A1,1 0 1 0 1,2 A1,1 0 1 0 3,2 Z M7,2 A1,1 0 1 0 5,2 A1,1 0 1 0 7,2 Z M11,2 A1,1 0 1 0 9,2 A1,1 0 1 0 11,2 Z");
        internal static readonly Style IconButton = ButtonStyle(true, 5);
        private static readonly Style ActionButton = ButtonStyle(false, 5);
        internal static readonly Style ListItem = ListItemStyle();
        internal static readonly Style CardMenuTrigger = CardHeaderVisibility(new Style(typeof(Button), ButtonStyle(true, 5, quiet: true)), showWhenActive: true);
        internal static readonly Style CardDate = CardHeaderVisibility(new Style(typeof(TextBlock)), showWhenActive: false);
        private static readonly System.Collections.Generic.Dictionary<int, (Brush Fill, Brush SelectedFill, Brush Text, Brush Muted)> CardPalettes =
            NoteTheme.All.ToDictionary(theme => theme.Id, theme => (
                NoteThemeBrushes.Body(theme), NoteThemeBrushes.SelectedBody(theme), NoteThemeBrushes.Text(theme), NoteThemeBrushes.Icon(theme)));

        internal static (Brush Fill, Brush SelectedFill, Brush Text, Brush Muted) CardColors(NoteTheme theme) => CardPalettes[theme.Id];

        internal static Path Icon(string geometry, Brush brush = null, int size = 13)
        {
            var path = new Path
            {
                Data = Geometry.Parse(geometry), Width = size, Height = size, Stretch = Stretch.Uniform,
                StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false
            };
            if (brush == null) path.SetResourceReference(Shape.StrokeProperty, DesktopCardTheme.Icon);
            else path.Stroke = brush;
            return path;
        }

        internal static Button Action(string label, double width = double.NaN)
        {
            var button = CardWindow.ActionButton(label);
            button.Style = ActionButton;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Width = width;
            button.Height = 25;
            return button;
        }

        internal static void ConfigureList(ListBox list)
        {
            list.Background = Brushes.Transparent;
            list.BorderThickness = new Thickness(0);
            list.Padding = new Thickness(0);
            list.ItemContainerStyle = ListItem;
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
            ScrollViewer.SetCanContentScroll(list, true);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        }

        internal static DataTrigger Selected(DependencyProperty property, object value)
        {
            var selected = new DataTrigger
            {
                Binding = new Binding(nameof(ListBoxItem.IsSelected))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
                },
                Value = true
            };
            selected.Setters.Add(new Setter(property, value));
            return selected;
        }

        private static Style CardHeaderVisibility(Style style, bool showWhenActive)
        {
            style.Setters.Add(new Setter(UIElement.VisibilityProperty, showWhenActive ? Visibility.Collapsed : Visibility.Visible));
            foreach (string property in new[] { nameof(ListBoxItem.IsSelected), nameof(ListBoxItem.IsMouseOver), nameof(ListBoxItem.IsKeyboardFocusWithin) })
            {
                var active = new DataTrigger
                {
                    Binding = new Binding(property) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) },
                    Value = true
                };
                active.Setters.Add(new Setter(UIElement.VisibilityProperty, showWhenActive ? Visibility.Visible : Visibility.Collapsed));
                style.Triggers.Add(active);
            }
            return style;
        }

        internal static FrameworkElementFactory Visual<T>(params object[] properties) where T : DependencyObject
        {
            var factory = new FrameworkElementFactory(typeof(T));
            for (int i = 0; i < properties.Length; i += 2)
                factory.SetValue((DependencyProperty)properties[i], properties[i + 1]);
            return factory;
        }

        private static Style ListItemStyle()
        {
            return new Style(typeof(ListBoxItem))
            {
                Setters =
                {
                    new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                    new Setter(Control.FocusVisualStyleProperty, null),
                    new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
                    {
                        VisualTree = Visual<ContentPresenter>(
                            ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty),
                            ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty))
                    })
                }
            };
        }

        private static Style ButtonStyle(bool icon, double radius, bool quiet = false)
        {
            var style = new Style(typeof(Button));
            var plate = Visual<Border>(Border.CornerRadiusProperty, new CornerRadius(radius),
                Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty),
                Border.BorderThicknessProperty, new Thickness(1),
                Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty),
                Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            plate.Name = "Plate";
            plate.AppendChild(Visual<ContentPresenter>(
                ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty),
                FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center,
                FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            var template = new ControlTemplate(typeof(Button)) { VisualTree = plate };
            var highlightStates = quiet ? new[] { UIElement.IsKeyboardFocusedProperty } :
                new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusedProperty };
            foreach (var property in highlightStates)
            {
                var active = new Trigger { Property = property, Value = true };
                object outline = quiet ? (object)new Binding(nameof(Control.Foreground)) { RelativeSource = RelativeSource.TemplatedParent }
                    : new DynamicResourceExtension(DesktopCardTheme.Accent);
                active.Setters.Add(new Setter(Border.BorderBrushProperty, outline, "Plate"));
                template.Triggers.Add(active);
            }
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "Plate"));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            if (icon)
            {
                style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 22d));
                style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 22d));
                style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
                style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            }
            return style;
        }
    }
}
