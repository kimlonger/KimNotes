using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using static KimNotes.Windows.Settings.FeatureWindow;

namespace KimNotes.Windows.Todos
{
    internal static class TodoAppearance
    {
        internal const string Pin = "M9,4 H15 V10 L17,13 H7 L9,10 Z M12,13 V21";
        internal const string Plus = "M12,5 V19 M5,12 H19";
        internal const string Clock = "M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 M12,7 V12 L16,14";
        private const string NormalCircle = "M12,6 A6,6 0 1 1 12,18 A6,6 0 1 1 12,6";
        private const string ImportantFlag = "M6,21 V4 H19 L16,8 L19,12 H6";
        private const string UrgentTriangle = "M12,3 L22,21 H2 Z M12,9 V14 M12,17.5 H12.12";
        internal const string RepeatArrows = "M19,5 L22,8 L19,11 M22,8 H6 A3,3 0 0 0 3,11 M5,19 L2,16 L5,13 M2,16 H18 A3,3 0 0 0 21,13";
        internal const string Note = "M6,4 H15 L19,8 V20 H6 Z M14,4 V9 H19 M9,12 H15 M9,16 H15";
        internal const string Chevron = "M7,10 L12,15 L17,10";
        internal static readonly Brush Subtle = Muted;
        internal static readonly Brush BlueTint = CardWindow.Brush("#E8EFF7");
        internal static readonly Brush Alert = Warning;
        internal static readonly Brush AlertTint = CardWindow.Brush("#FCEBE9");
        private static readonly Brush Important = CardWindow.Brush("#AC5B10");
        private static readonly Brush ImportantTint = CardWindow.Brush("#FCF0E2");
        private static readonly ControlTemplate DatePickerTemplate = CreateDatePickerTemplate();

        internal static string PriorityName(TodoPriority priority) => priority == TodoPriority.Urgent ? "紧急" :
            priority == TodoPriority.Important ? "重要" : "普通";

        internal static string PriorityGlyph(TodoPriority priority)
        {
            switch (priority)
            {
                case TodoPriority.Important: return ImportantFlag;
                case TodoPriority.Urgent: return UrgentTriangle;
                default: return NormalCircle;
            }
        }

        internal static Brush PriorityTone(TodoPriority priority) => priority == TodoPriority.Urgent ? Alert :
            priority == TodoPriority.Important ? Important : Subtle;

        internal static Brush PriorityTint(TodoPriority priority) => priority == TodoPriority.Urgent ? AlertTint :
            priority == TodoPriority.Important ? ImportantTint : BlueTint;

        internal static string PriorityInkResource(TodoPriority priority) => priority == TodoPriority.Urgent ? DesktopCardTheme.Danger :
            priority == TodoPriority.Important ? DesktopCardTheme.Caution : DesktopCardTheme.Icon;

        internal static StackPanel IconText(string glyph, string label, Brush foreground, double size = 12, string themedStroke = null)
        {
            var inline = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = VectorIcon(glyph, foreground, size, themedStroke);
            icon.Margin = new Thickness(0, 0, 4, 0);
            inline.Children.Add(icon);
            inline.Children.Add(Text(label, foreground));
            return inline;
        }

