using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;

namespace KimNotes.Windows.Presentation
{
    internal static class NoteThemeBrushes
    {
        private static readonly Brush RightBlend = CreateRightBlend();
        private static readonly ThemePalette[] Palettes =
        {
            // The corners run top-left, top-right, bottom-left, bottom-right.
            Gradient("晨雾蓝", 0xC5D9EE, new[] { 0xB9CEE9, 0xE1EAF5, 0xC9DDEC, 0xD1E4ED }, 0xB6CCE7, 0xDDE8F3,
                new Inks(0x2C3A55, 0x5B6A86, 0x355F92, 0xC0D6E9, barTint: 0x456CA1, barRule: 0x3C6499)),
            Gradient("蜜桃日落", 0xF4C8C9, new[] { 0xEFCBBB, 0xF4DEDA, 0xEDBFCE, 0xE9BFD6 }, 0xEECAB9, 0xF3DDDA,
                new Inks(0x4A2E33, 0x8A5F66, 0x935263, 0xE9B1C5)),
            Gradient("薄荷清晨", 0xCEE7D2, new[] { 0xBDE3CC, 0xE2F0E5, 0xD5E8CC, 0xE2EACD }, 0xBADFCB, 0xDFEDE2,
                new Inks(0x24423A, 0x557A6E, 0x386D5A, 0xCAE1C2)),
            Gradient("薰衣草", 0xDDC9EB, new[] { 0xCFC5E9, 0xEDE3F2, 0xE4C7E0, 0xEBD0DF }, 0xCCC3E8, 0xE8DFF0,
                new Inks(0x3A2F55, 0x6F6488, 0x69538D, 0xDEBADC)),
            Artwork("远山", "distant-mountains", 0xF5F3EB, 0xEAECE3, new Inks(0x353A34, 0x52675C, 0x466554, 0xDDDFD4)),
            Artwork("海雾", "sea-mist", 0xEDF4F1, 0xE1EEEB, new Inks(0x263A3D, 0x4D6970, 0x426876, 0xD3E2E0)),
            Artwork("云鲸", "cloud-whale", 0xDFE3F0, 0xD4DBEC, new Inks(0x303850, 0x505F80, 0x4C5B88, 0xCCD3E4)),
            LegacyColor("浅紫", 0xF4F0F5, 0xEBE6F0, new Inks(0x393345, 0x6D607C, 0x67517F, 0xE2DBEB)),
            LegacyColor("米白", 0xF6F3EB, 0xEFE8D9, new Inks(0x2D2722, 0x6D5946, 0x775C44, 0xE7DED0)),
            Artwork("暗夜", "night", 0x1B2435, 0x263449,
                new Inks(0xF5F8FD, 0xD7E2F4, 0xB7D3FC, 0x303D54, barTint: 0x0B1424, barRule: 0xA5BCD8),
                cards: (0x1D2738, new Inks(0xE9EEF7, 0xB9C9E1, 0xA8C7F5, 0x384961)), shade: NightShade(),
                logo: (0xFFF7E9, 0x51453D)),
            Classic(NoteTheme.All[10], 0x346B81),
            Classic(NoteTheme.All[11], 0x725513),
            Classic(NoteTheme.All[12], 0x2E6D54),
            Classic(NoteTheme.All[13], 0x9B4267)
        };

        internal static Brush Body(NoteTheme theme) => Palettes[theme.Id].Body;
        internal static Brush Chrome(NoteTheme theme) => Palettes[theme.Id].Chrome;
        internal static Brush EdgeChrome(NoteTheme theme) => Palettes[theme.Id].EdgeChrome;
        internal static Brush Surface(NoteTheme theme) => Palettes[theme.Id].Surface;
        internal static Brush SelectedBody(NoteTheme theme) => Palettes[theme.Id].Selected;
        internal static Brush Text(NoteTheme theme) => Palettes[theme.Id].Text;
        internal static Brush ChromeText(NoteTheme theme) => Palettes[theme.Id].ChromeText;
        internal static Brush Icon(NoteTheme theme) => Palettes[theme.Id].Icon;
        internal static Brush Accent(NoteTheme theme) => Palettes[theme.Id].Accent;
        internal static Brush Divider(NoteTheme theme) => Palettes[theme.Id].Divider;
        internal static string Name(NoteTheme theme) => Palettes[theme.Id].Name;
        internal static bool HasArtwork(NoteTheme theme) => Palettes[theme.Id].Body is ImageBrush;
        internal static bool IsDark(NoteTheme theme) => Palettes[theme.Id].BaseColor.GetBrightness() < 0.5f;
        internal static (Brush Wool, Brush Outline) LogoColors(NoteTheme theme) => Palettes[theme.Id].LogoColors;
        internal static (DrawingColor BaseColor, Brush Text, Brush Icon, Brush Accent, Brush Divider) CardColors(NoteTheme theme) =>
            Palettes[theme.Id].CardColors;

