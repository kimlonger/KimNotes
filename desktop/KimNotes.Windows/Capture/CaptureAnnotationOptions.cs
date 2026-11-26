using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static KimNotes.Windows.Capture.CaptureToolbar;

namespace KimNotes.Windows.Capture
{
    internal readonly struct CaptureAnnotationStyle
    {
        internal static CaptureAnnotationStyle Default { get; } = new CaptureAnnotationStyle(Color.FromRgb(222, 66, 74), 2.5, 14, false);

        internal CaptureAnnotationStyle(Color color, double strokeWidth, double fontSize, bool filled)
        {
            Color = color;
            StrokeWidth = strokeWidth;
            FontSize = fontSize;
            Filled = filled;
        }

        internal Color Color { get; }
        internal double StrokeWidth { get; }
        internal double FontSize { get; }
        internal bool Filled { get; }
    }

    internal sealed class CaptureAnnotationOptions : Border
    {
        private readonly Button[] widths = new Button[3];
        private readonly Button[] fontSizes = new Button[3];
        private readonly Button[] colors = new Button[6];
        private readonly WrapPanel row = new WrapPanel();
        private readonly StackPanel widthGroup = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly StackPanel fontGroup = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Border fillSample;
        private readonly Button fill;
        private CaptureAnnotationStyle style;

        private static readonly double[] StrokeWidths = { 1.5, 2.5, 5 };
        private static readonly double[] FontSizes = { 11, 14, 22 };
        private static readonly Color[] PaletteColors =
        {
            Color.FromRgb(14, 174, 243), Color.FromRgb(141, 213, 0), Color.FromRgb(255, 192, 0),
            Color.FromRgb(70, 70, 70), Colors.White, CaptureAnnotationStyle.Default.Color
        };

        internal event Action<CaptureAnnotationStyle> Changed;

        internal CaptureAnnotationOptions()
        {
            Background = Brushes.White;
            BorderBrush = Outline;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(7);
            Padding = new Thickness(4, 3, 4, 3);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            AutomationProperties.SetName(this, "标注选项");

            Child = row;
            row.Children.Add(widthGroup);
            row.Children.Add(fontGroup);
            for (int index = 0; index < widths.Length; index++)
            {
                double width = StrokeWidths[index];
                string label = "细中粗"[index] + "线条";
                double markSize = 4 + index * 3;
                var dot = new Ellipse { Width = markSize, Height = markSize };
                widths[index] = AddChoice(widthGroup, label, dot, () =>
                    Change(new CaptureAnnotationStyle(style.Color, width, style.FontSize, style.Filled)));

                double size = FontSizes[index];
                var glyph = new TextBlock { Text = "小中大"[index].ToString(), FontSize = 12, FontFamily = new FontFamily("Microsoft YaHei UI") };
                fontSizes[index] = AddChoice(fontGroup, glyph.Text + "字号", glyph, () =>
                    Change(new CaptureAnnotationStyle(style.Color, style.StrokeWidth, size, style.Filled)));
            }

            fillSample = new Border { CornerRadius = new CornerRadius(1), Margin = new Thickness(2) };
            var fillIcon = new Border
            {
                BorderBrush = Icon, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                Width = 14, Height = 14, Child = fillSample
            };
            fill = AddChoice(widthGroup, "实心填充", fillIcon, () =>
                Change(new CaptureAnnotationStyle(style.Color, style.StrokeWidth, style.FontSize, !style.Filled)));

            var palette = new StackPanel { Orientation = Orientation.Horizontal };
            palette.Children.Add(new Border
            {
                Width = 1, Height = 14, Margin = new Thickness(3, 0, 4, 0), Background = Divider,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(palette);
            string[] names = { "蓝色", "绿色", "黄色", "深灰色", "白色", "红色" };
            for (int index = 0; index < colors.Length; index++)
            {
                Color color = PaletteColors[index];
                var swatch = new Border
                {
                    Width = 15, Height = 15, CornerRadius = new CornerRadius(2),
                    Background = Paint(color.R, color.G, color.B),
                    BorderBrush = color == Colors.White ? Outline : Brushes.Transparent,
                    BorderThickness = new Thickness(1)
                };
                colors[index] = AddChoice(palette, names[index], swatch, () =>
                    Change(new CaptureAnnotationStyle(color, style.StrokeWidth, style.FontSize, style.Filled)));
            }
            Bind(CaptureTool.Rectangle, CaptureAnnotationStyle.Default);
        }

        internal static bool Supports(CaptureTool tool) => tool == CaptureTool.Rectangle || tool == CaptureTool.Arrow ||
            tool == CaptureTool.Pen || tool == CaptureTool.Text;

        internal void Bind(CaptureTool tool, CaptureAnnotationStyle value)
        {
            style = value;
            Visibility = Supports(tool) ? Visibility.Visible : Visibility.Collapsed;
            widthGroup.Visibility = tool == CaptureTool.Text ? Visibility.Collapsed : Visibility.Visible;
            fontGroup.Visibility = tool == CaptureTool.Text ? Visibility.Visible : Visibility.Collapsed;
            fill.Visibility = tool == CaptureTool.Rectangle ? Visibility.Visible : Visibility.Collapsed;
            RefreshChoices();
            // The floating card measures immediately after a tool switch to choose its screen bounds.
            widthGroup.InvalidateMeasure();
            fontGroup.InvalidateMeasure();
            row.InvalidateMeasure();
            InvalidateMeasure();
        }

        private static Button AddChoice(Panel group, string label, UIElement glyph, Action choose)
        {
            glyph.IsHitTestVisible = false;
            var button = new Button
            {
                Width = 22, Height = 23, Margin = new Thickness(0, 0, 1, 0), Content = glyph,
                Template = ButtonChrome, Focusable = false, Cursor = Cursors.Hand, ToolTip = label
            };
            AutomationProperties.SetName(button, label);
            button.Click += (sender, args) => choose();
            group.Children.Add(button);
            return button;
        }

        private void Change(CaptureAnnotationStyle value)
        {
            if (style.Equals(value)) return;
            style = value;
            RefreshChoices();
            Changed?.Invoke(value);
        }

        private void RefreshChoices()
        {
            for (int index = 0; index < widths.Length; index++)
            {
                bool selectedWidth = StrokeWidths[index] == style.StrokeWidth;
                SetChoice(widths[index], selectedWidth);
                ((Ellipse)widths[index].Content).Fill = selectedWidth ? Accent : Icon;
                bool selectedSize = FontSizes[index] == style.FontSize;
                SetChoice(fontSizes[index], selectedSize);
                ((TextBlock)fontSizes[index].Content).Foreground = selectedSize ? Accent : Icon;
            }
            for (int index = 0; index < colors.Length; index++) SetChoice(colors[index], PaletteColors[index] == style.Color);
            SetChoice(fill, style.Filled);
            ((Border)fill.Content).BorderBrush = style.Filled ? Accent : Icon;
            fillSample.Background = style.Filled ? Accent : Brushes.Transparent;
        }

        private static void SetChoice(Button button, bool selected)
        {
            button.Tag = selected;
            AutomationProperties.SetItemStatus(button, selected ? "已选中" : "未选中");
        }
    }
}
