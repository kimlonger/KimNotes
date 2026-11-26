using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KimNotes.utils;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Settings
{
    internal abstract class FeatureWindow : Window
    {
        internal static readonly Brush Ink = Paint(43, 47, 54);
        internal static readonly Brush Muted = Paint(104, 115, 129);
        internal static readonly Brush Accent = Paint(59, 108, 160);
        internal static readonly Brush Line = Paint(205, 216, 228);
        internal static readonly Brush Surface = Paint(245, 248, 251);
        internal static readonly Brush Warning = Paint(162, 63, 53);
        private static readonly Style ButtonStyle = CreateButtonStyle();
        private readonly StackPanel captionButtons;
        protected readonly ContentControl Body;

        protected FeatureWindow(string title, double width, double height)
        {
            DesktopCardTheme.Ensure();
            Title = title;
            Width = width;
            Height = height;
            MinWidth = Math.Min(width, 280);
            MinHeight = Math.Min(height, 240);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 12;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            ShowInTaskbar = InitConfig.ShowTaskbarIcons;
            Icon = BitmapIcon();

            var layout = new DockPanel();
            var titleBar = new DockPanel { Height = 31, LastChildFill = true, Margin = new Thickness(8, 0, 6, 0) };
            var header = new Border { CornerRadius = new CornerRadius(7, 7, 0, 0), Child = titleBar };
            header.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Chrome);
            header.MouseLeftButtonDown += (sender, args) =>
            {
                if (!args.Handled && args.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);

            captionButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(captionButtons, Dock.Right);
            titleBar.Children.Add(captionButtons);
            var close = TitleCloseButton.Create(Close);
            close.SetResourceReference(ForegroundProperty, DesktopCardTheme.Icon);
            captionButtons.Children.Add(close);
            var logo = new TitleLogo { Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0), IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(logo, Dock.Left);
            titleBar.Children.Add(logo);
            var caption = Text(title);
            caption.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
            titleBar.Children.Add(caption);

            Body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            layout.Children.Add(Body);
            var surface = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = layout
            };
            surface.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Surface);
            surface.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            Content = surface;
            new NoteWindowChrome(this);
        }

        protected Button AddCaptionButton(string glyph, string name, Action click)
        {
            var button = Button(glyph, click);
            button.Width = 25;
            button.Height = 24;
            button.Padding = new Thickness(0);
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            button.FontSize = 17;
            button.ToolTip = name;
            AutomationProperties.SetName(button, name);
            captionButtons.Children.Insert(Math.Max(0, captionButtons.Children.Count - 1), button);
            return button;
        }

        internal static Button Button(string label, Action click, bool primary = false)
        {
            var button = new Button
            {
                Content = label, Style = ButtonStyle, Height = 29, Padding = new Thickness(9, 0, 9, 0)
            };
            button.SetResourceReference(Control.BorderBrushProperty, primary ? DesktopCardTheme.Accent : DesktopCardTheme.Line);
            button.SetResourceReference(Control.BackgroundProperty, primary ? DesktopCardTheme.Accent : DesktopCardTheme.Field);
            button.SetResourceReference(Control.ForegroundProperty, primary ? DesktopCardTheme.PrimaryInk : DesktopCardTheme.Ink);
            if (click != null) button.Click += (sender, args) => click();
            return button;
        }

        internal static Hyperlink Link(string label, Action click)
        {
            var link = new Hyperlink(new Run(label)) { Cursor = Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, DesktopCardTheme.Accent);
            link.Click += (sender, args) => click();
            return link;
        }

        internal static TextBlock Text(string value, Brush brush = null)
        {
            var text = new TextBlock { Text = value, Foreground = brush ?? Ink, VerticalAlignment = VerticalAlignment.Center };
            var resource = InkResource(brush);
            if (resource != null) text.SetResourceReference(TextBlock.ForegroundProperty, resource);
            return text;
        }

        internal static Viewbox VectorIcon(string path, Brush brush = null, double size = 18, string themedStroke = null)
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(path), Stroke = brush ?? Ink, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
            };
            var resource = themedStroke ?? InkResource(brush);
            if (resource != null) icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, resource);
            canvas.Children.Add(icon);
            return new Viewbox { Child = canvas, Width = size, Height = size, IsHitTestVisible = false };
        }

        private static string InkResource(Brush brush)
        {
            if (brush == null || brush == Ink) return DesktopCardTheme.Ink;
            if (brush == Accent) return DesktopCardTheme.Accent;
            if (brush == Muted) return DesktopCardTheme.Icon;
            if (brush == Warning) return DesktopCardTheme.Danger;
            return null;
        }

        protected static void StyleInput(Control input)
        {
            input.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Field);
            input.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Line);
            input.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
        }

        internal static ScrollViewer Scroll(UIElement child)
        {
            return new ScrollViewer
            {
                Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        internal static BitmapSource Avatar(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using (var stream = new System.IO.MemoryStream(bytes, false))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 96;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception error) when (error is System.IO.IOException || error is NotSupportedException || error is ArgumentException || error is FormatException)
            {
                return null;
            }
        }

        internal static string OpenWebsite(string relativePath)
        {
            if (DesktopEndpoints.WebsiteRoot == null) return DesktopEndpoints.MissingMessage;
            try
            {
                Process.Start(new ProcessStartInfo(new Uri(DesktopEndpoints.WebsiteRoot, relativePath).AbsoluteUri) { UseShellExecute = true });
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "无法打开默认浏览器。";
            }
        }

        private static System.Windows.Media.Imaging.BitmapFrame BitmapIcon()
        {
            var name = typeof(FeatureWindow).Assembly.GetName().Name;
            return System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/" + name + ";component/Sheep.ico"));
        }

        private static Brush Paint(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static Style CreateButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            var border = new FrameworkElementFactory(typeof(Border), "Outline");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent), "Outline"));
            template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent), "Outline"));
            template.Triggers.Add(focus);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.8, "Outline"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "Outline"));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Seal();
            return style;
        }
    }
}
