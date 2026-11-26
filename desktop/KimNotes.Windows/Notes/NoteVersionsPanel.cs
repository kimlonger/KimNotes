using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using Newtonsoft.Json.Linq;
using static KimNotes.Windows.Notes.NoteCatalogVisuals;

namespace KimNotes.Windows.Notes
{
    internal sealed class NoteVersionsPanel : Grid, IDisposable
    {
        private const double TimelineRowHeight = 28;
        private const double PreviewLineHeight = 18;
        private const int PreviewVisibleLines = 4;
        private readonly CloudSync cloud;
        private readonly string filename;
        private readonly NoteTheme originalTheme;
        private readonly string originalTitle;
        private readonly ListBox versions = new ListBox();
        private readonly TextBlock heading = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly TextBlock status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 0, 3, 5),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 35
        };
        private readonly RichTextBox preview = new RichTextBox
        {
            IsReadOnly = true, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent, FontSize = 12.67, Height = PreviewLineHeight * PreviewVisibleLines
        };
        private readonly Border previewSurface;
        private readonly TextBlock previewCaption = new TextBlock
        {
            Text = "版本内容", FontSize = 11, VerticalAlignment = VerticalAlignment.Center
        };
        private readonly Button restore = Action("恢复此版本");
        private readonly Button back = Action("返回列表");
        private readonly Button copy = new Button { Style = IconButton, Content = Icon(Copy), ToolTip = "复制全文" };
        private readonly Button more = Action("加载更早版本", 100);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource loading;
        private JObject latest;
        private VersionRow rendered;
        private long? before;
        private bool fetching;
        private bool restoring;
        private bool disposed;
        private bool hasMore = true;
        private string operation;

        internal NoteVersionsPanel(CloudSync cloud, string filename, string title, NoteTheme theme, Action goBack)
        {
            this.cloud = cloud;
            this.filename = filename;
            originalTheme = theme ?? NoteTheme.Current();
            originalTitle = string.IsNullOrWhiteSpace(title) ? "未命名便签" : title;
            heading.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
            status.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
            heading.Text = originalTitle;
            heading.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = heading });
            status.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = status });
            Margin = new Thickness(10);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var timelineHeading = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            more.Height = 22;
            more.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(more, Dock.Right);
            timelineHeading.Children.Add(more);
            timelineHeading.Children.Add(heading);
            Children.Add(timelineHeading);
            SetRow(status, 1);
            Children.Add(status);

            ConfigureList(versions);
            versions.ItemTemplate = TimelineTemplate();
            AutomationProperties.SetName(versions, "版本时间轴");
            SetRow(versions, 2);
            Children.Add(versions);

            var inner = new DockPanel();
            var previewHeading = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            AutomationProperties.SetName(copy, "复制全文");
            DockPanel.SetDock(copy, Dock.Right);
            previewHeading.Children.Add(copy);
            previewHeading.Children.Add(previewCaption);
            DockPanel.SetDock(previewHeading, Dock.Top);
            inner.Children.Add(previewHeading);
            AutomationProperties.SetName(preview, "版本预览");
            inner.Children.Add(preview);
            previewSurface = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 4, 7, 5),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0), Child = inner
            };
            SetRow(previewSurface, 3);
            Children.Add(previewSurface);

            var commands = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
            back.Height = restore.Height = 29;
            back.Margin = new Thickness(0, 0, 4, 0);
            restore.Margin = new Thickness(4, 0, 0, 0);
            commands.Children.Add(back);
            commands.Children.Add(restore);
            SetRow(commands, 4);
            Children.Add(commands);

            ApplyPreviewTheme(originalTheme);
            SetPreview(new FlowDocument());
            back.Click += (sender, args) => goBack();
            copy.Click += (sender, args) => CopyVersion();
            restore.Click += async (sender, args) => await RestoreVersion();
            more.Click += async (sender, args) => await LoadMore();
            versions.SelectionChanged += async (sender, args) => await PreviewVersion();
            Loaded += async (sender, args) => { if (latest == null) await LoadMore(); };
            SetStatus(null);
            UpdateActions();
        }

        private async Task LoadMore()
        {
            if (fetching || restoring || !hasMore || disposed) return;
            fetching = true;
            UpdateActions();
            SetStatus("正在读取历史版本…");
            try
            {
                var page = await cloud.ReadNoteVersions(filename, before, lifetime.Token);
                if (disposed) return;
                var snapshots = page.OfType<JObject>().OrderByDescending(item => item.Value<long>("revision")).ToArray();
                if (snapshots.Length > 0)
                {
                    if (latest == null)
                    {
                        latest = snapshots[0];
                        var name = latest["document"]?.Value<string>("title");
                        heading.Text = string.IsNullOrWhiteSpace(name) ? originalTitle : name;
                    }
                    before = snapshots[snapshots.Length - 1].Value<long>("revision");
                    foreach (var item in snapshots)
                    {
                        if (item["deletedAt"] != null && item["deletedAt"].Type != JTokenType.Null) continue;
                        versions.Items.Add(new VersionRow(item, ReferenceEquals(item, latest)));
                    }
                }
                hasMore = snapshots.Length > 0 && before > 1;
                more.Content = "加载更早版本";
                SetStatus(versions.Items.Count == 0 ? "暂无历史版本。" : null);
                if (versions.SelectedIndex < 0 && versions.Items.Count > 0) versions.SelectedIndex = 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!disposed)
                {
                    more.Content = "重试加载";
                    SetStatus(error.Message);
                }
            }
            finally
            {
                fetching = false;
                if (!disposed) UpdateActions();
            }
        }

        private async Task PreviewVersion()
        {
            loading?.Cancel();
            loading = null;
            rendered = null;
            operation = null;
            UpdateActions();
            if (disposed || !(versions.SelectedItem is VersionRow row)) return;
            var document = (JObject)row.Snapshot["document"];
            string name = document.Value<string>("title");
            string text = document.Value<string>("text") ?? "";
            var theme = NoteTheme.All.FirstOrDefault(item => item.Id == document.Value<int>("themeId")) ?? originalTheme;
            ApplyPreviewTheme(theme);
            SetPreview(new FlowDocument(new Paragraph(new Run(
                string.IsNullOrWhiteSpace(name) || name == heading.Text || text.StartsWith(name, StringComparison.Ordinal) ? text :
                string.IsNullOrEmpty(text) ? name : name + "\n\n" + text))));
            rendered = row;
            UpdateActions();

            if (string.IsNullOrEmpty(document.Value<string>("rtf")) &&
                string.IsNullOrEmpty(document.Value<string>("rtfGzip")) &&
                !(document["attachments"] is JArray attachments && attachments.Count > 0)) return;

            using (var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                loading = request;
                try
                {
                    string rtf = await cloud.PreviewNoteVersion(filename, row.Snapshot, request.Token);
                    if (disposed || request != loading || request.IsCancellationRequested) return;
                    using (var buffer = new MemoryStream(Encoding.ASCII.GetBytes(rtf)))
                        SetPreview(NoteDocumentRtf.Read(buffer));
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    if (!disposed && request == loading && !request.IsCancellationRequested) SetStatus(error.Message);
                }
                finally
                {
                    if (request == loading)
                    {
                        loading = null;
                        if (!disposed) UpdateActions();
                    }
                }
            }
        }

        private void SetPreview(FlowDocument document)
        {
            document.PagePadding = new Thickness(0);
            document.LineHeight = PreviewLineHeight;
            document.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            preview.Document = document;
            preview.ScrollToHome();
        }

        private void ApplyPreviewTheme(NoteTheme theme)
        {
            previewSurface.Background = NoteThemeBrushes.Surface(theme);
            var colors = NoteThemeBrushes.CardColors(theme);
            previewSurface.BorderBrush = colors.Divider;
            previewCaption.Foreground = colors.Icon;
            ((System.Windows.Shapes.Path)copy.Content).Stroke = colors.Icon;
            preview.Foreground = colors.Text;
        }

        private void CopyVersion()
        {
            if (disposed || rendered == null) return;
            string content = new TextRange(preview.Document.ContentStart, preview.Document.ContentEnd).Text.TrimEnd('\r', '\n');
            string title = rendered.Snapshot["document"]?.Value<string>("title");
            string firstLine = content.Split('\n')[0].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(title) && firstLine != title)
                content = content.Length == 0 ? title : title + "\n\n" + content;
            if (content.Length == 0) return;
            try { Clipboard.SetText(content); SetStatus("已复制。"); }
            catch (ExternalException) { SetStatus("暂时无法复制，请重试。"); }
        }

        private async Task RestoreVersion()
        {
            if (disposed || rendered == null || fetching || restoring || rendered.Current || latest == null) return;
            var selected = rendered;
            if (operation == null &&
                MessageBox.Show(Window.GetWindow(this), "恢复这个版本？当前内容仍会保留在历史里。", "恢复此版本",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            restoring = true;
            UpdateActions();
            SetStatus("正在恢复…");
            try
            {
                bool restored = await cloud.RestoreNoteVersion(filename, selected.Snapshot.Value<string>("id"),
                    latest.Value<long>("revision"), selected.Snapshot.Value<long>("revision"),
                    operation ?? (operation = Guid.NewGuid().ToString()), lifetime.Token);
                if (disposed) return;
                operation = null;
                before = null;
                latest = null;
                hasMore = true;
                versions.Items.Clear();
                SetPreview(new FlowDocument());
                restoring = false;
                await LoadMore();
                if (!disposed && latest != null)
                    SetStatus(restored ? "已恢复为一个新版本。" : "便签已有新改动，已重新加载历史。");
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!disposed) SetStatus(error.Message); }
            finally
            {
                restoring = false;
                if (!disposed) UpdateActions();
            }
        }

        private void SetStatus(string message)
        {
            status.Text = message;
            status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateActions()
        {
            copy.IsEnabled = !disposed && rendered != null;
            restore.IsEnabled = copy.IsEnabled && !fetching && !restoring && !rendered.Current;
            restore.SetResourceReference(Control.BackgroundProperty,
                restore.IsEnabled ? DesktopCardTheme.Accent : DesktopCardTheme.Tint);
            restore.SetResourceReference(Control.ForegroundProperty,
                restore.IsEnabled ? DesktopCardTheme.PrimaryInk : DesktopCardTheme.Icon);
            restore.SetResourceReference(Control.BorderBrushProperty,
                restore.IsEnabled ? DesktopCardTheme.Accent : DesktopCardTheme.Line);
            more.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
            more.IsEnabled = !disposed && !fetching && !restoring;
            back.IsEnabled = versions.IsEnabled = !disposed && !restoring;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            lifetime.Dispose();
        }

        private static DataTemplate TimelineTemplate()
        {
            var row = Visual<DockPanel>(FrameworkElement.HeightProperty, TimelineRowHeight);
            var marks = Visual<Grid>(FrameworkElement.WidthProperty, 14d, DockPanel.DockProperty, Dock.Left);
            marks.AppendChild(Visual<Border>(FrameworkElement.WidthProperty, 1d,
                Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Line),
                FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            var dotStyle = new Style(typeof(Border));
            dotStyle.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Field)));
            dotStyle.Triggers.Add(Selected(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Accent)));
            marks.AppendChild(Visual<Border>(FrameworkElement.StyleProperty, dotStyle, FrameworkElement.WidthProperty, 7d,
                FrameworkElement.HeightProperty, 7d, Border.CornerRadiusProperty, new CornerRadius(4),
                Border.BorderThicknessProperty, new Thickness(1),
                Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent),
                FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center, FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            row.AppendChild(marks);

            var selected = new Style(typeof(Border));
            selected.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
            selected.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent));
            selected.Triggers.Add(Selected(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Tint)));
            selected.Triggers.Add(Selected(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Line)));
            var plate = Visual<Border>(FrameworkElement.StyleProperty, selected, Border.CornerRadiusProperty, new CornerRadius(6),
                Border.BorderThicknessProperty, new Thickness(1),
                FrameworkElement.MarginProperty, new Thickness(2, 2, 1, 2), Border.PaddingProperty, new Thickness(7, 0, 7, 0));
            var labels = Visual<DockPanel>();
            var current = Visual<TextBlock>(DockPanel.DockProperty, Dock.Right,
                FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center, TextBlock.FontSizeProperty, 11d,
                TextBlock.TextProperty, "当前版本", TextBlock.ForegroundProperty, new DynamicResourceExtension(DesktopCardTheme.Icon),
                FrameworkElement.MarginProperty, new Thickness(7, 0, 0, 0));
            current.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(VersionRow.CurrentVisibility)));
            labels.AppendChild(current);
            var date = Visual<TextBlock>(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center,
                TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            date.SetBinding(TextBlock.TextProperty, new Binding(nameof(VersionRow.Date)));
            date.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
            date.SetBinding(ToolTipProperty, new Binding(nameof(VersionRow.ExactDate)));
            labels.AppendChild(date);
            plate.AppendChild(labels);
            row.AppendChild(plate);
            return new DataTemplate { VisualTree = row };
        }

        private sealed class VersionRow
        {
            internal JObject Snapshot { get; }
            internal bool Current { get; }
            public Visibility CurrentVisibility => Current ? Visibility.Visible : Visibility.Collapsed;
            public string Date { get; }
            public string ExactDate { get; }

            internal VersionRow(JObject snapshot, bool current)
            {
                Snapshot = snapshot;
                Current = current;
                var label = snapshot.Value<string>("updatedAt") ?? snapshot.Value<string>("createdAt");
                if (DateTimeOffset.TryParse(label, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var recorded))
                {
                    Date = recorded.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    ExactDate = recorded.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                else Date = ExactDate = "版本 " + snapshot.Value<long>("revision");
            }
        }
    }
}
