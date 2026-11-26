using System.Windows.Controls;
using System.Windows.Input;
using KimNotes.Windows.Presentation;
using static KimNotes.Windows.Notes.NoteCatalogVisuals;

namespace KimNotes.Windows.Notes
{
    internal static class NoteCatalogMenu
    {
        internal static ContextMenu Create(Button anchor, ICommand open, ICommand versions, ICommand delete)
        {
            var menu = CardActionMenu.Create(anchor, "便签操作");
            menu.Items.Add(CardActionMenu.Item("打开便签", Icon(Open, size: 12), open));
            menu.Items.Add(CardActionMenu.Item("历史版本", Icon(History, size: 12), versions));
            var alert = CardActionMenu.DeleteInk;
            menu.Items.Add(CardActionMenu.Item("删除便签", Icon(Delete, alert, 12), delete, alert));
            return menu;
        }
    }
}
