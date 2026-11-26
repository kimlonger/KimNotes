using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Settings
{
    internal static class CommunityGroup
    {
        internal static TextBlock CreateEntry(Window owner)
        {
            var popup = new Popup { Placement = PlacementMode.Top, AllowsTransparency = true, StaysOpen = false };
            var link = FeatureWindow.Link("q群 1135382915", () =>
            {
                if (popup.Child == null) popup.Child = CreateCard();
                popup.IsOpen = true;
            });
            AutomationProperties.SetHelpText(link, "打开小羊工具箱群二维码");
            var entry = new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center };
            popup.PlacementTarget = entry;
            popup.VerticalOffset = -3;
            link.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Escape || !popup.IsOpen) return;
                args.Handled = true;
                popup.IsOpen = false;
            };
            owner.LocationChanged += (sender, args) => popup.IsOpen = false;
            owner.Deactivated += (sender, args) => popup.IsOpen = false;
            owner.Closed += (sender, args) => popup.IsOpen = false;
            popup.Closed += (sender, args) => popup.Child = null;
            return entry;
        }

        private static Border CreateCard()
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/KimNotes.WpfPreview;component/Resources/CommunityGroupQr.jpg");
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            var qr = new Image { Width = 190, Height = 190, Source = image };
            AutomationProperties.SetName(qr, "小羊工具箱 QQ 群二维码");
            RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.HighQuality);
            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(4), Child = qr };
            card.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Surface);
            card.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            return card;
        }
    }
}
