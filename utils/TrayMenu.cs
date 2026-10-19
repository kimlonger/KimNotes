using System;
using System.Drawing;
using System.Windows.Forms;

namespace KimNotes
{
    internal sealed class TrayMenu : ContextMenuStrip
    {
        private readonly Font menuFont = new Font("Microsoft YaHei UI", 9f);

        internal TrayMenu()
        {
            Font = menuFont;
            ShowImageMargin = false;
            ShowCheckMargin = false;
            Padding = new Padding(3);
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = true };
        }

        internal void AddItem(string text, Action action, bool separatorAfter = false)
        {
            var item = new ToolStripMenuItem(text) { Padding = new Padding(10, 3, 16, 3) };
            item.Click += (s, e) => action();
            Items.Add(item);
            if (separatorAfter) Items.Add(new ToolStripSeparator());
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.FromArgb(250, 250, 250);
            public override Color MenuBorder => Color.FromArgb(219, 219, 219);
            public override Color MenuItemBorder => Color.FromArgb(235, 235, 235);
            public override Color MenuItemSelected => Color.FromArgb(235, 235, 235);
            public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
            public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
            public override Color SeparatorDark => Color.FromArgb(220, 220, 220);
            public override Color SeparatorLight => Color.FromArgb(250, 250, 250);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) menuFont.Dispose();
        }
    }
}
