using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Notes
{
    internal sealed class NoteQuickPanel : Border
    {
        private const double PaletteHeight = 29;
        private const double RowHeight = 30;
        private const double TopRadius = 7;
        private const string RowHover = "NoteQuickPanel.RowHover";
        private const string RowInk = "NoteQuickPanel.RowInk";
        private const string RuleInk = "NoteQuickPanel.RuleInk";
        private static readonly Style RowStyle = CreateRowStyle();
        private static readonly ControlTemplate ButtonTemplate = CreateButtonTemplate();
        private static readonly ControlTemplate SwatchTemplate = CreateSwatchTemplate();
        private static readonly Geometry NoteListIcon = Geometry.Parse("M5,4 H19 V20 H5 Z M8.5,8 H15.5 M8.5,12 H15.5 M8.5,16 H13");
        private static readonly Geometry SettingsIcon = CreateGear();

        private readonly Dictionary<int, Button> swatches = new Dictionary<int, Button>();
        private readonly Path[] icons;

        internal NoteQuickPanel(Action<int> selectTheme, Action showNotes, Action showSettings)
        {
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = Brushes.White;
            Resources[RowHover] = Paint(242, 246, 251);
            Resources[RowInk] = Paint(43, 47, 54);
            Resources[RuleInk] = Paint(227, 232, 239);
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Cycle);

            var content = new StackPanel();
            var palette = new Grid { Name = "ColorPalette", Height = PaletteHeight };
            // Existing notes can still render the illustrations that are no longer offered here.
            var orderedThemes = NoteTheme.All.Where(theme => theme.Id is not (7 or 8))
                .OrderBy(theme => NoteThemeBrushes.Body(theme) is not SolidColorBrush)
                .ThenBy(NoteThemeBrushes.HasArtwork);
            foreach (var theme in orderedThemes)
            {
                int index = palette.ColumnDefinitions.Count;
                palette.ColumnDefinitions.Add(new ColumnDefinition());
                var title = NoteThemeBrushes.Name(theme);
                var swatch = new Button
                {
                    Template = SwatchTemplate, Cursor = Cursors.Hand, Background = NoteThemeBrushes.Body(theme),
                    Foreground = NoteThemeBrushes.Text(theme), FontFamily = new FontFamily("Segoe UI"), FontSize = 14.67,
                    FocusVisualStyle = null,
                    ToolTip = new RoundedToolTip
                    {
                        Content = title, Placement = PlacementMode.Top, VerticalOffset = -3
                    }
                };
                ToolTipService.SetInitialShowDelay(swatch, 160);
                AutomationProperties.SetName(swatch, title);
                swatch.Click += (sender, args) => selectTheme(theme.Id);
                swatches.Add(theme.Id, swatch);
                Grid.SetColumn(swatch, index);
                palette.Children.Add(swatch);
            }
            content.Children.Add(palette);
            content.Children.Add(CreateRule());

            var actionRows = new StackPanel { Margin = new Thickness(4) };
            var actions = new[]
            {
                (Label: "便签列表", Run: showNotes, Icon: NoteListIcon),
                (Label: "设置", Run: showSettings, Icon: SettingsIcon)
            };
            icons = new Path[actions.Length];
            for (int index = 0; index < actions.Length; index++)
            {
                var action = actions[index];
                var row = new Grid();
                var icon = new Path
                {
                    Data = action.Icon, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
                };
                icons[index] = icon;
                var viewBox = new Viewbox
                {
                    Width = 18.25, Height = 18.25, Margin = new Thickness(11.875, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Child = new Canvas { Width = 24, Height = 24, Children = { icon } }
                };
                row.Children.Add(viewBox);
                row.Children.Add(new TextBlock
                {
                    Text = action.Label, Margin = new Thickness(38, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                });
                var button = new Button { Style = RowStyle, Template = ButtonTemplate, Height = RowHeight, Content = row };
                AutomationProperties.SetName(button, action.Label);
                button.Click += (sender, args) => action.Run();
                actionRows.Children.Add(button);
            }
            content.Children.Add(actionRows);

            var bottomRule = CreateRule();
            bottomRule.VerticalAlignment = VerticalAlignment.Bottom;
            Child = new Grid { Children = { content, bottomRule } };
        }

        internal void ApplyTheme(int themeId)
        {
            var theme = NoteTheme.All.Find(option => option.Id == themeId) ?? NoteTheme.Current();
            Background = NoteThemeBrushes.Surface(theme);
            Resources[RowHover] = NoteThemeBrushes.HasArtwork(theme) ? NoteThemeBrushes.Chrome(theme) : NoteThemeBrushes.Body(theme);
            var colors = NoteThemeBrushes.CardColors(theme);
            Resources[RowInk] = colors.Text;
            Resources[RuleInk] = colors.Divider;
            foreach (var swatch in swatches)
            {
                swatch.Value.Content = swatch.Key == theme.Id ? "✓" : string.Empty;
                var tip = (ToolTip)swatch.Value.ToolTip;
                tip.Background = Background;
                tip.Foreground = colors.Text;
                tip.BorderBrush = colors.Divider;
            }
            foreach (var icon in icons) icon.Stroke = colors.Icon;
        }

        private static Border CreateRule()
        {
            var line = new Border { Height = 1, Opacity = 0.65, IsHitTestVisible = false };
            line.SetResourceReference(BackgroundProperty, RuleInk);
            return line;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            double width = sizeInfo.NewSize.Width, height = sizeInfo.NewSize.Height;
            double radius = Math.Min(TopRadius, Math.Min(width / 2, height));
            if (radius <= 0) return;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var arc = new Size(radius, radius);
                context.BeginFigure(new Point(radius, 0), true, true);
                context.LineTo(new Point(width - radius, 0), true, false);
                context.ArcTo(new Point(width, radius), arc, 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(width, height), true, false);
                context.LineTo(new Point(0, height), true, false);
                context.LineTo(new Point(0, radius), true, false);
                context.ArcTo(new Point(radius, 0), arc, 0, false, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            Clip = geometry;
        }

        private static Geometry CreateGear()
        {
            Point Tooth(int index)
            {
                double angle = index * Math.PI / 16;
                double radius = index % 4 < 2 ? 8 : 6.4;
                return new Point(12 + Math.Cos(angle) * radius, 12 + Math.Sin(angle) * radius);
            }
            var outline = new StreamGeometry();
            using (var context = outline.Open())
            {
                context.BeginFigure(Tooth(0), false, true);
                for (int index = 1; index < 32; index++) context.LineTo(Tooth(index), true, false);
            }
            var gear = new GeometryGroup { Children = { outline, new EllipseGeometry(new Point(12, 12), 3, 3) } };
            gear.Freeze();
            return gear;
        }

        private static Style CreateRowStyle() => new Style(typeof(Button))
        {
            Setters =
            {
                new Setter(Control.BackgroundProperty, Brushes.Transparent), new Setter(Control.ForegroundProperty, new DynamicResourceExtension(RowInk)),
                new Setter(Control.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")), new Setter(Control.FontSizeProperty, 12.67),
                new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch), new Setter(CursorProperty, Cursors.Hand),
                new Setter(FocusVisualStyleProperty, null)
            },
            Triggers =
            {
                new Trigger { Property = IsMouseOverProperty, Value = true,
                    Setters = { new Setter(Control.BackgroundProperty, new DynamicResourceExtension(RowHover)) } },
                new Trigger { Property = IsKeyboardFocusedProperty, Value = true,
                    Setters = { new Setter(Control.BackgroundProperty, new DynamicResourceExtension(RowHover)) } }
            }
        };

        private static ControlTemplate CreateButtonTemplate()
        {
            var surface = new FrameworkElementFactory(typeof(Border), "Surface");
            surface.SetValue(CornerRadiusProperty, new CornerRadius(5));
            surface.SetValue(BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            surface.AppendChild(presenter);
            return new ControlTemplate(typeof(Button)) { VisualTree = surface };
        }

        private static ControlTemplate CreateSwatchTemplate()
        {
            var target = new FrameworkElementFactory(typeof(Grid), "SwatchFill");
            target.SetValue(Panel.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var outline = new FrameworkElementFactory(typeof(Border), "Outline");
            outline.SetValue(MarginProperty, new Thickness(1));
            outline.SetValue(CornerRadiusProperty, new CornerRadius(3));
            outline.SetValue(BorderThicknessProperty, new Thickness(1));
            outline.SetValue(IsHitTestVisibleProperty, false);
            target.AppendChild(outline);
            var mark = new FrameworkElementFactory(typeof(ContentPresenter));
            mark.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(IsHitTestVisibleProperty, false);
            target.AppendChild(mark);

            var highlight = Paint(116, 132, 153);
            return new ControlTemplate(typeof(Button))
            {
                VisualTree = target,
                Triggers =
                {
                    new Trigger { Property = IsMouseOverProperty, Value = true,
                        Setters = { new Setter(BorderBrushProperty, highlight, "Outline") } },
                    new Trigger { Property = IsKeyboardFocusedProperty, Value = true,
                        Setters = { new Setter(BorderBrushProperty, highlight, "Outline") } }
                }
            };
        }

        private static Brush Paint(byte r, byte g, byte b, byte alpha = 255)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
