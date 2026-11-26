using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using static KimNotes.Windows.Settings.FeatureWindow;
using static KimNotes.Windows.Todos.TodoAppearance;

namespace KimNotes.Windows.Todos
{
    internal sealed class TodoCard : Border
    {
        private const string Checkmark = "M5,12 L10,17 L19,8";
        private const string Pencil = "M4,20 L5,15 L16,4 L20,8 L9,19 Z M13,7 L17,11";
        private const string Trash = "M4,7 H20 M9,7 V4 H15 V7 M6,7 L7.5,20 H16.5 L18,7 M10,10.5 L10.5,16.5 M14,10.5 L13.5,16.5";
        private const string Link = "M9,15 L15,9 M8,17 L6,19 A4.2,4.2 0 0 1 0,13 L6,7 A4.2,4.2 0 0 1 12,7 M12,17 A4.2,4.2 0 0 0 18,17 L24,11 A4.2,4.2 0 0 0 18,5 L16,7";
        private const string Ellipsis = "M5,12 h0.12 M12,12 h0.12 M19,12 h0.12";
        private const double BodyLineHeight = 16;
        private const int BodyLineLimit = 4;
        private static readonly ControlTemplate QuietButtonTemplate = CreateQuietButtonTemplate();

        private readonly TodoItem item;
        private readonly Button more;
        private readonly TextBlock timestamp;
        private readonly TextBlock bodyTip = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        private readonly string bodyTipText;

        internal event Action<bool> HoverChanged;

        internal TodoCard(TodoItem item, bool highlighted, Action edit, Action<string> openNote, Action<int> select)
        {
            this.item = item;
            Focusable = true;
            FocusVisualStyle = null;
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, DesktopCardTheme.Line);
            CornerRadius = new CornerRadius(7);
            UseLayoutRounding = true;
            MinHeight = 31;
            Margin = new Thickness(0, 0, 0, 4);
            Padding = new Thickness(4, 4, 7, 4);

            var layout = new DockPanel();
            Child = layout;
            var toggle = CreateCompletionButton(item);
            toggle.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(toggle, Dock.Left);
            layout.Children.Add(toggle);

            var details = new DockPanel { MinHeight = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            layout.Children.Add(details);

            var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Height = 18 };
            DockPanel.SetDock(trailing, Dock.Right);
            details.Children.Add(trailing);
            if (!item.Done)
            {
                AddPriorityAndRepeat(trailing, item);
            }

            // Keep the slot the same width in both states so a hover never rewraps the task.
            var actionSlot = new Grid { Margin = new Thickness(3, 0, 0, 0) };
            trailing.Children.Add(actionSlot);
            timestamp = Text("", Subtle);
            timestamp.FontSize = 10.67;
            timestamp.HorizontalAlignment = HorizontalAlignment.Right;
            actionSlot.Children.Add(timestamp);

            more = CompactButton(VectorIcon(Ellipsis, Muted, 15), "待办操作", null);
            more.Width = 20;
            more.Height = 17;
            more.HorizontalAlignment = HorizontalAlignment.Right;
            var menu = CardActionMenu.Create(more, "待办操作");
            menu.Items.Add(CardActionMenu.Item("编辑待办", VectorIcon(Pencil, Muted, 14), edit));
            if (openNote != null && !string.IsNullOrWhiteSpace(item.FromNote))
                menu.Items.Add(CardActionMenu.Item("打开来源便签", VectorIcon(Note, Muted, 14), () => openNote(item.FromNote)));
            AddNoteLink(menu, item.Notes);
            menu.Items.Add(CardActionMenu.Item("删除待办", VectorIcon(Trash, CardActionMenu.DeleteInk, 14),
                () => TodoStore.Delete(item), CardActionMenu.DeleteInk));
            more.ContextMenu = menu;
            actionSlot.Children.Add(more);

            string content = item.Text ?? "";
            if (item.Pinned && !item.Done)
            {
                var pin = VectorIcon(Pin, Accent, 11);
                pin.VerticalAlignment = VerticalAlignment.Top;
                pin.Margin = new Thickness(0, 3, 3, 0);
                DockPanel.SetDock(pin, Dock.Left);
                details.Children.Add(pin);
                AutomationProperties.SetHelpText(this, "已置顶");
            }
            var body = Text(content, item.Done ? Subtle : Ink);
            body.FontSize = 12.67;
            body.TextWrapping = TextWrapping.Wrap;
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            body.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            body.LineHeight = BodyLineHeight;
            body.MaxHeight = BodyLineHeight * BodyLineLimit;
            body.ClipToBounds = true;
            body.Margin = new Thickness(0, 0, 4, 0);
            bodyTipText = string.IsNullOrWhiteSpace(item.Notes) ? content : content + "\n\n备注\n" + item.Notes;
            body.ToolTip = bodyTip;
            if (item.Done) body.TextDecorations = TextDecorations.Strikethrough;
            details.Children.Add(body);

