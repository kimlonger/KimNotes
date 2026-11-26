using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KimNotes
{
    public partial class note
    {
        private enum DockSide { Top, Left, Right }
        private const int MovingMessage = 0x0216, ExitSizeMoveMessage = 0x0232, CancelModeMessage = 0x001F;
        private const int EscapeKey = 0x1B, PhysicalKeyDown = 0x8000;
        private const int DwmTransitionsForceDisabled = 3, DwmCloak = 13;
        private EdgeTabWindow edgeTab;
        private Rectangle? edgeDragStart;
        private int edgeDragSequence;
        private bool showingEdgeWithoutActivation;
        internal bool IsEdgeDocked => edgeTab != null;
        protected override bool ShowWithoutActivation => showingEdgeWithoutActivation || base.ShowWithoutActivation;

        private struct DockTarget
        {
            internal Rectangle Work;
            internal DockSide Side;
        }

        private float DockScale => Math.Max(1, DeviceDpi / 96f);
        private int EdgePx(int value) => (int)Math.Round(value * DockScale);

        private static DockSide? FindDockSide(Point cursor, Rectangle screen, Rectangle workArea,
            IEnumerable<Rectangle> displays, int threshold)
        {
            if (!workArea.Contains(cursor)) return null;
            bool leftShared = false, rightShared = false, topShared = false;
            foreach (var other in displays)
            {
                if (other == screen) continue;
                bool verticalOverlap = Math.Max(other.Top, screen.Top) < Math.Min(other.Bottom, screen.Bottom);
                bool horizontalOverlap = Math.Max(other.Left, screen.Left) < Math.Min(other.Right, screen.Right);
                leftShared |= verticalOverlap && other.Right == screen.Left;
                rightShared |= verticalOverlap && other.Left == screen.Right;
                topShared |= horizontalOverlap && other.Bottom == screen.Top;
            }
            int fromTop = cursor.Y - workArea.Top;
            int fromLeft = cursor.X - workArea.Left;
            int fromRight = workArea.Right - 1 - cursor.X;
            int nearest = Math.Min(fromTop, Math.Min(fromLeft, fromRight));
            if (nearest > threshold || (topShared && fromTop == nearest) ||
                (leftShared && fromLeft == nearest) || (rightShared && fromRight == nearest)) return null;
            if (fromTop == nearest) return DockSide.Top;
            return fromLeft == nearest ? DockSide.Left : DockSide.Right;
        }

        private DockTarget? FindEdgeTarget(Point cursor)
        {
            var displays = Screen.AllScreens;
            var display = displays.FirstOrDefault(screen => screen.Bounds.Contains(cursor));
            if (display == null) return null;
            var work = display.WorkingArea;
            var side = FindDockSide(cursor, display.Bounds, work, displays.Select(screen => screen.Bounds), EdgePx(18));
            return side.HasValue ? new DockTarget { Work = work, Side = side.Value } : (DockTarget?)null;
        }

        private Rectangle EdgeTabBounds(DockTarget target, Point cursor)
        {
            bool horizontal = target.Side == DockSide.Top;
            int width = Math.Min(target.Work.Width, EdgePx(horizontal ? 82 : 26));
            int height = Math.Min(target.Work.Height, EdgePx(horizontal ? 26 : 82));
            int x = horizontal ? Clamp(cursor.X - width / 2, target.Work.Left, target.Work.Right - width)
                : target.Side == DockSide.Left ? target.Work.Left : target.Work.Right - width;
            int y = horizontal ? target.Work.Top
                : Clamp(cursor.Y - height / 2, target.Work.Top, target.Work.Bottom - height);
            var bounds = new Rectangle(x, y, width, height);
            var occupied = Application.OpenForms.OfType<note>().Where(other => other != this && other.IsEdgeDocked)
                .Select(other => other.edgeTab.Bounds).ToArray();
            int min = horizontal ? target.Work.Left : target.Work.Top;
            int max = horizontal ? target.Work.Right - width : target.Work.Bottom - height;
            int step = (horizontal ? width : height) + EdgePx(3);
            for (int offset = 0; offset <= max - min + step; offset += step)
            {
                foreach (int shift in new[] { offset, -offset })
                {
                    var candidate = bounds;
                    if (horizontal) candidate.X = Clamp(x + shift, min, max);
                    else candidate.Y = Clamp(y + shift, min, max);
                    if (!occupied.Any(rect => rect.IntersectsWith(candidate))) return candidate;
                }
            }
            return bounds;
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, Math.Max(min, max)));

        private void TrackEdgeDrag()
        {
            if (edgeDragStart.HasValue || !Visible || IsEdgeDocked || (MouseButtons & MouseButtons.Left) == 0) return;
            edgeDragSequence++;
            edgeDragStart = Bounds;
        }

        private void FinishEdgeDrag()
        {
            var start = edgeDragStart;
            edgeDragStart = null;
            // Windows restores the original bounds when Escape cancels the move.
            // A resize or a keyboard move never starts the mouse dock path.
            if (!start.HasValue || !Visible || IsEdgeDocked || start.Value.Location == Location ||
                (GetAsyncKeyState(EscapeKey) & PhysicalKeyDown) != 0) return;
            var cursor = Cursor.Position;
            var target = FindEdgeTarget(cursor);
            if (!target.HasValue) return;
            int sequence = edgeDragSequence;
            var droppedLocation = Location;
            // Let Windows finish its native move before changing either window's visibility.
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed || Disposing || !Visible || IsMovingWindow || IsEdgeDocked ||
                    sequence != edgeDragSequence || Location != droppedLocation) return;
                DockNote(target.Value, cursor, start.Value.Size);
            }));
        }

        private static string[] EdgeTitleCharacters(string title)
        {
            var display = new List<string>(4);
            var elements = StringInfo.GetTextElementEnumerator(string.IsNullOrWhiteSpace(title) ? "小羊便签" : title);
            while (display.Count < 4 && elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (!string.IsNullOrWhiteSpace(element)) display.Add(element);
            }
            return display.ToArray();
        }

        private void DockNote(DockTarget target, Point cursor, Size size)
        {
            var tabBounds = EdgeTabBounds(target, cursor);
            string title = NoteTitle() ?? "小羊便签";
            edgeTab = new EdgeTabWindow(currentTheme, target, tabBounds, EdgeTitleCharacters(title), title,
                DockScale, () => OpenEdgeTab(activate: true));
            Hide();
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Bounds = NoteBoundsBesideTab(target, tabBounds, size);
            edgeTab.Show();
        }

        private Rectangle NoteBoundsBesideTab(DockTarget target, Rectangle tabBounds, Size size)
        {
            int gap = EdgePx(7);
            return new Rectangle(
                target.Side == DockSide.Left ? target.Work.Left + gap :
                    target.Side == DockSide.Right ? Math.Max(target.Work.Left, target.Work.Right - size.Width - gap) :
                    Clamp(tabBounds.Left, target.Work.Left, target.Work.Right - size.Width),
                target.Side == DockSide.Top ? target.Work.Top + gap : Clamp(tabBounds.Top, target.Work.Top, target.Work.Bottom - size.Height),
                size.Width, size.Height);
        }

        internal void ShowNoteWindow(bool activate = true)
        {
            if (IsEdgeDocked) OpenEdgeTab(activate);
            else ShowUndockedNote(activate);
        }

        private void OpenEdgeTab(bool activate)
        {
            if (!IsEdgeDocked) return;
            Bounds = NoteBoundsBesideTab(edgeTab.Target, edgeTab.Bounds, Size);
            SetPinned(true);
            RevealNoteFromEdge(activate);
        }

        private void RevealNoteFromEdge(bool activate)
        {
            // Show can publish the native shadow before the RichEdit and other child
            // HWNDs have repainted. Cloaking keeps the whole first frame off-screen.
            var window = Handle;
            bool transitionsDisabled = SetDwmFlag(window, DwmTransitionsForceDisabled, true);
            bool cloaked = SetDwmFlag(window, DwmCloak, true);
            try
            {
                unfocusedOutsideSince = null;
                if (activate)
                {
                    compact = false;
                    chromeAnimationTimer.Stop();
                    chromeFade = 1f;
                }
                RestoreThemeSurface();
                PerformLayout();
                ShowUndockedNote(false);
                RedrawCurrentFrame();
                if (cloaked && Visible) DwmFlush();
            }
            finally
            {
                if (IsHandleCreated && Handle == window)
                {
                    if (cloaked) SetDwmFlag(window, DwmCloak, false);
                    if (transitionsDisabled) SetDwmFlag(window, DwmTransitionsForceDisabled, false);
                }
            }
            RemoveEdgeTab();
            if (activate && Visible) Activate();
        }

        private static bool SetDwmFlag(IntPtr window, int attribute, bool enabled)
        {
            int value = enabled ? 1 : 0;
            return DwmSetWindowAttribute(window, attribute, ref value, sizeof(int)) >= 0;
        }

        private void RemoveEdgeTab()
        {
            var tab = edgeTab;
            edgeTab = null;
            tab?.Dispose();
        }

        private void ShowUndockedNote(bool activate)
        {
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            if (!Visible)
            {
                showingEdgeWithoutActivation = !activate;
                try { Show(); }
                finally { showingEdgeWithoutActivation = false; }
            }
            if (activate) Activate();
        }

        private void ResetEdgeForDisplayChange()
        {
            edgeDragStart = null;
            edgeDragSequence++;
            if (!IsEdgeDocked) return;
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(Clamp(Left, work.Left, work.Right - Width), Clamp(Top, work.Top, work.Bottom - Height));
            RevealNoteFromEdge(false);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            edgeDragStart = null;
            RemoveEdgeTab();
            base.OnFormClosed(e);
        }

        private sealed class EdgeTabWindow : Form
        {
            private readonly NoteTheme theme;
            private readonly DockSide side;
            private readonly string[] characters;
            private readonly Font titleFont;
            private readonly int lineHeight;
            private readonly int cornerDiameter;
            private readonly int clickTolerance;
            private readonly Action expand;
            private bool hovering;
            private Point? pressPoint;
            internal DockTarget Target { get; }

            internal EdgeTabWindow(NoteTheme theme, DockTarget target, Rectangle bounds, string[] characters,
                string fullTitle, float scale, Action expand)
            {
                this.theme = theme ?? NoteTheme.All[0];
                Target = target;
                side = target.Side;
                this.characters = characters;
                this.expand = expand;
                titleFont = new Font("Microsoft YaHei UI", 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                lineHeight = (int)Math.Round(16 * scale);
                cornerDiameter = (int)Math.Round(14 * scale);
                clickTolerance = (int)Math.Round(5 * scale);
                BackColor = Blend(this.theme.Chrome, this.theme.Body, 0.35f);
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                AutoScaleMode = AutoScaleMode.None;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
                AccessibleName = "展开便签：" + fullTitle;
                Bounds = bounds;
                using (var outline = TabOutline(ClientRectangle)) Region = new Region(outline);
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x00000080 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                SetDwmFlag(Handle, DwmTransitionsForceDisabled, true);
            }

            private GraphicsPath TabOutline(Rectangle rectangle)
            {
                var path = new GraphicsPath();
                int diameter = Math.Min(cornerDiameter, Math.Min(rectangle.Width, rectangle.Height));
                int x = rectangle.Left, y = rectangle.Top, r = rectangle.Right, b = rectangle.Bottom;
                if (diameter < 2) { path.AddRectangle(rectangle); return path; }
                if (side == DockSide.Right) path.AddArc(x, y, diameter, diameter, 180, 90);
                else path.AddLine(x, y, x, y);
                if (side == DockSide.Left) path.AddArc(r - diameter, y, diameter, diameter, 270, 90);
                else path.AddLine(r, y, r, y);
                if (side != DockSide.Right) path.AddArc(r - diameter, b - diameter, diameter, diameter, 0, 90);
                else path.AddLine(r, b, r, b);
                if (side != DockSide.Left) path.AddArc(x, b - diameter, diameter, diameter, 90, 90);
                else path.AddLine(x, b, x, b);
                path.CloseFigure();
                return path;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var color = hovering ? Blend(theme.Chrome, theme.IconHover, 0.1f) : BackColor;
                e.Graphics.Clear(color);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var outline = new Pen(Blend(theme.Divider, theme.ChromeText, 0.14f)))
                using (var shape = TabOutline(new Rectangle(0, 0, Width - 1, Height - 1)))
                    e.Graphics.DrawPath(outline, shape);
                const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                if (side == DockSide.Top)
                {
                    TextRenderer.DrawText(e.Graphics, string.Concat(characters), titleFont, ClientRectangle, theme.ChromeText, flags);
                    return;
                }
                int top = (Height - lineHeight * characters.Length) / 2;
                foreach (var character in characters)
                {
                    var row = new Rectangle(0, top, Width, lineHeight);
                    TextRenderer.DrawText(e.Graphics, character, titleFont, row, theme.ChromeText, flags);
                    top += lineHeight;
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hovering = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                hovering = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                pressPoint = e.Location;
                Capture = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (pressPoint.HasValue && !IsClickPosition(e.Location)) pressPoint = null;
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                bool clicked = IsClickPosition(e.Location);
                pressPoint = null;
                Capture = false;
                if (clicked) BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) expand(); }));
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                if (!Capture) pressPoint = null;
                base.OnMouseCaptureChanged(e);
            }

            private bool IsClickPosition(Point position)
            {
                if (!pressPoint.HasValue || !ClientRectangle.Contains(position)) return false;
                var down = pressPoint.Value;
                return Math.Abs(position.X - down.X) < clickTolerance && Math.Abs(position.Y - down.Y) < clickTolerance;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) titleFont?.Dispose();
                base.Dispose(disposing);
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
    }
}
