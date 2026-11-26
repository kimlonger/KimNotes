using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace KimNotes.Windows.Presentation
{
    internal class CardWindow : Window
    {
        internal Grid Body { get; } = new Grid();
        internal static readonly Brush Divider = Brush("#CDD8E4");
        internal static readonly Brush Quiet = Brush("#607083");
        internal static readonly Brush Ink = Brush("#2B2F36");
        internal static readonly Brush Light = Brush("#EAF0F7");
        internal static readonly Brush Accent = Brush("#4479B1");
        private readonly TitleLogo titleLogo;
        private readonly TextBlock caption;

        protected CardWindow(string title)
        {
            DesktopCardTheme.Ensure();
            Title = title;
            Width = 301; Height = 401; MinWidth = 285; MinHeight = 260;
            AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            ShowInTaskbar = KimNotes.utils.InitConfig.ShowTaskbarIcons;
            new NoteWindowChrome(this);

            var layout = new DockPanel();
            var header = new Border
            {
                Height = 31, CornerRadius = new CornerRadius(7, 7, 0, 0)
            };
            header.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Chrome);
            header.MouseLeftButtonDown += (sender, args) =>
            {
                if (args.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            var titleBar = new DockPanel { Margin = new Thickness(12, 0, 6, 0) };
            var close = TitleCloseButton.Create(Close);
            close.SetResourceReference(ForegroundProperty, DesktopCardTheme.Icon);
            DockPanel.SetDock(close, Dock.Right);
            titleBar.Children.Add(close);
            titleLogo = new TitleLogo { Width = 18, Height = 18, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(titleLogo, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(titleLogo, Dock.Left);
            titleBar.Children.Add(titleLogo);
            caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.67, Margin = new Thickness(7, 0, 0, 0) };
            caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
            titleBar.Children.Add(caption);
            header.Child = titleBar;
            layout.Children.Add(Body);
            var surface = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Child = layout
            };
            surface.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Surface);
            surface.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            Content = surface;
        }

        protected void SetHeaderStyle(bool branded = true)
        {
            titleLogo.Visibility = branded ? Visibility.Visible : Visibility.Collapsed;
            caption.Margin = branded ? new Thickness(7, 0, 0, 0) : new Thickness(0);
        }

        internal static Brush Brush(string color)
        {
            var solid = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(color);
            solid.Freeze();
            return solid;
        }

        internal static Button ActionButton(string text, string tooltip = null)
        {
            var button = new Button
            {
                Content = text, ToolTip = tooltip ?? text, Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left, Height = 27
            };
            button.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Field);
            button.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Line);
            button.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Ink);
            return button;
        }
    }
}
