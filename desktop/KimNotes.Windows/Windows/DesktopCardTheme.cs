using System.Linq;
using System.Windows;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;

namespace KimNotes.Windows.Presentation
{
    internal static class DesktopCardTheme
    {
        internal const string Chrome = "CardTheme.Chrome";
        internal const string Surface = "CardTheme.Surface";
        internal const string Field = "CardTheme.Field";
        internal const string SearchSurface = "CardTheme.SearchSurface";
        internal const string SearchBorder = "CardTheme.SearchBorder";
        internal const string Tint = "CardTheme.Tint";
        internal const string Ink = "CardTheme.Ink";
        internal const string Icon = "CardTheme.Icon";
        internal const string Line = "CardTheme.Line";
        internal const string Accent = "CardTheme.Accent";
        internal const string PrimaryInk = "CardTheme.PrimaryInk";
        internal const string Danger = "CardTheme.Danger";
        internal const string DangerTint = "CardTheme.DangerTint";
        internal const string Caution = "CardTheme.Caution";
        internal const string CautionTint = "CardTheme.CautionTint";
        internal const string LogoWool = "CardTheme.LogoWool";
        internal const string LogoOutline = "CardTheme.LogoOutline";

        private static readonly ResourceDictionary Palette = new ResourceDictionary();
        private static NoteTheme current;
        internal static NoteTheme Current => current ?? NoteTheme.Current();

        internal static void Ensure() => Apply(Current.Id);

        internal static void UseOn(FrameworkElement element)
        {
            Ensure();
            var resources = element.Resources.MergedDictionaries;
            if (!resources.Contains(Palette)) resources.Add(Palette);
        }

        internal static void Apply(int id)
        {
            var app = Application.Current;
            if (app == null || current?.Id == id) return;
            var theme = NoteTheme.All.FirstOrDefault(option => option.Id == id) ?? NoteTheme.Current();
            var colors = NoteThemeBrushes.CardColors(theme);
            bool dark = NoteThemeBrushes.IsDark(theme);
            var tint = Solid(Mix(colors.BaseColor, DrawingColor.White, dark ? 0.12 : 0.15));
            var surface = NoteThemeBrushes.Surface(theme);
            var logo = NoteThemeBrushes.LogoColors(theme);
            var searchTone = dark ? ToDrawingColor(((SolidColorBrush)colors.Accent).Color) : DrawingColor.White;
            var searchFill = Mix(colors.BaseColor, searchTone, dark ? 0.17 : 0.78);
            var searchEdge = Mix(searchFill, ToDrawingColor(((SolidColorBrush)colors.Icon).Color), dark ? 0.53 : 0.45);
            var next = new ResourceDictionary
            {
                [Chrome] = NoteThemeBrushes.Chrome(theme),
                [Surface] = surface,
                [Field] = Solid(Mix(colors.BaseColor, DrawingColor.White, dark ? 0.06 : 0.5)),
                [SearchSurface] = Solid(searchFill),
                [SearchBorder] = Solid(searchEdge),
                [Tint] = tint,
                [Ink] = colors.Text,
                [Icon] = colors.Icon,
                [Line] = colors.Divider,
                [Accent] = colors.Accent,
                [PrimaryInk] = dark ? Solid(DrawingColor.FromArgb(24, 35, 52)) : Brushes.White,
                [Danger] = Solid(dark ? DrawingColor.FromArgb(255, 183, 176) : DrawingColor.FromArgb(174, 59, 52)),
                [DangerTint] = Solid(dark ? DrawingColor.FromArgb(75, 47, 54) : DrawingColor.FromArgb(252, 235, 233)),
                [Caution] = Solid(dark ? DrawingColor.FromArgb(255, 210, 154) : DrawingColor.FromArgb(172, 91, 16)),
                [CautionTint] = Solid(dark ? DrawingColor.FromArgb(69, 54, 45) : DrawingColor.FromArgb(252, 240, 226)),
                [LogoWool] = dark ? logo.Wool : tint,
                [LogoOutline] = dark ? logo.Outline : colors.Text,
                ["TraySurface"] = surface,
                ["TrayText"] = colors.Text,
                ["TrayBorder"] = colors.Divider,
                ["TrayHover"] = tint
            };
            if (Palette.MergedDictionaries.Count == 0)
            {
                Palette.MergedDictionaries.Add(next);
                app.Resources.MergedDictionaries.Add(Palette);
            }
            else Palette.MergedDictionaries[0] = next;
            current = theme;
        }

        private static DrawingColor Mix(DrawingColor first, DrawingColor second, double weight)
        {
            int Channel(int original, int target) => (int)(original + (target - original) * weight);
            return DrawingColor.FromArgb(Channel(first.R, second.R), Channel(first.G, second.G), Channel(first.B, second.B));
        }

        private static DrawingColor ToDrawingColor(Color color) => DrawingColor.FromArgb(color.R, color.G, color.B);

        private static Brush Solid(DrawingColor color)
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }
}