            void SelectCard() => select?.Invoke(item.Id);
            void ShowActions()
            {
                SelectCard();
                SetActionsVisible(true);
                menu.IsOpen = true;
            }
            MouseEnter += (sender, args) =>
            {
                SetActionsVisible(true);
                HoverChanged?.Invoke(true);
            };
            MouseLeave += (sender, args) =>
            {
                SetActionsVisible(menu.IsOpen);
                HoverChanged?.Invoke(false);
            };
            PreviewMouseLeftButtonDown += (sender, args) => SelectCard();
            MouseLeftButtonDown += (sender, args) => Focus();
            GotKeyboardFocus += (sender, args) => SelectCard();
            more.Click += (sender, args) => ShowActions();
            menu.Opened += (sender, args) => { SelectCard(); SetActionsVisible(true); };
            menu.Closed += (sender, args) => SetActionsVisible(IsMouseOver);
            KeyDown += (sender, args) =>
            {
                if (!IsKeyboardFocused || (args.Key != Key.Enter && args.Key != Key.Apps)) return;
                args.Handled = true;
                ShowActions();
            };
            SetSelected(highlighted);
            RefreshTime();
            SetActionsVisible(false);
        }

        internal void SetSelected(bool value)
        {
            SetResourceReference(BackgroundProperty, value ? DesktopCardTheme.Tint : item.Done ? DesktopCardTheme.Surface : DesktopCardTheme.Field);
        }

        internal void CloseActions() => more.ContextMenu.IsOpen = false;

        private void SetActionsVisible(bool visible)
        {
            timestamp.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
            more.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        }

        internal void RefreshTime()
        {
            var time = item.Done ? item.DoneAt ?? item.Created : item.Due;
            timestamp.Text = item.Done ? time.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) :
                item.IsOverdue ? "过期" : time.HasValue ? Format(time.Value) : "";
            timestamp.SetResourceReference(TextBlock.ForegroundProperty, item.IsOverdue ? DesktopCardTheme.Danger : DesktopCardTheme.Icon);
            string timing = time.HasValue ? (item.Done ? "完成时间 " : "提醒时间 ") + FormatFull(time.Value) : null;
            ToolTip = timing;
            bodyTip.Text = timing == null ? bodyTipText : bodyTipText + "\n\n" + timing;
        }

        private static void AddPriorityAndRepeat(Panel trailing, TodoItem item)
        {
            if (item.Priority != TodoPriority.Normal)
            {
                var icon = VectorIcon(PriorityGlyph(item.Priority), PriorityTone(item.Priority), 12, PriorityInkResource(item.Priority));
                var marker = new Border
                {
                    Child = icon, CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(1, 0, 1, 0), Margin = new Thickness(0, 0, 3, 0),
                    ToolTip = PriorityName(item.Priority), VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(marker, PriorityName(item.Priority));
                trailing.Children.Add(marker);
            }
            if (item.Repeat != RepeatRule.None)
            {
                var repeat = new Border
                {
                    Child = VectorIcon(RepeatArrows, Subtle, 11), Margin = new Thickness(0, 0, 3, 0),
                    ToolTip = TodoStore.RepeatLabel(item.Repeat), VerticalAlignment = VerticalAlignment.Center
                };
                trailing.Children.Add(repeat);
            }
        }

        private static Button CreateCompletionButton(TodoItem item)
        {
            bool wasDone = item.Done;
            DateTime? previousDue = item.Due;
            string tone = item.Done ? DesktopCardTheme.Icon : PriorityInkResource(item.Priority);
            var circle = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Background = Brushes.Transparent
            };
            circle.SetResourceReference(BorderBrushProperty, tone);
            if (wasDone)
            {
                circle.SetResourceReference(BackgroundProperty, tone);
                circle.Child = VectorIcon(Checkmark, size: 9, themedStroke: DesktopCardTheme.Surface);
            }
            var toggle = CompactButton(circle, wasDone ? "恢复待办" : "完成待办", () =>
            {
                if (item.Done == wasDone && item.Due == previousDue) TodoStore.SetDone(item, !wasDone);
            });
            toggle.Width = 18;
            return toggle;
        }

        private static void AddNoteLink(ContextMenu menu, string notes)
        {
            var link = (notes ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(text => Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null)
                .FirstOrDefault(uri => uri != null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
            if (link == null) return;
            menu.Items.Add(CardActionMenu.Item("打开备注链接", VectorIcon(Link, Muted, 14), () =>
            {
                try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); }
                catch (Win32Exception error) { Program.LogError(error); }
            }));
        }

        private static Button CompactButton(UIElement content, string name, Action action)
        {
            var button = new Button
            {
                Content = content, Height = 18, Padding = new Thickness(0), Template = QuietButtonTemplate,
                FocusVisualStyle = null, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = name
            };
            if (action != null) button.Click += (sender, args) => action();
            AutomationProperties.SetName(button, name);
            return button;
        }

        private static ControlTemplate CreateQuietButtonTemplate()
        {
            var hitArea = new FrameworkElementFactory(typeof(Border), "HitArea");
            hitArea.SetValue(BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            hitArea.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = hitArea };
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(OpacityProperty, 0.75d, "HitArea"));
            template.Triggers.Add(pressed);
            return template;
        }

        private static string FormatFull(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private static string Format(DateTime value)
        {
            DateTime today = DateTime.Today;
            if (value.Date == today) return "今天 " + value.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (value.Date == today.AddDays(1)) return "明天 " + value.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (value.Date > today && value.Date < today.AddDays(7))
                return value.ToString("ddd HH:mm", CultureInfo.GetCultureInfo("zh-CN"));
            return FormatFull(value);
        }
    }
}
