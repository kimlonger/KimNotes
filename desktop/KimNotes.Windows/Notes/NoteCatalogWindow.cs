using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using static KimNotes.Windows.Notes.NoteCatalogVisuals;
using Path = System.IO.Path;

namespace KimNotes.Windows.Notes
{
    internal sealed partial class NoteCatalogWindow : CardWindow
    {
        private readonly NoteWorkspace workspace;
        private readonly CloudSync cloud;
        private readonly DockPanel catalog = new DockPanel();
        private readonly TextBox search = new TextBox();
        private readonly ListBox results = new ListBox();
        private readonly TextBlock status = new TextBlock
        {
            Margin = new Thickness(3, 8, 3, 3), TextWrapping = TextWrapping.Wrap
        };
        private readonly TextBlock entryCount = new TextBlock();
        private readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        private List<Entry> entries = new List<Entry>();
        private ContextMenu openMenu;
        private bool refreshAfterMenu;
        private NoteVersionsPanel history;
        private string displayedUserId = CloudStore.UserId;
        private bool displayedIncognito = Storage.Incognito;
        private bool closed;

        internal NoteCatalogWindow(NoteWorkspace workspace) : base("便签列表")
        {
            this.workspace = workspace;
            cloud = CloudSync.Current;
            status.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Icon);
            ApplyCatalogHeader();
            AddCatalogContents();
            Body.Children.Add(catalog);

            ConfigureList(results);
            AutomationProperties.SetName(results, "便签列表");
            results.ItemTemplate = CardTemplate();
            search.TextChanged += (sender, args) => { DismissMenu(); ApplySearch(false); };
            results.KeyDown += (sender, args) =>
            {
                if (!args.Handled && args.Key == Key.Enter && results.SelectedItem is Entry entry)
                {
                    args.Handled = true;
                    OpenEntry(entry);
                }
            };
            Storage.NotesChanged += QueueReload;
            if (cloud != null) cloud.Changed += QueueReload;
            refresh.Tick += (sender, args) =>
            {
                refresh.Stop();
                if (openMenu?.IsOpen == true) refreshAfterMenu = true;
                else if (history == null) Reload();
            };
            Closed += (sender, args) =>
            {
                closed = true;
                refresh.Stop();
                DismissMenu();
                Storage.NotesChanged -= QueueReload;
                if (cloud != null) cloud.Changed -= QueueReload;
                history?.Dispose();
            };
            Loaded += (sender, args) => { Reload(); search.Focus(); };
            ApplySearch(false);
        }

        private void ApplyCatalogHeader() => SetHeaderStyle(branded: false);

        private void AddCatalogContents()
        {
            var tools = SearchField.Create(search, "搜索便签内容", entryCount);
            tools.Margin = new Thickness(9, 10, 9, 10);
            DockPanel.SetDock(tools, Dock.Top);
            catalog.Children.Add(tools);

            status.Margin = new Thickness(13, 7, 13, 3);
            DockPanel.SetDock(status, Dock.Top);
            catalog.Children.Add(status);
            results.Margin = new Thickness(9, 0, 9, 6);
            catalog.Children.Add(results);
        }

