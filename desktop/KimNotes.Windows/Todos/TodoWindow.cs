using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using KimNotes.Windows.Settings;
using static KimNotes.Windows.Todos.TodoAppearance;

namespace KimNotes.Windows.Todos
{
    internal sealed class TodoWindow : FeatureWindow
    {
        private readonly TextBox search = new TextBox();
        private readonly TextBlock entryCount = new TextBlock();
        private readonly StackPanel cards = new StackPanel();
        private readonly ScrollViewer viewport;
        private readonly Dictionary<int, TodoCard> visibleCards = new Dictionary<int, TodoCard>();
        private readonly Action<string> openSourceNote;
        private readonly DispatcherTimer statusTimer;
        private bool closed, refreshQueued, completedExpanded = true;
        private int highlighted = -1;
        private TodoCard hoveredCard;
        private long displayedScope = TodoStore.CurrentScopeVersion();

        internal TodoWindow(Action<string> openSourceNote = null) : base("待办清单", 301, 401)
        {
            this.openSourceNote = openSourceNote;
            MinHeight = 305;
            var pin = AddCaptionButton("", "置顶待办清单", () => Topmost = !Topmost);
            pin.Content = VectorIcon(Pin, Muted, 17);
            pin.Click += (sender, args) =>
            {
                pin.Content = VectorIcon(Pin, Topmost ? Accent : Muted, 17);
                AutomationProperties.SetItemStatus(pin, Topmost ? "已置顶" : "未置顶");
                if (Topmost) pin.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Accent);
                else pin.BorderBrush = Brushes.Transparent;
            };

            var layout = new DockPanel { Margin = new Thickness(9) };
            var searchBar = CreateSearchBar();
            DockPanel.SetDock(searchBar, Dock.Top);
            layout.Children.Add(searchBar);
            viewport = Scroll(cards);
            viewport.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            layout.Children.Add(viewport);
            Body.Content = layout;

            var add = new RoutedCommand();
            InputBindings.Add(new KeyBinding(add, Key.Enter, ModifierKeys.Control));
            CommandBindings.Add(new CommandBinding(add, (sender, args) => AddTodo()));
            search.TextChanged += (sender, args) => Render(false);
            TodoStore.Changed += QueueRefresh;
            TodoStore.ScopeChanged += ResetScope;
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            statusTimer.Tick += (sender, args) =>
            {
                foreach (var card in visibleCards.Values) card.RefreshTime();
            };
            statusTimer.Start();
            Closed += (sender, args) =>
            {
                closed = true;
                statusTimer.Stop();
                TodoStore.Changed -= QueueRefresh;
                TodoStore.ScopeChanged -= ResetScope;
            };
            Render(false);
        }