        internal static Border Field(TextBox input, string label, double height, bool multiline = false, string glyph = null)
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Line)));
            style.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Field)));
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent)));
            style.Triggers.Add(focus);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Surface)));
            style.Triggers.Add(disabled);
            var field = new Grid();
            var inset = new Thickness(glyph == null ? 8 : 29, multiline ? 7 : 0, 7, 0);
            var placeholder = Text(label, Subtle);
            placeholder.Margin = inset;
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
            field.Children.Add(placeholder);
            if (glyph != null)
            {
                var icon = VectorIcon(glyph, Subtle, 16);
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                icon.VerticalAlignment = VerticalAlignment.Center;
                icon.Margin = new Thickness(7, 0, 0, 0);
                field.Children.Add(icon);
            }
            input.Padding = inset;
            input.BorderThickness = new Thickness(0);
            input.Background = Brushes.Transparent;
            input.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
            input.VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
            AutomationProperties.SetName(input, label);
            void UpdatePlaceholder() => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            input.TextChanged += (sender, args) => UpdatePlaceholder();
            UpdatePlaceholder();
            field.Children.Add(input);
            return new Border
            {
                Height = height, Style = style, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Child = field
            };
        }

        internal static RadioButton Pill(string label, string group, Brush tone, Brush tint, string glyph = null,
            Brush glyphTone = null, double height = 23)
        {
            string inkResource = tone == Important ? DesktopCardTheme.Caution : tone == Alert ? DesktopCardTheme.Danger : DesktopCardTheme.Accent;
            string fillResource = tint == ImportantTint ? DesktopCardTheme.CautionTint : tint == AlertTint ? DesktopCardTheme.DangerTint : DesktopCardTheme.Tint;
            var border = new FrameworkElementFactory(typeof(Border), "Pill");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(height / 2));
            border.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Field);
            border.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new Thickness(7, 1, 7, 1));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = border };
            var selected = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(fillResource), "Pill"));
            selected.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(inkResource), "Pill"));
            template.Triggers.Add(selected);
            var hovered = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hovered.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(inkResource), "Pill"));
            template.Triggers.Add(hovered);
            string normalInk = glyphTone == null || glyphTone == Subtle ? DesktopCardTheme.Ink : inkResource;
            var labelView = Text(label);
            UIElement content = labelView;
            if (glyph != null)
            {
                var inline = IconText(glyph, label, glyphTone ?? tone,
                    themedStroke: glyphTone == Subtle ? DesktopCardTheme.Icon : inkResource);
                labelView = (TextBlock)inline.Children[1];
                content = inline;
            }
            var radio = new RadioButton
            {
                Content = content,
                GroupName = group, Template = template, Cursor = Cursors.Hand,
                Height = height, Margin = new Thickness(0, 0, 4, 0), FontSize = 11
            };
            labelView.SetResourceReference(TextBlock.ForegroundProperty, normalInk);
            radio.Checked += (sender, args) => labelView.SetResourceReference(TextBlock.ForegroundProperty, inkResource);
            radio.Unchecked += (sender, args) => labelView.SetResourceReference(TextBlock.ForegroundProperty, normalInk);
            AutomationProperties.SetName(radio, label);
            return radio;
        }

        internal static void StyleDatePicker(DatePicker picker) => picker.Template = DatePickerTemplate;

        private static ControlTemplate CreateDatePickerTemplate()
        {
            var root = new FrameworkElementFactory(typeof(Grid), "PART_Root");
            var frame = new FrameworkElementFactory(typeof(Border), "DateFrame");
            frame.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            frame.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Field);
            root.AppendChild(frame);

            var layout = new FrameworkElementFactory(typeof(DockPanel));
            frame.AppendChild(layout);
            var calendar = new FrameworkElementFactory(typeof(Button), "PART_Button");
            calendar.SetValue(DockPanel.DockProperty, Dock.Right);
            calendar.SetValue(FrameworkElement.WidthProperty, 26d);
            calendar.SetValue(UIElement.FocusableProperty, false);
            calendar.SetValue(AutomationProperties.NameProperty, "打开日历");
            calendar.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            var hitArea = new FrameworkElementFactory(typeof(Border));
            hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var icon = new FrameworkElementFactory(typeof(Path));
            icon.SetValue(Path.DataProperty, Geometry.Parse("M4,6 H20 V21 H4 Z M4,11 H20 M8,3 V8 M16,3 V8"));
            icon.SetValue(Shape.StretchProperty, Stretch.Uniform);
            icon.SetResourceReference(Shape.StrokeProperty, DesktopCardTheme.Icon);
            icon.SetValue(Shape.StrokeThicknessProperty, 1.35d);
            icon.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
            icon.SetValue(FrameworkElement.WidthProperty, 12d);
            icon.SetValue(FrameworkElement.HeightProperty, 12d);
            hitArea.AppendChild(icon);
            calendar.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = hitArea });
            layout.AppendChild(calendar);

            var input = new FrameworkElementFactory(typeof(DatePickerTextBox), "PART_TextBox");
            input.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            input.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            input.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
            input.SetValue(Control.PaddingProperty, new Thickness(7, 0, 0, 0));
            input.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
            var inputFrame = new FrameworkElementFactory(typeof(Border));
            inputFrame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var inputHost = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            inputHost.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            inputFrame.AppendChild(inputHost);
            input.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(DatePickerTextBox)) { VisualTree = inputFrame });
            layout.AppendChild(input);

            var popup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.StaysOpenProperty, false);
            root.AppendChild(popup);
            var template = new ControlTemplate(typeof(DatePicker)) { VisualTree = root };
            var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent), "DateFrame"));
            template.Triggers.Add(focused);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Surface), "DateFrame"));
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(DesktopCardTheme.Icon), "PART_TextBox"));
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55d, "PART_Button"));
            template.Triggers.Add(disabled);
            return template;
        }
    }
}
