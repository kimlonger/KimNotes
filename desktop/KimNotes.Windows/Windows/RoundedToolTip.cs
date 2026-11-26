using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KimNotes.Windows.Presentation
{
    internal sealed class RoundedToolTip : ToolTip
    {
        private const double Gap = 4;
        private static readonly ControlTemplate RoundedTemplate = CreateTemplate();

        public RoundedToolTip()
        {
            Template = RoundedTemplate;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 12;
            Padding = new Thickness(8, 4, 8, 4);
            BorderThickness = new Thickness(1);
            UseLayoutRounding = true;
            Placement = PlacementMode.Custom;
            CustomPopupPlacementCallback = PlaceBelow;
        }

        private static CustomPopupPlacement[] PlaceBelow(Size popup, Size target, Point offset)
        {
            double x = (target.Width - popup.Width) / 2 + offset.X;
            return new[]
            {
                new CustomPopupPlacement(new Point(x, target.Height + Gap + offset.Y), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(x, -popup.Height - Gap + offset.Y), PopupPrimaryAxis.Horizontal)
            };
        }

        private static ControlTemplate CreateTemplate()
        {
            var surface = new FrameworkElementFactory(typeof(Border), "TipSurface");
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            surface.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            surface.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            surface.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            return new ControlTemplate(typeof(ToolTip)) { VisualTree = surface };
        }
    }
}
