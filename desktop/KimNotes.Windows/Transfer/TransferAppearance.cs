using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KimNotes.Windows.Presentation;
using ShapePath = System.Windows.Shapes.Path;

namespace KimNotes.Windows.Transfer
{
    internal static class TransferAppearance
    {
        private static readonly ControlTemplate ActionTemplate = BuildActionTemplate();
        private static readonly ControlTemplate InputTemplate = BuildInputTemplate();

        internal static Visibility VisibilityFor(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
        internal static T PlaceDocked<T>(T content, Dock place) where T : UIElement
        {
            DockPanel.SetDock(content, place);
            return content;
        }

        internal static StackPanel ContentGroup(Panel parent)
        {
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 4, 5, 4) };
            parent.Children.Add(panel);
            return panel;
        }

        internal static Border Card(UIElement body)
        {
            var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8), BorderThickness = new Thickness(1), Child = body };
            card.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Tint);
            card.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            return card;
        }

        internal static Grid DropCard(UIElement body)
        {
            var card = new Grid { Background = Brushes.Transparent };
            var outline = new Rectangle
            {
                RadiusX = 8, RadiusY = 8, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false
            };
            outline.SetResourceReference(Shape.FillProperty, DesktopCardTheme.Field);
            outline.SetResourceReference(Shape.StrokeProperty, DesktopCardTheme.Line);
            card.Children.Add(outline);
            card.Children.Add(body);
            return card;
        }

        internal static Hyperlink InlineAction(string text, Action action)
        {
            var link = new Hyperlink(new Run(text)) { FontWeight = FontWeights.SemiBold, TextDecorations = null };
            link.SetResourceReference(TextElement.ForegroundProperty, DesktopCardTheme.Accent);
            link.Click += (sender, args) => action();
            return link;
        }

        internal static DockPanel PickupHint(string message)
        {
            var hint = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
            var glyph = Glyph("M5,10 H19 V21 H5 Z M8,10 V7 A4,4 0 0 1 16,7 V10", 10);
            glyph.SetResourceReference(Shape.StrokeProperty, DesktopCardTheme.Icon);
            hint.Children.Add(PlaceDocked(glyph, Dock.Left));
            var label = Label(message, 10.5, centered: true);
            label.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
            hint.Children.Add(label);
            return hint;
        }

        internal static void StyleCodeInput(TextBox input)
        {
            input.Template = InputTemplate;
            input.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Field);
            input.BorderThickness = new Thickness(1);
            input.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Line);
            input.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
        }

        private static ControlTemplate BuildInputTemplate()
        {
            var outline = new FrameworkElementFactory(typeof(Border), "Outline");
            outline.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty })
                outline.SetBinding(property, new Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            content.SetValue(UIElement.FocusableProperty, false);
            outline.AppendChild(content);
            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = outline };
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent), "Outline"));
            template.Triggers.Add(focus);
            return template;
        }

        internal static ShapePath Glyph(string data, double size)
        {
            var path = new ShapePath
            {
                Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform,
                StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            };
            path.SetResourceReference(Shape.StrokeProperty, DesktopCardTheme.Accent);
            return path;
        }

        internal static TextBlock Label(string text, double size = 12.5, bool bold = false, bool centered = false)
        {
            var label = new TextBlock
            {
                Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left, Margin = new Thickness(4, 3, 4, 3)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
            return label;
        }

        internal static Button ActionButton(object label, Action action, bool primary = false, string accessible = null)
        {
            var button = new Button
            {
                Content = label, Template = ActionTemplate, MinHeight = 32, Margin = new Thickness(2),
                Padding = new Thickness(5, 2, 5, 2), BorderThickness = new Thickness(1), Cursor = Cursors.Hand
            };
            if (accessible != null) AutomationProperties.SetName(button, accessible);
            SetButtonAppearance(button, primary);
            button.Click += (sender, args) => action();
            return button;
        }

        internal static Button ModeButton(string label, Action action)
        {
            var button = ActionButton(label, action);
            button.Height = button.MinHeight = 30;
            return button;
        }

        internal static void SetModeAppearance(Button button, bool selected)
        {
            if (selected)
            {
                button.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Tint);
                button.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Accent);
                button.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Accent);
            }
            else
                SetButtonAppearance(button, false);
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            AutomationProperties.SetItemStatus(button, selected ? "已选中" : "未选中");
        }

        private static ControlTemplate BuildActionTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "Surface");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.PaddingProperty })
                border.SetBinding(property, new Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.OpacityProperty, 0.88d, "Surface"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.OpacityProperty, 0.75d, "Surface"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.45d, "Surface"));
            template.Triggers.Add(disabled);
            return template;
        }

        internal static void SetButtonAppearance(Button button, bool primary)
        {
            if (primary)
            {
                button.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Accent);
                button.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Accent);
                button.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.PrimaryInk);
            }
            else
            {
                button.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Field);
                button.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
                button.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Line);
            }
        }
    }
}