        internal static (Brush Title, Brush Tools, Brush TitleRule, Brush ToolsRule) NoteBars(NoteTheme theme) =>
            Palettes[theme.Id].NoteBars;

        internal static (Brush Hover, Brush Selected, Brush Pressed, Brush Focus, Brush Disabled) ToolStates(NoteTheme theme) =>
            Palettes[theme.Id].ToolStates;

        private static ThemePalette Gradient(string name, int baseColor, int[] corners, int chromeStart, int chromeEnd, Inks inks)
        {
            var sample = Rgb(baseColor);
            var stops = System.Array.ConvertAll(corners, Rgb);
            return new ThemePalette(name, sample, inks, FourCorners(stops),
                Linear(Rgb(chromeStart), Rgb(chromeEnd), new Point(1, 0)),
                FourCorners(System.Array.ConvertAll(stops, color => Mix(color, Colors.White, 0.56))),
                FourCorners(System.Array.ConvertAll(stops, color => Mix(color, Rgb(inks.Accent), 0.07))));
        }

        private static ThemePalette Classic(NoteTheme theme, int accent)
        {
            var body = Rgb(theme.Body.ToArgb());
            var bodyBrush = Solid(body);
            var chrome = Solid(Rgb(theme.Chrome.ToArgb()));
            var rule = Solid(Rgb(theme.Divider.ToArgb(), 152));
            var inks = new Inks(theme.Text.ToArgb(), theme.Icon.ToArgb(), accent, theme.Divider.ToArgb());
            return new ThemePalette(theme.Name, body, inks, bodyBrush, chrome,
                Solid(Mix(body, Colors.White, 0.32)), Solid(Mix(body, Rgb(accent), 0.10)),
                bars: (chrome, bodyBrush, rule, rule), chromeText: Solid(Rgb(theme.ChromeText.ToArgb())));
        }

        private static ThemePalette LegacyColor(string name, int baseColor, int chrome, Inks inks)
        {
            var body = Rgb(baseColor);
            return new ThemePalette(name, body, inks, Solid(body), Solid(Rgb(chrome)),
                Solid(Mix(body, Colors.White, 0.32)), Solid(Mix(body, Rgb(inks.Accent), 0.10)));
        }

        private static ThemePalette Artwork(string name, string file, int baseColor, int chrome, Inks inks,
            (int Base, Inks Inks)? cards = null, Brush shade = null, (int Wool, int Outline)? logo = null)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri("pack://application:,,,/KimNotes.WpfPreview;component/Resources/ThemeArt/" + file + ".jpg");
            image.EndInit();
            image.Freeze();

            var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            ImageSource source = image;
            if (shade != null)
            {
                var graded = new DrawingImage(new DrawingGroup
                {
                    Children =
                    {
                        new ImageDrawing(image, bounds),
                        new GeometryDrawing(shade, null, new RectangleGeometry(bounds))
                    }
                });
                graded.Freeze();
                source = graded;
            }

            var art = new ImageBrush(source) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Bottom };
            art.Freeze();