        private void QueueReload()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(QueueReload));
                return;
            }
            if (closed) return;
            if (displayedUserId != CloudStore.UserId || displayedIncognito != Storage.Incognito)
            {
                displayedUserId = CloudStore.UserId;
                displayedIncognito = Storage.Incognito;
                DismissMenu();
                entries.Clear();
                CloseVersions();
                ApplySearch(false);
            }
            refresh.Stop();
            refresh.Start();
        }

        private void Reload()
        {
            try
            {
                var index = NoteIndex.Load();
                entries = workspace.Repository.VisibleFiles().Select(path => ReadEntry(index, path)).ToList();
                index.Save();
                ApplySearch(true);
            }
            catch (Exception error)
            {
                entries.Clear();
                ApplySearch(false);
                SetStatus(error.Message);
                Program.LogError(error);
            }
        }

        private Entry ReadEntry(NoteIndex index, string path)
        {
            var file = new FileInfo(path);
            string name = file.Name;
            DateTime modified = file.LastWriteTime;
            string title, summary, text;
            if (index.TryGet(name, modified, file.Length, out var existing))
            {
                title = existing.Title; summary = existing.Summary; text = existing.SearchText;
            }
            else
            {
                try
                {
                    var document = workspace.Repository.Load(path);
                    text = new TextRange(document.ContentStart, document.ContentEnd).Text;
                    var known = NoteIndex.FromText(text, modified, file.Length);
                    title = known.Title; summary = known.Summary;
                    index.Set(name, known);
                }
                catch (Exception error) { Program.LogError(error); title = name; summary = ""; text = ""; }
            }

            bool empty = title == "（空便签）";
            if (empty) title = "空便签";
            title = cloud?.DisplayTitle(name, title) ?? title;
            var metadata = Storage.Metadata(path);
            var theme = NoteTheme.All.FirstOrDefault(item => item.Id == metadata.ThemeId) ?? NoteTheme.Current();
            var colors = CardColors(theme);
            string sync = cloud?.NoteStatus(name);
            string source = cloud?.NoteSourceDevice(name) ?? metadata.CreatedOnMachine;
            string details = string.Join(" · ", new[]
            {
                modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(source) ? null : "来自 " + source,
                sync == "同步失败" ? cloud?.NoteError(name) ?? sync : sync
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var entry = new Entry
            {
                Path = path,
                Name = title,
                Theme = theme,
                SearchText = title + "\n" + text,
                Preview = summary?.Replace("\r\n", "\n").Replace('\r', '\n').Trim(),
                Modified = modified,
                Date = modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Details = details,
                Fill = colors.Fill,
                SelectedFill = colors.SelectedFill,
                TitleInk = empty ? colors.Muted : colors.Text,
                MutedInk = colors.Muted,
                CanShowHistory = cloud?.Session != null && !cloud.NeedsLogin
            };
            entry.OpenAction = new EntryAction(() => OpenEntry(entry));
            entry.DeleteAction = new EntryAction(() => DeleteEntry(entry));
            entry.HistoryAction = new EntryAction(() => ShowVersions(entry), entry.CanShowHistory);
            return entry;
        }

        private void ApplySearch(bool preserveOffset)
        {
            double offset = preserveOffset ? ResultScroll()?.VerticalOffset ?? 0 : 0;
            var selection = (results.SelectedItem as Entry)?.Path;
            var query = search.Text.Trim();
            var items = entries.Where(item => query.Length == 0 ||
                item.SearchText.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderByDescending(item => item.Modified).ToList();
            results.ItemsSource = items;
            results.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Path, selection, StringComparison.OrdinalIgnoreCase));
            ResultScroll()?.ScrollToVerticalOffset(offset);
            SetStatus(items.Count == 0 ? (Storage.Incognito ? "无痕模式" : "没有找到便签") : "");
            entryCount.Text = $"{items.Count} 条便签";
        }

        private ScrollViewer ResultScroll()
        {
            if (VisualTreeHelper.GetChildrenCount(results) == 0) return null;
            var root = VisualTreeHelper.GetChild(results, 0);
            return root as ScrollViewer ?? (VisualTreeHelper.GetChildrenCount(root) > 0
                ? VisualTreeHelper.GetChild(root, 0) as ScrollViewer : null);
        }

        private void SetStatus(string message)
        {
            status.Text = message;
            status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OpenEntry(Entry entry)
        {
            try { workspace.Open(entry.Path); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开便签"); }
        }

        private void OpenEntryMenu(object sender, RoutedEventArgs args)
        {
            if (!(sender is Button anchor) || !(anchor.DataContext is Entry entry)) return;
            args.Handled = true;
            DismissMenu();
            results.SelectedItem = entry;
            var menu = NoteCatalogMenu.Create(anchor, entry.OpenAction, entry.HistoryAction, entry.DeleteAction);
            openMenu = menu;
            menu.Closed += (source, closedArgs) =>
            {
                if (openMenu != menu) return;
                openMenu = null;
                if (refreshAfterMenu && !closed) refresh.Start();
                refreshAfterMenu = false;
            };
            menu.IsOpen = true;
        }

        private void DismissMenu()
        {
            if (openMenu != null) openMenu.IsOpen = false;
        }

        private void DeleteEntry(Entry entry)
        {
            if (MessageBox.Show(this, "确定删除这条便签吗？", "删除便签",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try { workspace.Delete(entry.Path); Reload(); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法删除便签"); }
        }

        private void ShowVersions(Entry entry)
        {
            if (!entry.CanShowHistory || history != null) return;
            results.SelectedItem = entry;
            double offset = ResultScroll()?.VerticalOffset ?? 0;
            history = new NoteVersionsPanel(cloud, Path.GetFileName(entry.Path), entry.Name, entry.Theme, () =>
            {
                CloseVersions();
                Reload();
                ResultScroll()?.ScrollToVerticalOffset(offset);
                search.Focus();
            });
            catalog.Visibility = Visibility.Collapsed;
            Body.Children.Add(history);
            Title = "历史版本";
            SetHeaderStyle();
        }

        private void CloseVersions()
        {
            if (history == null) return;
            history.Dispose();
            Body.Children.Remove(history);
            history = null;
            catalog.Visibility = Visibility.Visible;
            Title = "便签列表";
            ApplyCatalogHeader();
        }

        private sealed class Entry
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public NoteTheme Theme { get; set; }
            public string SearchText { get; set; }
            public string Preview { get; set; }
            public Visibility PreviewVisibility => string.IsNullOrEmpty(Preview) ? Visibility.Collapsed : Visibility.Visible;
            public DateTime Modified { get; set; }
            public string Date { get; set; }
            public string Details { get; set; }
            public Brush Fill { get; set; }
            public Brush SelectedFill { get; set; }
            public Brush TitleInk { get; set; }
            public Brush MutedInk { get; set; }
            public bool CanShowHistory { get; set; }
            public ICommand OpenAction { get; set; }
            public ICommand DeleteAction { get; set; }
            public ICommand HistoryAction { get; set; }
        }

        private sealed class EntryAction : ICommand
        {
            private readonly Action action;
            private readonly bool enabled;
            internal EntryAction(Action action, bool enabled = true) { this.action = action; this.enabled = enabled; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => enabled;
            public void Execute(object parameter) { if (enabled) action(); }
        }
    }
}