        private UIElement CreateSearchBar()
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var add = Button("", AddTodo, true);
            add.Content = VectorIcon(Plus, size: 17, themedStroke: DesktopCardTheme.PrimaryInk);
            add.Width = SearchField.Height;
            add.Height = SearchField.Height;
            add.Padding = new Thickness(0);
            add.Margin = new Thickness(7, 0, 0, 0);
            add.ToolTip = "新增待办（Ctrl + Enter）";
            AutomationProperties.SetName(add, "新增待办");
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);

            row.Children.Add(SearchField.Create(search, "搜索待办", entryCount));
            return row;
        }

        internal void ShowTodo(int id)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (id < 0) return;
            if (visibleCards.TryGetValue(id, out var visible))
            {
                OnCardSelected(id);
                visible.BringIntoView();
                return;
            }

            highlighted = id;
            if (TodoStore.Find(id)?.Done == true) completedExpanded = true;
            if (search.Text.Length != 0) search.Clear();
            else Render(true);
            if (visibleCards.TryGetValue(id, out var card)) card.BringIntoView();
        }

        private void OnCardSelected(int id)
        {
            if (id == highlighted || !visibleCards.ContainsKey(id)) return;
            var previousHighlight = VisibleHighlight();
            var previousSelection = SelectedCard();
            highlighted = id;
            previousSelection?.CloseActions();
            UpdateHighlight(previousHighlight);
        }

        private void OnCardHoverChanged(int id, TodoCard card, bool isHovered)
        {
            if (!visibleCards.TryGetValue(id, out var current) || current != card || (!isHovered && hoveredCard != card))
                return;
            var previous = VisibleHighlight();
            hoveredCard = isHovered ? card : null;
            UpdateHighlight(previous);
        }

        private TodoCard SelectedCard() => visibleCards.TryGetValue(highlighted, out var card) ? card : null;

        private TodoCard VisibleHighlight() => hoveredCard ?? SelectedCard();

        private void UpdateHighlight(TodoCard previous)
        {
            var current = VisibleHighlight();
            if (previous == current) return;
            previous?.SetSelected(false);
            current?.SetSelected(true);
        }

        private void QueueRefresh()
        {
            if (closed || refreshQueued) return;
            refreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                refreshQueued = false;
                if (!closed) Render(true);
            }));
        }

        private void ResetScope()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ResetScope));
                return;
            }
            var current = TodoStore.CurrentScopeVersion();
            if (closed || displayedScope == current) return;
            displayedScope = current;
            foreach (var card in visibleCards.Values) card.CloseActions();
            highlighted = -1;
            if (search.Text.Length != 0) search.Clear();
            else Render(false);
        }

        private void Render(bool preservePosition)
        {
            double offset = preservePosition ? viewport.VerticalOffset : 0;
            hoveredCard = null;
            cards.Children.Clear();
            visibleCards.Clear();
            string term = search.Text.Trim();
            var active = TodoStore.Active().Where(item => Matches(item, term)).ToList();
            var completed = TodoStore.Done().Where(item => Matches(item, term)).ToList();
            entryCount.Text = active.Count + " 条待办";
            RenderGroup("进行中", active, completed: false);
            RenderGroup("已完成", completed, completed: true);

            if (active.Count + completed.Count == 0)
            {
                var info = Text(term.Length == 0 ? "暂无待办" : "没有匹配的待办", Muted);
                info.Margin = new Thickness(0, 14, 0, 0);
                info.HorizontalAlignment = HorizontalAlignment.Center;
                cards.Children.Add(info);
            }
            viewport.ScrollToVerticalOffset(offset);
        }

        private static bool Matches(TodoItem item, string term) =>
            term.Length == 0 || Contains(item.Text, term) || Contains(item.Notes, term) || Contains(item.FromNoteTitle, term);

        private static bool Contains(string value, string term) =>
            value?.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0;

        private void RenderGroup(string name, IList<TodoItem> items, bool completed)
        {
            if (items.Count == 0) return;
            var header = new DockPanel { Margin = new Thickness(2, completed ? 7 : 2, 2, 3), Height = 19 };
            if (completed) header.Children.Add(CreateCompletedToggle());
            var title = Text(name + " " + items.Count, Muted);
            title.FontSize = 10.5;
            title.FontWeight = FontWeights.SemiBold;
            header.Children.Add(title);
            cards.Children.Add(header);

            if (completed && !completedExpanded) return;
            foreach (var item in items)
            {
                var card = new TodoCard(item, item.Id == highlighted, () => EditTodo(item), openSourceNote, OnCardSelected);
                card.HoverChanged += isHovered => OnCardHoverChanged(item.Id, card, isHovered);
                visibleCards.Add(item.Id, card);
                cards.Children.Add(card);
            }
        }

        private Button CreateCompletedToggle()
        {
            var toggle = Button("", () =>
            {
                completedExpanded = !completedExpanded;
                Render(true);
            });
            string label = completedExpanded ? "收起" : "展开";
            string glyph = completedExpanded ? "M7,15 L12,10 L17,15" : Chevron;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Text(label, Subtle));
            var chevron = VectorIcon(glyph, Subtle, 10);
            chevron.Margin = new Thickness(3, 0, 0, 0);
            content.Children.Add(chevron);
            toggle.Content = content;
            toggle.FontSize = 9.5;
            toggle.Background = toggle.BorderBrush = Brushes.Transparent;
            toggle.Height = 19;
            toggle.Padding = new Thickness(4, 0, 0, 0);
            AutomationProperties.SetName(toggle, label + "已完成待办");
            DockPanel.SetDock(toggle, Dock.Right);
            return toggle;
        }

        private void AddTodo()
        {
            var editor = new TodoEditor("");
            editor.Confirmed += (sender, args) =>
            {
                var item = TodoStore.AddDetailed(editor.TodoText, editor.Due, editor.Repeat, editor.Priority, null, false);
                if (!closed) highlighted = item.Id;
            };
            editor.ShowBeside(this);
        }

        private void EditTodo(TodoItem item)
        {
            var id = item.Id;
            if (!ReferenceEquals(item, TodoStore.Find(id))) return;
            var existing = Application.Current.Windows.OfType<TodoEditor>()
                .FirstOrDefault(editor => editor.EditingId == id && editor.AppliesToCurrentScope);
            if (existing != null)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var original = (item.Created, item.Text, item.Due, item.Repeat, item.Priority);
            var editorWindow = new TodoEditor(item.Text, item.Due, item.Repeat, true, false, item.Priority) { EditingId = id };
            editorWindow.Confirmed += (sender, args) =>
            {
                var current = TodoStore.Find(id);
                if (current == null || (current.Created, current.Text, current.Due, current.Repeat, current.Priority) != original)
                {
                    args.Cancel = true;
                    editorWindow.ReportFailure("这条待办已更新，请从清单重新打开。");
                    return;
                }
                TodoStore.SaveDetails(current, editorWindow.TodoText, editorWindow.Due, editorWindow.Repeat,
                    editorWindow.Priority, current.Notes, current.Pinned);
            };
            editorWindow.ShowBeside(this);
        }
    }
}
