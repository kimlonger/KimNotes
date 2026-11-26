using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KimNotes.Windows.Presentation;

namespace KimNotes.Windows.Runtime
{
    internal static class DesktopTrayMenu
    {
        private static readonly ResourceDictionary Styles = new ResourceDictionary
        {
            Source = new Uri("/KimNotes.WpfPreview;component/Themes/DesktopTrayMenu.xaml", UriKind.Relative)
        };

        internal static ContextMenu Create(params (string Label, Action Run)[] actions) => CreateGrouped(actions);

        internal static ContextMenu CreateGrouped(params (string Label, Action Run)[][] groups)
        {
            var menu = new ContextMenu { StaysOpen = false, Placement = PlacementMode.MousePoint, Style = (Style)Styles["TrayPopup"] };
            DesktopCardTheme.UseOn(menu);
            foreach (var group in groups)
            {
                if (group.Length == 0) continue;
                if (menu.Items.Count > 0)
                    menu.Items.Add(new Separator { Style = (Style)Styles["TraySeparator"] });
                foreach (var action in group)
                {
                    var item = new MenuItem { Header = action.Label, Style = (Style)Styles["TrayCommand"] };
                    item.Click += (sender, args) => action.Run();
                    menu.Items.Add(item);
                }
            }
            return menu;
        }
    }
}