            var selectedArt = new DrawingBrush(new DrawingGroup
            {
                Children =
                {
                    new ImageDrawing(source, bounds),
                    new GeometryDrawing(Solid(Rgb(inks.Accent, 22)), null, new RectangleGeometry(bounds))
                }
            }) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Bottom };
            selectedArt.Freeze();

            var surface = cards.HasValue ? Rgb(cards.Value.Base) : Mix(Rgb(baseColor), Colors.White, 0.32);
            return new ThemePalette(name, Rgb(baseColor), inks, art, Solid(Rgb(chrome)), Solid(surface), selectedArt, cards, logo);
        }

        private static Brush NightShade()
        {
            // Subdue the brighter horizon so white text remains legible into the lower half.
            var shade = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Rgb(0, 0), 0),
                    new GradientStop(Rgb(0, 20), 0.25),
                    new GradientStop(Rgb(0, 50), 0.45),
                    new GradientStop(Rgb(0, 89), 0.60),
                    new GradientStop(Rgb(0, 118), 0.70),
                    new GradientStop(Rgb(0, 107), 0.78),
                    new GradientStop(Rgb(0, 43), 0.90),
                    new GradientStop(Rgb(0, 16), 1)
                }
            };
            shade.Freeze();
            return shade;
        }

        private static Brush FourCorners(Color[] stops)
        {
            // Stack the left and right top-to-bottom gradients and feather in the right side.
            // This retains both the pale upper corner and the lower color shift as the window resizes.
            var bounds = new Rect(0, 0, 1, 1);
            var face = new RectangleGeometry(bounds);
            var topToBottom = new Point(0, 1);
            var right = new DrawingGroup
            {
                OpacityMask = RightBlend,
                Children = { new GeometryDrawing(Linear(stops[1], stops[3], topToBottom), null, face) }
            };
            var drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(Linear(stops[0], stops[2], topToBottom), null, face),
                    right
                }
            };
            var brush = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill
            };
            brush.Freeze();
            return brush;
        }

        private static Brush CreateRightBlend()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(32, 0, 0, 0), 0.30),
                    new GradientStop(Color.FromArgb(118, 0, 0, 0), 0.62),
                    new GradientStop(Colors.Black, 1)
                }
            };
            brush.Freeze();
            return brush;
        }

        private static Brush Linear(Color start, Color end, Point direction)
        {
            var brush = new LinearGradientBrush(start, end, new Point(0, 0), direction);
            brush.Freeze();
            return brush;
        }

        private static Brush ArtworkBar(int tint, bool top)
        {
            var brush = new LinearGradientBrush(Rgb(tint, 19), Rgb(tint, 0),
                new Point(0, top ? 0 : 1), new Point(0, top ? 1 : 0));
            brush.Freeze();
            return brush;
        }

        private static Color Rgb(int hex) => Rgb(hex, byte.MaxValue);

        private static Color Rgb(int hex, byte alpha) =>
            Color.FromArgb(alpha, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

        private static Color Mix(Color first, Color second, double weight) => Color.FromRgb(
            (byte)(first.R + (second.R - first.R) * weight),
            (byte)(first.G + (second.G - first.G) * weight),
            (byte)(first.B + (second.B - first.B) * weight));

        private static Brush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private readonly struct Inks
        {
            internal Inks(int text, int icon, int accent, int divider, int? barTint = null, int? barRule = null)
            {
                Text = text;
                Icon = icon;
                Accent = accent;
                Divider = divider;
                BarTint = barTint ?? accent;
                BarRule = barRule ?? accent;
            }

            internal readonly int Text;
            internal readonly int Icon;
            internal readonly int Accent;
            internal readonly int Divider;
            internal readonly int BarTint;
            internal readonly int BarRule;
        }

        private sealed class ThemePalette
        {
            internal ThemePalette(string name, Color baseColor, Inks inks, Brush body, Brush chrome, Brush surface, Brush selected,
                (int Base, Inks Inks)? cards = null, (int Wool, int Outline)? logo = null,
                (Brush Title, Brush Tools, Brush TitleRule, Brush ToolsRule)? bars = null, Brush chromeText = null)
            {
                Name = name;
                BaseColor = DrawingColor.FromArgb(baseColor.R, baseColor.G, baseColor.B);
                Text = Solid(Rgb(inks.Text));
                ChromeText = chromeText ?? Text;
                Icon = Solid(Rgb(inks.Icon));
                Accent = Solid(Rgb(inks.Accent));
                Divider = Solid(Rgb(inks.Divider));
                Body = body;
                Chrome = chrome;
                EdgeChrome = cards.HasValue ? Solid(baseColor) : chrome;
                Surface = surface;
                Selected = selected;
                LogoColors = logo.HasValue
                    ? (Solid(Rgb(logo.Value.Wool)), Solid(Rgb(logo.Value.Outline)))
                    : (Solid(baseColor), Text);
                if (cards.HasValue)
                {
                    var color = Rgb(cards.Value.Base);
                    var card = cards.Value.Inks;
                    CardColors = (DrawingColor.FromArgb(color.R, color.G, color.B), Solid(Rgb(card.Text)),
                        Solid(Rgb(card.Icon)), Solid(Rgb(card.Accent)), Solid(Rgb(card.Divider)));
                }
                else CardColors = (BaseColor, Text, Icon, Accent, Divider);
                bool artwork = body is ImageBrush;
                var rule = Solid(Rgb(inks.BarRule, artwork ? (byte)29 : (byte)35));
                NoteBars = bars ?? (artwork
                    ? (ArtworkBar(inks.BarTint, true), ArtworkBar(inks.BarTint, false), rule, rule)
                    : (Solid(Rgb(inks.BarTint, 23)), Solid(Rgb(inks.BarTint, 18)), rule, rule));
                ToolStates = (Solid(Rgb(inks.Accent, 23)), Solid(Rgb(inks.Accent, 49)),
                    Solid(Rgb(inks.Accent, 44)), Solid(Rgb(inks.Accent, 112)), Solid(Rgb(inks.Icon, 155)));
            }

            internal string Name { get; }
            internal DrawingColor BaseColor { get; }
            internal Brush Text { get; }
            internal Brush ChromeText { get; }
            internal Brush Icon { get; }
            internal Brush Accent { get; }
            internal Brush Divider { get; }
            internal Brush Body { get; }
            internal Brush Chrome { get; }
            internal Brush EdgeChrome { get; }
            internal Brush Surface { get; }
            internal Brush Selected { get; }
            internal (Brush Wool, Brush Outline) LogoColors { get; }
            internal (DrawingColor BaseColor, Brush Text, Brush Icon, Brush Accent, Brush Divider) CardColors { get; }
            internal (Brush Title, Brush Tools, Brush TitleRule, Brush ToolsRule) NoteBars { get; }
            internal (Brush Hover, Brush Selected, Brush Pressed, Brush Focus, Brush Disabled) ToolStates { get; }
        }
    }
}
