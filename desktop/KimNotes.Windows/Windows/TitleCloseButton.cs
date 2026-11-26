using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KimNotes.Windows.Presentation
{
    internal sealed class TitleCloseButton : Button
    {
        private static readonly Brush DefaultInk = CreateBrush(Color.FromRgb(85, 96, 107));
        private static readonly Brush HoverSurface = CreateBrush(Color.FromArgb(105, 255, 255, 255));
        private static readonly ControlTemplate CloseTemplate = CreateTemplate();

        public TitleCloseButton()
        {
            Width = 26;
            Height = 26;
            VerticalAlignment = VerticalAlignment.Center;
            Foreground = DefaultInk;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            FocusVisualStyle = null;
            ToolTip = "关闭";
            AutomationProperties.SetName(this, "关闭");
            Template = CloseTemplate;
        }

        internal static TitleCloseButton Create(Action close)
        {
            var button = new TitleCloseButton();
            button.Click += (sender, args) => close();
            return button;
        }

        private static ControlTemplate CreateTemplate()
        {
            var surface = new FrameworkElementFactory(typeof(Border), "Surface");
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            var canvas = new FrameworkElementFactory(typeof(Canvas));
            canvas.SetValue(WidthProperty, 16d);
            canvas.SetValue(HeightProperty, 16d);
            canvas.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            canvas.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            canvas.SetValue(IsHitTestVisibleProperty, false);
            var geometry = Geometry.Parse("M2,2 L14,14 M14,2 L2,14");
            geometry.Freeze();
            var glyph = new FrameworkElementFactory(typeof(Path), "Glyph");
            glyph.SetValue(Path.DataProperty, geometry);
            glyph.SetValue(Shape.StrokeProperty, new TemplateBindingExtension(ForegroundProperty));
            glyph.SetValue(Shape.StrokeThicknessProperty, 1.7d);
            glyph.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            glyph.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            canvas.AppendChild(glyph);
            surface.AppendChild(canvas);

            var template = new ControlTemplate(typeof(TitleCloseButton)) { VisualTree = surface };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverSurface, "Surface"));
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty,
                new Binding(nameof(Foreground)) { RelativeSource = RelativeSource.TemplatedParent }, "Surface"));
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(OpacityProperty, 0.75d, "Surface"));
            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.5d, "Surface"));
            template.Triggers.Add(hover);
            template.Triggers.Add(focus);
            template.Triggers.Add(pressed);
            template.Triggers.Add(disabled);
            return template;
        }

        private static Brush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
