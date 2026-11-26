using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KimNotes.Windows.Capture;
using KimNotes.Windows.Presentation;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using WinForms = System.Windows.Forms;

namespace KimNotes.Windows.Notes
{
    internal enum NoteDockSide { Top, Left, Right }

    internal sealed class NoteEdgeTab : Window
    {
        private const int ShortSide = 28;
        private const int LongSide = 68;
        private const string DefaultCaption = "小羊便签";
        private static readonly char[] LineBreaks = { '\r', '\n', '\u0085', '\u2028', '\u2029' };
        private static readonly ControlTemplate TabButtonTemplate = CreateTabButtonTemplate();
        private readonly NoteDockSide side;
        private readonly DrawingRectangle bounds;
        private readonly Border plate;
        private readonly Button button;
        private readonly TextBlock caption;

        internal int Index { get; }
        internal DrawingRectangle PixelBounds
        {
            get
            {
                var handle = new WindowInteropHelper(this).Handle;
                return handle != IntPtr.Zero && NotePlacement.GetWindowRect(handle, out var current)
                    ? DrawingRectangle.FromLTRB(current.Left, current.Top, current.Right, current.Bottom) : bounds;
            }
        }

        private NoteEdgeTab(int index, NoteDockSide side, DrawingRectangle bounds, string title, Action reveal, NoteTheme theme)
        {
            Index = index;
            this.side = side;
            this.bounds = bounds;
            bool horizontal = side == NoteDockSide.Top;
            Width = horizontal ? LongSide : ShortSide;
            Height = horizontal ? ShortSide : LongSide;
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            UseLayoutRounding = true;

            string heading = FirstLineCaption(title);
            caption = new TextBlock
            {
                Text = FormatCaption(heading, horizontal), FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13, FontWeight = FontWeights.Normal, TextAlignment = TextAlignment.Center,
                LineHeight = 15.5, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            button = new Button
            {
                Template = TabButtonTemplate, Content = caption, Cursor = Cursors.Hand, FocusVisualStyle = null,
                ToolTip = heading
            };
            AutomationProperties.SetName(button, "展开便签 " + index.ToString(CultureInfo.InvariantCulture) + " · " + heading);
            button.Click += (sender, args) => reveal();
            plate = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Child = button };
            Content = plate;
            ApplyTheme(theme);
            SourceInitialized += (sender, args) => NotePlacement.SetWindowPos(
                new WindowInteropHelper(this).Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x14);
        }

        internal void ApplyTheme(NoteTheme theme)
        {
            plate.Background = NoteThemeBrushes.EdgeChrome(theme);
            plate.BorderBrush = NoteThemeBrushes.Divider(theme);
            caption.Foreground = NoteThemeBrushes.ChromeText(theme);
        }

        internal static string FirstLineCaption(string text)
        {
            if (string.IsNullOrEmpty(text)) return DefaultCaption;
            int breakAt = text.IndexOfAny(LineBreaks);
            string first = (breakAt < 0 ? text : text.Substring(0, breakAt)).Trim();
            return first.Length == 0 ? DefaultCaption : first;
        }

        internal static string FormatCaption(string title, bool horizontal)
        {
            var elements = StringInfo.GetTextElementEnumerator(FirstLineCaption(title));
            var visible = new List<string>(4);
            while (visible.Count < 4 && elements.MoveNext()) visible.Add(elements.GetTextElement());
            return string.Join(horizontal ? "" : "\n", visible);
        }

