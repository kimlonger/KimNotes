using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KimNotes.Windows.Capture
{
    internal enum CaptureTool
    {
        Edit, Rectangle, Arrow, Mosaic, Text, Ocr, PickColor, Insert, Undo, Save, Copy, Cancel, Pin,
        SaveDefault, Previous, Next, Destroy, Translate, Pen, Confirm
    }

    internal sealed class CaptureToolbar : Border
    {
        internal static readonly Brush Outline = Paint(210, 218, 227);
        internal static readonly Brush Divider = Paint(231, 235, 240);
        internal static readonly Brush Icon = Paint(85, 96, 107);
        internal static readonly Brush Accent = Paint(74, 127, 193);
        private static readonly Brush Highlight = Paint(232, 240, 249);
        private static readonly Brush Pressed = Paint(203, 216, 232);
        private static readonly Brush HoverOutline = Paint(168, 196, 230);
        private static readonly Brush CancelIcon = Paint(218, 85, 91);
        private static readonly Brush PinIcon = Paint(25, 171, 120);
        internal static ControlTemplate ButtonChrome { get; } = BuildToolTemplate();

        private readonly Button undo;
        private readonly Dictionary<CaptureTool, Button> actions = new Dictionary<CaptureTool, Button>();
        private Button selected;

        internal CaptureToolbar(Action<CaptureTool> use, bool canUndo = true)
        {
            Background = Brushes.White;
            BorderBrush = Outline;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(5);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            Child = buttons;

            Button Add(CaptureTool tool, string label, string path)
            {
                var geometry = Geometry.Parse(path);
                geometry.Freeze();
                var glyph = new Path
                {
                    Data = geometry,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                glyph.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
                var glyphGrid = new Canvas { Width = 24, Height = 24 };
                glyphGrid.Children.Add(glyph);
                var glyphView = new Viewbox { Width = 18, Height = 18, Child = glyphGrid, IsHitTestVisible = false };
                var button = new Button
                {
                    Width = 24,
                    Height = 24,
                    Content = glyphView,
                    Foreground = tool == CaptureTool.Cancel ? CancelIcon : tool == CaptureTool.Pin ? PinIcon : Icon,
                    Template = ButtonChrome,
                    Margin = new Thickness(0, 0, tool == CaptureTool.Pin ? 0 : 1, 0),
                    FocusVisualStyle = null,
                    Cursor = Cursors.Hand,
                    ToolTip = label,
                    Focusable = true
                };
                AutomationProperties.SetName(button, label);
                button.Click += (sender, args) => use(tool);
                buttons.Children.Add(button);
                actions.Add(tool, button);
                return button;
            }

            void Separate()
            {
                var cell = new Grid { Width = 8, Height = 24, IsHitTestVisible = false };
                cell.Children.Add(new Border
                {
                    Width = 1, Height = 14, Background = Divider,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                });
                buttons.Children.Add(cell);
            }

            Add(CaptureTool.Rectangle, "矩形标注", "M4,5 H20 V19 H4 Z");
            Add(CaptureTool.Arrow, "箭头标注", "M5,19 L19,5 M10,5 H19 V14");
            Add(CaptureTool.Pen, "画笔标注", "M4,20 L5.5,15 L16,4.5 C17,3.5 18,3.5 19,4.5 L19.5,5 C20.5,6 20.5,7 19.5,8 L9,18.5 Z M14.5,6 L18,9.5");
            Add(CaptureTool.Mosaic, "马赛克", "M4.5,4.5 H10.5 V10.5 H4.5 Z M13.5,4.5 H19.5 V10.5 H13.5 Z M4.5,13.5 H10.5 V19.5 H4.5 Z M13.5,13.5 H19.5 V19.5 H13.5 Z");
            Add(CaptureTool.Text, "文字标注", "M4,7 V5 H20 V7 M12,5 V19 M8.5,19 H15.5");
            Separate();
            Add(CaptureTool.Ocr, "文字识别", "M4,9 V4 H9 M15,4 H20 V9 M4,15 V20 H9 M15,20 H20 V15 M8,9 H16 M12,9 V16");
            Add(CaptureTool.PickColor, "取色", "M5,16 L15,6 L18,9 L8,19 L4,20 Z M13,5 L19,11 M17,6 L20,3");
            Add(CaptureTool.Insert, "插入最近打开的便签", "M5,4 H19 V20 H5 Z M8.5,8 H15.5 M8.5,12 H15.5 M8.5,16 H13");
            undo = Add(CaptureTool.Undo, "撤销标注 (Ctrl+Z)", "M9,5 L4,10 L9,15 M4,10 H13 C22,10 22,19 13,19 H9");
            undo.IsEnabled = canUndo;
            Add(CaptureTool.Save, "另存图片", "M5,12 V20 H19 V12 M12,4 V15 M8,11 L12,15 L16,11");
            Add(CaptureTool.Copy, "复制图片", "M8,8 H20 V20 H8 Z M15,5 H5 V15");
            Separate();
            Add(CaptureTool.Cancel, "取消 / 关闭", "M6,6 L18,18 M18,6 L6,18");
            Add(CaptureTool.Pin, "复制并贴图", "M4,12 L10,18 L20,6");
        }

        internal bool CanUndo
        {
            set { undo.IsEnabled = value; }
        }

        internal Button GetToolButton(CaptureTool tool) => actions.TryGetValue(tool, out var button) ? button : null;

        internal void Select(CaptureTool? tool)
        {
            var next = tool.HasValue ? GetToolButton(tool.Value) : null;
            if (selected == next) return;
            if (selected != null) SetSelection(selected, false);
            selected = next;
            if (selected != null) SetSelection(selected, true);
        }

        private static void SetSelection(Button button, bool value)
        {
            button.Tag = value;
            if (button.Foreground != PinIcon && button.Foreground != CancelIcon)
                button.Foreground = value ? Accent : Icon;
            AutomationProperties.SetItemStatus(button, value ? "已选中" : "未选中");
        }

        private static ControlTemplate BuildToolTemplate()
        {
            var surface = new FrameworkElementFactory(typeof(Border), "Surface");
            surface.SetValue(BackgroundProperty, Brushes.Transparent);
            surface.SetValue(BorderBrushProperty, Brushes.Transparent);
            surface.SetValue(BorderThicknessProperty, new Thickness(1));
            surface.SetValue(CornerRadiusProperty, new CornerRadius(5));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
            void HighlightFor(DependencyProperty property, Brush fill)
            {
                var trigger = new Trigger { Property = property, Value = true };
                trigger.Setters.Add(new Setter(BackgroundProperty, fill, "Surface"));
                trigger.Setters.Add(new Setter(BorderBrushProperty, HoverOutline, "Surface"));
                template.Triggers.Add(trigger);
            }
            HighlightFor(Button.IsMouseOverProperty, Highlight);
            HighlightFor(Button.IsKeyboardFocusedProperty, Highlight);
            HighlightFor(TagProperty, Highlight);
            HighlightFor(Button.IsPressedProperty, Pressed);

            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.4, "Surface"));
            template.Triggers.Add(disabled);
            return template;
        }

        internal static Brush Paint(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
