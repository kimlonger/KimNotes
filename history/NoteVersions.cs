using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KimNotes.utils;
using Newtonsoft.Json.Linq;

namespace KimNotes
{
    public partial class history
    {
        private Panel versionsPanel;
        private CancellationTokenSource versionsPage;
        private CancellationTokenSource versionPreview;
        private ListBox versionList;
        private RichTextBox versionBody;
        private Label versionsStatus, versionsTitle;
        private VersionActionButton versionMore, versionRestore, versionsBack;
        private ActionIconButton versionCopy;
        private TableLayoutPanel versionsLayout;
        private VersionPreviewSurface previewSurface;
        private JObject latestVersion;
        private string versionsFile, restoreOperation;
        private long? versionBefore;
        private bool loadingVersions, restoringVersion;
        private int versionBodyWheelDelta;

        private sealed class VersionRow
        {
            internal JObject Snapshot;
            internal bool Current;
            internal long Revision => Snapshot.Value<long>("revision");
            internal string DateLabel => Snapshot.Value<DateTime?>("updatedAt")?.ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "时间未知";
            public override string ToString()
            {
                return DateLabel + (Current ? "  ·  当前版本" : "");
            }
        }

        private async void OpenVersions(HistoryCardMeta card)
        {
            if (versionsPanel != null) return;
            var page = new CancellationTokenSource();
            versionsPage = page;
            versionsFile = card.FileName;
            var theme = card.Theme ?? NoteTheme.Current();
            var font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            var titleFont = new Font(font, FontStyle.Bold);
            versionsPanel = new Panel { BackColor = formBackColor, Font = font, Visible = false };
            versionsPanel.Disposed += (sender, args) => { font.Dispose(); titleFont.Dispose(); };
            versionsBack = VersionButton("返回列表", () => BeginInvoke((Action)(() =>
            {
                // Finish the native button click before disposing its window.
                if (versionsPage == page) CloseVersions();
            })));
            versionMore = VersionButton("加载更早版本", async () => await LoadVersions());
            versionMore.Quiet = true;
            versionCopy = new ActionIconButton(CopyVersion)
            {
                IconId = "copy", IconSize = 16, Theme = theme, BackColor = theme.Body, AccessibleName = "复制全文",
                Width = Dpi(25), Height = Dpi(25), Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = Padding.Empty
            };
            syncTips.SetToolTip(versionCopy, "复制全文");
            versionCopy.Disposed += (sender, args) => { if (!resourcesReleased) syncTips.SetToolTip(versionCopy, null); };
            versionRestore = VersionButton("恢复此版本", RestoreVersion);
            versionRestore.Primary = true;
            versionCopy.Enabled = versionRestore.Enabled = false;
            versionList = new VersionTimeline { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
                BackColor = Color.White, ForeColor = theme.ChromeText, AccessibleName = "版本时间轴",
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Dpi(33), Margin = Padding.Empty };
            versionList.DrawItem += DrawVersionRow;
            versionList.GotFocus += (sender, args) => versionList.Invalidate();
            versionList.LostFocus += (sender, args) => versionList.Invalidate();
            versionList.SelectedIndexChanged += async (sender, args) => await PreviewVersion();
            versionBody = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = theme.Body, ForeColor = theme.Text, Font = font,
                BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.None, WordWrap = true, DetectUrls = false,
                AccessibleName = "版本预览", Margin = Padding.Empty };
            versionBody.MouseWheel += ScrollVersionBody;
            previewSurface = new VersionPreviewSurface { Dock = DockStyle.Fill, Padding = new Padding(Dpi(13), Dpi(12), Dpi(13), Dpi(12)),
                Tint = theme.Body, Margin = new Padding(0, Dpi(7), 0, 0), Radius = Dpi(10) };
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty,
                BackColor = Color.Transparent };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(29)));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(versionBody, 0, 0);
            content.Controls.Add(versionCopy, 1, 0);
            previewSurface.Controls.Add(content);
            versionsStatus = new Label { Dock = DockStyle.Fill, ForeColor = theme.ChromeText, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "历史版本状态", Margin = Padding.Empty };
            versionsStatus.TextChanged += (sender, args) => LayoutVersionRows();
            versionsTitle = new Label { Text = "便签", Dock = DockStyle.Fill, AutoEllipsis = true,
                Font = titleFont, ForeColor = theme.Text, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                Margin = Padding.Empty, Padding = new Padding(Dpi(2), Dpi(5), Dpi(2), 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.Paint += (sender, args) =>
            {
                using (var pen = new Pen(theme.Divider)) args.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            versionsBack.Size = new Size(Dpi(82), Dpi(27));
            versionRestore.Size = new Size(Dpi(92), Dpi(27));
            footer.Controls.Add(versionsBack, 0, 0);
            footer.Controls.Add(versionRestore, 2, 0);
            versionsLayout = new TableLayoutPanel { ColumnCount = 1, RowCount = 6, Dock = DockStyle.Fill, Margin = Padding.Empty };
            versionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(34)));
            versionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            versionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            versionsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            versionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            versionsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(34)));
            versionsLayout.Controls.Add(versionsTitle, 0, 0);
            versionsLayout.Controls.Add(versionList, 0, 1);
            versionsLayout.Controls.Add(versionMore, 0, 2);
            versionsLayout.Controls.Add(previewSurface, 0, 3);
            versionsLayout.Controls.Add(versionsStatus, 0, 4);
            versionsLayout.Controls.Add(footer, 0, 5);
            versionsPanel.Controls.Add(versionsLayout);
            versionMore.Visible = false;
            SuspendLayout();
            try
            {
                Controls.Add(versionsPanel);
                versionsPanel.BringToFront();
                AdjustLayoutForDpi();
                searchBox.Hide();
                panel1.Hide();
                if (chromeBar.Tag is Label heading) heading.Text = "历史版本";
                versionsPanel.Show();
            }
            finally { ResumeLayout(true); }
            LayoutVersionRows();
            var load = LoadVersions();
            RedrawCurrentFrame();
            await load;
        }

        private VersionActionButton VersionButton(string label, Action action)
        {
            var button = new VersionActionButton { Text = label, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                Width = Dpi(132), Height = Dpi(29), BackColor = Color.White, ForeColor = Color.FromArgb(47, 67, 90),
                Anchor = AnchorStyles.Left, Margin = Padding.Empty, Radius = Dpi(6) };
            button.Click += (sender, args) => action();
            return button;
        }

        private void LayoutVersionRows()
        {
            if (versionsLayout == null || versionsLayout.IsDisposed) return;
            versionsLayout.RowStyles[1].Height = versionList.ItemHeight * Math.Min(3, versionList.Items.Count);
            versionsLayout.RowStyles[2].Height = versionMore.Visible ? Dpi(31) : 0;
            versionsLayout.RowStyles[4].Height = string.IsNullOrEmpty(versionsStatus.Text) ? Dpi(11) : Dpi(30);
        }

        private static int ReadWheelSteps(MouseEventArgs args, ref int remainder)
        {
            if (args is HandledMouseEventArgs handled)
            {
                if (handled.Handled) return 0;
                handled.Handled = true;
            }
            remainder += args.Delta;
            int steps = remainder / SystemInformation.MouseWheelScrollDelta;
            remainder %= SystemInformation.MouseWheelScrollDelta;
            return steps;
        }

        private void ScrollVersionBody(object sender, MouseEventArgs args)
        {
            int steps = ReadWheelSteps(args, ref versionBodyWheelDelta);
            if (steps == 0) return;
            const int scroll = 0x00B5, lineScroll = 0x00B6;
            const int pageUp = 2, pageDown = 3;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines >= 0)
                Win32ApiHelper.SendMessage(versionBody.Handle, lineScroll, 0, -steps * lines);
            else
                for (int page = 0; page < Math.Abs(steps); page++)
                    Win32ApiHelper.SendMessage(versionBody.Handle, scroll, steps > 0 ? pageUp : pageDown, 0);
        }

        private sealed class VersionTimeline : ListBox
        {
            private int wheelDelta;
            protected override CreateParams CreateParams
            {
                get
                {
                    const int scrollBars = 0x00300000; // WS_HSCROLL | WS_VSCROLL
                    var parameters = base.CreateParams;
                    parameters.Style &= ~scrollBars;
                    return parameters;
                }
            }

            protected override void OnMouseWheel(MouseEventArgs args)
            {
                int steps = ReadWheelSteps(args, ref wheelDelta);
                if (steps == 0 || Items.Count == 0) return;
                int visible = Math.Max(1, ClientSize.Height / ItemHeight);
                int lines = SystemInformation.MouseWheelScrollLines;
                TopIndex = Math.Max(0, Math.Min(Items.Count - visible, TopIndex - steps * (lines < 0 ? visible : lines)));
            }
        }

        private void DrawVersionRow(object sender, DrawItemEventArgs args)
        {
            if (args.Index < 0) return;
            var row = (VersionRow)versionList.Items[args.Index];
            var bounds = args.Bounds;
            bool selected = (args.State & DrawItemState.Selected) != 0;
            int centerX = bounds.Left + Dpi(10), centerY = bounds.Top + bounds.Height / 2;
            var ink = Color.FromArgb(36, 56, 77);
            var muted = Color.FromArgb(93, 113, 134);
            var accent = Color.FromArgb(64, 112, 173);
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(Color.White)) args.Graphics.FillRectangle(background, bounds);
            using (var line = new Pen(Color.FromArgb(208, 220, 234), Dpi(1)))
                args.Graphics.DrawLine(line, centerX, args.Index == 0 ? centerY : bounds.Top,
                    centerX, args.Index == versionList.Items.Count - 1 ? centerY : bounds.Bottom);
            var surface = new Rectangle(bounds.Left + Dpi(25), bounds.Top + Dpi(3),
                Math.Max(1, bounds.Width - Dpi(27)), bounds.Height - Dpi(6));
            if (selected)
            {
                using (var plate = RoundedVersionRect(surface, Dpi(7)))
                using (var fill = new SolidBrush(Color.FromArgb(230, 239, 250))) args.Graphics.FillPath(fill, plate);
            }
            int radius = Dpi(4);
            var dot = new Rectangle(centerX - radius, centerY - radius, radius * 2, radius * 2);
            using (var fill = new SolidBrush(selected ? accent : Color.White)) args.Graphics.FillEllipse(fill, dot);
            using (var border = new Pen(selected ? accent : muted, Dpi(1))) args.Graphics.DrawEllipse(border, dot);
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            bool showInlineStatus = row.Current && surface.Width >= Dpi(165);
            int statusWidth = showInlineStatus ? Dpi(62) : 0;
            var dateBounds = Rectangle.FromLTRB(surface.Left + Dpi(8), bounds.Top, Math.Max(surface.Left + Dpi(8), surface.Right - statusWidth), bounds.Bottom);
            TextRenderer.DrawText(args.Graphics, row.Current && !showInlineStatus ? row.ToString() : row.DateLabel, versionList.Font,
                dateBounds, ink, flags | TextFormatFlags.EndEllipsis);
            if (showInlineStatus)
                TextRenderer.DrawText(args.Graphics, "当前版本", versionList.Font,
                    new Rectangle(surface.Right - statusWidth, bounds.Top, statusWidth - Dpi(8), bounds.Height),
                    muted, flags | TextFormatFlags.Right);
            if (selected && versionList.Focused && (args.State & DrawItemState.Focus) != 0)
            {
                using (var plate = RoundedVersionRect(surface, Dpi(7)))
                using (var border = new Pen(Color.FromArgb(139, 170, 210))) args.Graphics.DrawPath(border, plate);
            }
        }

        private static GraphicsPath RoundedVersionRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(0, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            if (d < 2) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class VersionPreviewSurface : Panel
        {
            internal int Radius;
            internal Color Tint;
            protected override void OnPaintBackground(PaintEventArgs args)
            {
                base.OnPaintBackground(args);
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var outline = RoundedVersionRect(ClientRectangle, Radius))
                using (var fill = new SolidBrush(Tint)) args.Graphics.FillPath(fill, outline);
            }
        }

        private sealed class VersionActionButton : Button
        {
            internal bool Primary, Quiet;
            internal int Radius;
            private bool hovering;
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
            protected override void OnPaint(PaintEventArgs args)
            {
                args.Graphics.Clear(Color.White);
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                var background = Primary ? (Enabled ? Color.FromArgb(64, 112, 173) : Color.FromArgb(219, 229, 240))
                    : hovering && Enabled ? Color.FromArgb(239, 245, 251) : Color.White;
                var foreground = Enabled ? (Primary ? Color.White : ForeColor) : Color.FromArgb(98, 114, 133);
                using (var shape = RoundedVersionRect(box, Radius))
                using (var fill = new SolidBrush(background))
                {
                    args.Graphics.FillPath(fill, shape);
                    if ((!Primary && !Quiet) || (Focused && ShowFocusCues))
                    {
                        using (var edge = new Pen(Focused && ShowFocusCues ? Color.FromArgb(112, 158, 213) : Color.FromArgb(199, 213, 229)))
                            args.Graphics.DrawPath(edge, shape);
                    }
                }
                TextRenderer.DrawText(args.Graphics, Text, Font, new Rectangle(Quiet ? 4 : 7, 0,
                    Math.Max(0, Width - 14), Height), foreground, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | (Quiet ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
            }
        }

        private async Task LoadVersions()
        {
            if (loadingVersions || versionsPage == null) return;
            var page = versionsPage;
            loadingVersions = true; versionMore.Enabled = false;
            versionsStatus.Text = "正在读取历史版本…";
            try
            {
                var cloud = CloudSync.Current;
                if (cloud == null) throw new InvalidOperationException("请登录后查看已同步便签的历史版本。");
                var snapshots = (await cloud.ReadNoteVersions(versionsFile, versionBefore, page.Token))
                    .OfType<JObject>().OrderByDescending(value => value.Value<long>("revision")).ToArray();
                if (versionsPage != page) return;
                versionsStatus.Text = "";
                if (snapshots.Length == 0)
                {
                    versionMore.Visible = false;
                    if (versionList.Items.Count == 0) versionsStatus.Text = "暂无历史版本。";
                    return;
                }
                foreach (var snapshot in snapshots)
                {
                    bool current = latestVersion == null;
                    if (current)
                    {
                        latestVersion = snapshot;
                        var title = snapshot["document"]?.Value<string>("title");
                        versionsTitle.Text = string.IsNullOrWhiteSpace(title) ? "未命名便签" : title;
                    }
                    if (snapshot["deletedAt"] != null && snapshot["deletedAt"].Type != JTokenType.Null) continue;
                    versionList.Items.Add(new VersionRow { Snapshot = snapshot, Current = current });
                }
                versionBefore = snapshots.Last().Value<long>("revision");
                versionMore.Visible = versionBefore > 1;
                versionMore.Text = "加载更早版本";
                if (versionList.SelectedIndex < 0 && versionList.Items.Count > 0) versionList.SelectedIndex = 0;
            }
            catch (Exception error)
            {
                if (versionsPage != page) return;
                versionsStatus.Text = error.Message;
                versionMore.Visible = true;
                versionMore.Text = "重试加载";
            }
            finally
            {
                if (versionsPage == page) { loadingVersions = false; versionMore.Enabled = true; LayoutVersionRows(); }
            }
        }

        private async Task PreviewVersion()
        {
            if (!(versionList.SelectedItem is VersionRow row)) return;
            versionPreview?.Cancel();
            using (var preview = CancellationTokenSource.CreateLinkedTokenSource(versionsPage.Token))
            {
                versionPreview = preview;
                var document = (JObject)row.Snapshot["document"];
                var theme = NoteTheme.All.FirstOrDefault(item => item.Id == document.Value<int>("themeId")) ?? NoteTheme.Current();
                versionBody.BackColor = versionCopy.BackColor = theme.Body;
                versionCopy.Theme = theme;
                previewSurface.Tint = versionBody.BackColor;
                previewSurface.Invalidate();
                var title = document.Value<string>("title");
                var body = document.Value<string>("text") ?? "";
                versionBodyWheelDelta = 0;
                versionBody.Text = !string.IsNullOrWhiteSpace(title) && title != versionsTitle.Text
                    ? (body.Length > 0 ? title + Environment.NewLine + Environment.NewLine + body : title) : body;
                versionBody.SelectAll();
                versionBody.SelectionFont = versionsPanel.Font;
                versionBody.SelectionColor = theme.Text;
                versionBody.Select(0, 0);
                versionCopy.Enabled = versionBody.TextLength > 0 || !string.IsNullOrWhiteSpace(title);
                versionRestore.Enabled = !row.Current && !restoringVersion;
                versionsStatus.Text = "";
                try
                {
                    if (string.IsNullOrEmpty(document.Value<string>("rtf")) &&
                        string.IsNullOrEmpty(document.Value<string>("rtfGzip")) &&
                        !(document["attachments"] is JArray attachments && attachments.Count > 0)) return;
                    string rtf = await CloudSync.Current.PreviewNoteVersion(versionsFile, row.Snapshot, preview.Token);
                    if (!preview.IsCancellationRequested && versionPreview == preview)
                    {
                        versionBody.Rtf = rtf;
                        versionCopy.Enabled = versionBody.TextLength > 0 || !string.IsNullOrWhiteSpace(title);
                    }
                }
                catch (Exception error)
                {
                    if (!preview.IsCancellationRequested && versionPreview == preview) versionsStatus.Text = error.Message;
                }
                finally { if (versionPreview == preview) versionPreview = null; }
            }
        }

        private void CopyVersion()
        {
            if (!(versionList.SelectedItem is VersionRow row)) return;
            string content = versionBody.Text;
            string title = row.Snapshot["document"]?.Value<string>("title");
            string firstLine = content.Split('\n')[0].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(title) && firstLine != title)
                content = content.Length == 0 ? title : title + Environment.NewLine + Environment.NewLine + content;
            if (string.IsNullOrEmpty(content)) return;
            try
            {
                Clipboard.SetText(content);
                syncTips.Show("已复制", versionCopy, 0, versionCopy.Height, 1500);
            }
            catch (System.Runtime.InteropServices.ExternalException) { versionsStatus.Text = "暂时无法复制，请重试。"; }
        }

        private async void RestoreVersion()
        {
            if (restoringVersion || !(versionList.SelectedItem is VersionRow row) || row.Current) return;
            if (restoreOperation == null && MessageBox.Show(this, "恢复这个版本？当前内容仍会保留在历史里。",
                "恢复此版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var page = versionsPage;
            restoreOperation = restoreOperation ?? Guid.NewGuid().ToString();
            restoringVersion = true;
            versionList.Enabled = versionRestore.Enabled = versionsBack.Enabled = false;
            versionsStatus.Text = "正在恢复…";
            try
            {
                bool applied = await CloudSync.Current.RestoreNoteVersion(versionsFile,
                    row.Snapshot.Value<string>("id"), latestVersion.Value<long>("revision"), row.Revision, restoreOperation, page.Token);
                if (versionsPage != page) return;
                restoreOperation = null;
                versionBefore = null; latestVersion = null;
                versionList.Items.Clear();
                versionBody.Clear();
                versionCopy.Enabled = false;
                await LoadVersions();
                if (versionsPage == page)
                    versionsStatus.Text = applied ? "已恢复为一个新版本。" : "便签已有新改动，已重新加载历史。";
            }
            catch (Exception error)
            {
                if (versionsPage == page) versionsStatus.Text = error.Message;
            }
            finally
            {
                if (versionsPage == page)
                {
                    restoringVersion = false;
                    versionsBack.Enabled = true;
                    versionList.Enabled = restoreOperation == null;
                    versionRestore.Enabled = versionList.SelectedItem is VersionRow chosen && !chosen.Current;
                    versionRestore.Text = restoreOperation == null ? "恢复此版本" : "重试恢复";
                }
            }
        }

        private void CloseVersions()
        {
            var outgoing = versionsPanel;
            if (outgoing == null) return;
            var page = versionsPage;
            versionsPage = null;
            page.Cancel();
            page.Dispose();
            versionPreview = null;
            versionsPanel = null;
            versionsLayout = null;
            latestVersion = null; versionBefore = null; restoreOperation = null;
            loadingVersions = restoringVersion = false;
            if (resourcesReleased)
            {
                outgoing.Dispose();
                return;
            }
            SuspendLayout();
            try
            {
                outgoing.Hide();
                outgoing.Dispose();
                if (chromeBar.Tag is Label heading) heading.Text = "便签列表";
                searchBox.Show();
                panel1.Show();
            }
            finally { ResumeLayout(true); }
            searchBox.Input.Focus();
            RedrawCurrentFrame();
        }
    }
}