        internal static NoteEdgeTab TryCreate(Window note, DrawingPoint cursor, DrawingPoint travel, int index, IEnumerable<DrawingRectangle> occupied,
            string title, Action reveal, NoteTheme theme)
        {
            using var dpiScope = new CaptureScreen.PhysicalDpiScope();
            var screens = WinForms.Screen.AllScreens;
            var screen = screens.FirstOrDefault(candidate => candidate.Bounds.Contains(cursor));
            if (screen == null) return null;
            var handle = new WindowInteropHelper(note).Handle;
            if (handle == IntPtr.Zero || !NotePlacement.GetWindowRect(handle, out var rect)) return null;
            var noteBounds = DrawingRectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var work = screen.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(note);
            int Scale(double value, double factor) => Math.Max(1, (int)Math.Round(value * factor));
            var side = FindDockSide(cursor, noteBounds, travel, screen.Bounds, work, screens.Select(candidate => candidate.Bounds),
                Scale(18, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
            if (!side.HasValue) return null;
            bool horizontal = side.Value == NoteDockSide.Top;
            var tabSize = new DrawingSize(Scale(horizontal ? LongSide : ShortSide, dpi.DpiScaleX),
                Scale(horizontal ? ShortSide : LongSide, dpi.DpiScaleY));
            var bounds = FindTabBounds(side.Value, work, cursor, tabSize,
                Scale(4, horizontal ? dpi.DpiScaleX : dpi.DpiScaleY), occupied);
            return bounds.HasValue ? new NoteEdgeTab(index, side.Value, bounds.Value, title, reveal, theme) : null;
        }

        internal void MoveNoteBeside(Window note)
        {
            var handle = new WindowInteropHelper(note).Handle;
            if (handle == IntPtr.Zero || !NotePlacement.GetWindowRect(handle, out var rect)) return;
            var tab = PixelBounds;
            var work = WinForms.Screen.FromRectangle(tab).WorkingArea;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            int inset = (int)Math.Round(7 * VisualTreeHelper.GetDpi(note).DpiScaleX);
            int x = side == NoteDockSide.Left ? work.Left + inset : side == NoteDockSide.Right ? work.Right - width - inset : tab.Left;
            int y = side == NoteDockSide.Top ? work.Top + inset : tab.Top;
            NotePlacement.SetWindowPos(handle, IntPtr.Zero, Clamp(x, work.Left, work.Right - width),
                Clamp(y, work.Top, work.Bottom - height), 0, 0, 0x15);
        }

        internal static NoteDockSide? FindDockSide(DrawingPoint cursor, DrawingRectangle noteBounds, DrawingPoint travel,
            DrawingRectangle screen, DrawingRectangle work, IEnumerable<DrawingRectangle> displays, int threshold)
        {
            if (!work.Contains(cursor)) return null;
            int fromTop = cursor.Y - work.Top;
            int fromLeft = cursor.X - work.Left;
            int fromRight = work.Right - 1 - cursor.X;
            bool nearTop = fromTop <= threshold && fromTop <= Math.Min(fromLeft, fromRight);
            NoteDockSide? side = nearTop ? NoteDockSide.Top : (NoteDockSide?)null;

            int sideways = Math.Abs(travel.X);
            if (sideways >= Math.Max(1, threshold / 3) && (!nearTop || sideways >= Math.Abs(travel.Y)) &&
                noteBounds.Width < work.Width && noteBounds.IntersectsWith(work))
            {
                if (travel.X < 0 && noteBounds.Left - work.Left <= threshold) side = NoteDockSide.Left;
                if (travel.X > 0 && work.Right - noteBounds.Right <= threshold) side = NoteDockSide.Right;
            }

            if (!side.HasValue) return null;
            foreach (var other in displays)
            {
                if (other == screen) continue;
                bool verticalOverlap = Math.Max(other.Top, screen.Top) < Math.Min(other.Bottom, screen.Bottom);
                bool horizontalOverlap = Math.Max(other.Left, screen.Left) < Math.Min(other.Right, screen.Right);
                if ((side == NoteDockSide.Left && verticalOverlap && other.Right == screen.Left) ||
                    (side == NoteDockSide.Right && verticalOverlap && other.Left == screen.Right) ||
                    (side == NoteDockSide.Top && horizontalOverlap && other.Bottom == screen.Top)) return null;
            }
            return side;
        }

        internal static DrawingRectangle? FindTabBounds(NoteDockSide side, DrawingRectangle work, DrawingPoint cursor,
            DrawingSize tabSize, int gap, IEnumerable<DrawingRectangle> occupied)
        {
            if (tabSize.Width <= 0 || tabSize.Height <= 0 || tabSize.Width > work.Width || tabSize.Height > work.Height) return null;
            bool horizontal = side == NoteDockSide.Top;
            int length = horizontal ? tabSize.Width : tabSize.Height;
            int min = horizontal ? work.Left : work.Top;
            int max = (horizontal ? work.Right : work.Bottom) - length;
            int preferred = Clamp((horizontal ? cursor.X : cursor.Y) - length / 2, min, max);
            int across = side == NoteDockSide.Right ? work.Right - tabSize.Width : work.Left;
            var obstacles = occupied.Select(rect => DrawingRectangle.Inflate(rect, gap, gap)).ToArray();
            var candidates = new HashSet<int> { preferred, min, max };
            foreach (var obstacle in obstacles)
            {
                candidates.Add(Clamp((horizontal ? obstacle.Left : obstacle.Top) - length, min, max));
                candidates.Add(Clamp(horizontal ? obstacle.Right : obstacle.Bottom, min, max));
            }
            foreach (int slot in candidates.OrderBy(value => Math.Abs(value - preferred)).ThenBy(value => value))
            {
                var bounds = new DrawingRectangle(horizontal ? slot : across, horizontal ? work.Top : slot, tabSize.Width, tabSize.Height);
                if (!obstacles.Any(bounds.IntersectsWith)) return bounds;
            }
            return null;
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

        private static ControlTemplate CreateTabButtonTemplate()
        {
            var hitArea = new FrameworkElementFactory(typeof(Border));
            hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "Caption");
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            hitArea.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = hitArea };
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(OpacityProperty, 0.75d, "Caption"));
            template.Triggers.Add(pressed);
            return template;
        }
    }
}
