using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KimNotes.Windows.Presentation;
using ShapePath = System.Windows.Shapes.Path;

namespace KimNotes.Windows.Notes
{
    internal sealed class NoteToolsPopup : Popup
    {
        private const double TileWidth = 62;
        private const double PointerWidth = 10;
        private const double PointerInset = 8;
        private const double AnchorGap = 2;

        internal Button FileTransfer { get; }

        internal NoteToolsPopup(FrameworkElement anchor, Action openTransfer)
        {
            PlacementTarget = anchor;
            Placement = PlacementMode.Custom;
            CustomPopupPlacementCallback = PlaceByAnchor;
            AllowsTransparency = true;
            StaysOpen = true;

            FileTransfer = CreateTile(openTransfer);
            var tile = new Border
            {
                Width = TileWidth, Height = 52,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 5, 0, 5), Child = FileTransfer
            };
            tile.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Surface);
            tile.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            AutomationProperties.SetName(tile, "桌面工具");
            var pointer = CreatePointer();
            var panel = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
            DesktopCardTheme.UseOn(panel);
            panel.Children.Add(tile);
            panel.Children.Add(pointer);
            Child = panel;

            Opened += (sender, args) =>
            {
                if (PresentationSource.FromVisual(panel) == null || PresentationSource.FromVisual(anchor) == null) return;
                var center = panel.PointFromScreen(anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2)));
                bool below = center.Y <= panel.ActualHeight / 2;
                pointer.VerticalAlignment = below ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                pointer.RenderTransform = below ? Transform.Identity : new ScaleTransform(1, -1);
                double rightLimit = panel.ActualWidth - PointerWidth - PointerInset;
                double left = Math.Max(PointerInset, Math.Min(rightLimit, center.X - PointerWidth / 2));
                pointer.Margin = new Thickness(left, 0, 0, 0);
            };
        }

        private static CustomPopupPlacement[] PlaceByAnchor(Size popup, Size anchor, Point offset)
        {
            double x = (anchor.Width - popup.Width) / 2 + offset.X;
            return new[]
            {
                new CustomPopupPlacement(new Point(x, anchor.Height + AnchorGap + offset.Y), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(x, -popup.Height - AnchorGap + offset.Y), PopupPrimaryAxis.Horizontal)
            };
        }

        private static ShapePath CreatePointer()
        {
            var pointer = new ShapePath
            {
                // Leave the base open so the fill covers the tile border at the join.
                Data = Geometry.Parse("M0,6 L4.5,0 9,6"),
                Width = PointerWidth, Height = 7, StrokeThickness = 1,
                Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, RenderTransformOrigin = new Point(0.5, 0.5),
                Margin = new Thickness((TileWidth - PointerWidth) / 2, 0, 0, 0), IsHitTestVisible = false
            };
            pointer.SetResourceReference(ShapePath.FillProperty, DesktopCardTheme.Surface);
            pointer.SetResourceReference(ShapePath.StrokeProperty, DesktopCardTheme.Line);
            return pointer;
        }

        private static Button CreateTile(Action open)
        {
            var symbol = new ShapePath
            {
                Data = Geometry.Parse("M3,8 H20 M16,4 L20,8 16,12 M21,16 H4 M8,12 L4,16 8,20"),
                Width = 15, Height = 15, Stretch = Stretch.Uniform,
                StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            var icon = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
                Child = symbol, HorizontalAlignment = HorizontalAlignment.Center
            };
            var label = new TextBlock
            {
                Text = "文件互传", FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
            };
            var content = new StackPanel { IsHitTestVisible = false };
            content.Children.Add(icon);
            content.Children.Add(label);
            var hitArea = new FrameworkElementFactory(typeof(Border));
            hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            hitArea.AppendChild(presenter);
            var button = new Button
            {
                Content = content, Cursor = Cursors.Hand, FocusVisualStyle = null,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = hitArea }
            };
            AutomationProperties.SetName(button, label.Text);
            void UpdateTile()
            {
                bool active = button.IsMouseOver || button.IsKeyboardFocused;
                icon.SetResourceReference(Border.BackgroundProperty, active ? DesktopCardTheme.Tint : DesktopCardTheme.Field);
                symbol.SetResourceReference(ShapePath.StrokeProperty, active ? DesktopCardTheme.Accent : DesktopCardTheme.Icon);
                label.SetResourceReference(TextBlock.ForegroundProperty, active ? DesktopCardTheme.Accent : DesktopCardTheme.Ink);
            }
            UpdateTile();
            button.MouseEnter += (sender, args) => UpdateTile();
            button.MouseLeave += (sender, args) => UpdateTile();
            button.IsKeyboardFocusedChanged += (sender, args) => UpdateTile();
            button.Click += (sender, args) => open();
            return button;
        }
    }
}
