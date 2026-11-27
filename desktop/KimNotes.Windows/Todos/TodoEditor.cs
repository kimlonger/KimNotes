using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using static KimNotes.Windows.Todos.TodoAppearance;
using KimNotes.Windows.Settings;

namespace KimNotes.Windows.Todos
{
    internal sealed class TodoEditor : FeatureWindow
    {
        private const int NoReminder = 0, Today = 1, Tomorrow = 2, Custom = 3;
        private readonly bool timeOnly;
        private readonly bool placeNextToAnchor;
        private readonly long scopeAtOpen = TodoStore.CurrentScopeVersion();
        private bool closed;
        private readonly TextBox content;
        private readonly DatePicker date = new DatePicker
        {
            SelectedDateFormat = DatePickerFormat.Short,
            Language = XmlLanguage.GetLanguage("zh-CN"),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        private readonly TextBox time = new TextBox { Width = 60, HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly TextBlock status = Text("", Alert);
        private readonly RadioButton[] presets = new RadioButton[4];
        private readonly DockPanel customFields = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        private readonly TextBlock reminderValue = Text("", Accent);
        private readonly Button repeatButton;
        private RepeatRule repeatChoice;
        private int presetChoice;

        internal string TodoText => content.Text.Trim();
        internal DateTime? Due { get; private set; }
        internal RepeatRule Repeat { get; private set; }
        internal TodoPriority Priority { get; private set; }
        internal int? EditingId { get; set; }
        internal bool AppliesToCurrentScope => scopeAtOpen == TodoStore.CurrentScopeVersion();
        internal event EventHandler<CancelEventArgs> Confirmed;

        internal TodoEditor(string initialText, DateTime? due = null, RepeatRule repeat = RepeatRule.None,
            bool editing = false, bool timeOnly = false)
            : this(initialText, due, repeat, editing, timeOnly, TodoPriority.Normal) { }

        internal TodoEditor(string initialText, DateTime? due, RepeatRule repeat, bool editing, bool timeOnly, TodoPriority priority)
            : base(timeOnly ? "设置提醒" : editing ? "编辑待办" : "新增待办", 301, timeOnly ? 260 : 401)
        {
            this.timeOnly = timeOnly;
            placeNextToAnchor = !editing && !timeOnly;
            WindowStartupLocation = placeNextToAnchor ? WindowStartupLocation.Manual : WindowStartupLocation.CenterOwner;
            if (!timeOnly)
            {
                MinWidth = Width;
                MinHeight = Height;
                customFields.Margin = new Thickness(0);
            }
            Priority = priority;
            repeatChoice = repeat;

            var layout = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };
            var footer = CreateFooter(editing);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
            var details = new StackPanel();
            if (timeOnly)
            {
                var viewport = Scroll(details);
                viewport.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                layout.Children.Add(viewport);
            }
            else layout.Children.Add(details);

            content = new TextBox
            {
                Text = initialText ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
            };
            if (!timeOnly)
            {
                var field = Field(content, "要做什么？", 72, true);
                field.Margin = new Thickness(0, 0, 0, 14);
                details.Children.Add(field);
            }
            details.Children.Add(CreateReminderOptions());
            if (!timeOnly) details.Children.Add(CreatePriorityOptions());

            repeatButton = CreateRepeatMenu();
            details.Children.Add(SettingRow(RepeatArrows, "重复", repeatButton));
            details.Children.Add(SettingRow(Clock, "提醒时间", reminderValue));
            details.Children.Add(CreateCustomFields());

            Body.Content = layout;
            SetDateAndTime(due ?? DateTime.Today.AddHours(18));
            int selected = due == DateTime.Today.AddHours(18) ? Today : due == DateTime.Today.AddDays(1).AddHours(9) ? Tomorrow :
                due.HasValue ? Custom : NoReminder;
            presets[selected].IsChecked = true;
            PreviewKeyDown += (sender, args) =>
            {
                if (args.Key == Key.Escape && !date.IsDropDownOpen && !repeatButton.ContextMenu.IsOpen)
                {
                    args.Handled = true;
                    Close();
                    return;
                }
                if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
                args.Handled = true;
                Commit();
            };
            Loaded += (sender, args) =>
            {
                if (timeOnly) presets[selected].Focus();
                else { content.Focus(); content.CaretIndex = content.Text.Length; }
            };
            TodoStore.ScopeChanged += CloseIfScopeChanged;
            Closed += (sender, args) =>
            {
                closed = true;
                TodoStore.ScopeChanged -= CloseIfScopeChanged;
            };
        }

        private void CloseIfScopeChanged()
        {
            if (AppliesToCurrentScope) return;
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(CloseIfScopeChanged));
                return;
            }
            if (!closed) Close();
        }

        internal void ShowBeside(Window anchor)
        {
            WindowStartupLocation = anchor == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
            if (anchor != null)
            {
                EventHandler place = null;
                place = (sender, args) =>
                {
                    SourceInitialized -= place;
                    TodoEditorPlacement.PlaceByAnchor(this, anchor, placeNextToAnchor);
                };
                SourceInitialized += place;
            }
            Show();
        }

        internal void ReportFailure(string message) => SetStatus(message);

        private UIElement CreateFooter(bool editing)
        {
            var footer = new StackPanel();
            status.TextWrapping = TextWrapping.Wrap;
            if (!timeOnly)
            {
                status.FontSize = 10.5;
                status.Height = 14;
            }
            SetStatus("");
            footer.Children.Add(status);
            var actions = new UniformGrid { Columns = 2, Margin = new Thickness(0, timeOnly ? 8 : 2, 0, 0) };
            double height = timeOnly ? 28 : 30;
            var cancel = Button("取消", Close);
            cancel.Height = height;
            cancel.Margin = new Thickness(0, 0, 4, 0);
            actions.Children.Add(cancel);
            var accept = Button(timeOnly || editing ? "保存" : "加入待办", Commit, true);
            accept.Height = height;
            accept.Margin = new Thickness(4, 0, 0, 0);
            actions.Children.Add(accept);
            footer.Children.Add(actions);
            return footer;
        }

        private UIElement CreateReminderOptions()
        {
            var options = new WrapPanel { Margin = new Thickness(0, timeOnly ? 5 : 7, 0, 0) };
            string[] names = { "不提醒", "今天 18:00", "明天 09:00", "自定义…" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var option = Pill(names[i], "ReminderPreset", Accent, BlueTint, height: timeOnly ? 23 : 26);
                option.Checked += (sender, args) => ApplyPreset(index);
                presets[index] = option;
                options.Children.Add(option);
            }
            return LabeledOptions("提醒", options);
        }

        private UIElement CreatePriorityOptions()
        {
            var options = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            foreach (TodoPriority priority in Enum.GetValues(typeof(TodoPriority)))
            {
                var tone = PriorityTone(priority);
                var option = Pill(PriorityName(priority), "TodoPriority", priority == TodoPriority.Normal ? Accent : tone,
                    PriorityTint(priority), PriorityGlyph(priority), tone, height: 26);
                option.IsChecked = priority == Priority;
                option.Checked += (sender, args) => Priority = priority;
                options.Children.Add(option);
            }
            return LabeledOptions("优先级", options);
        }

        private StackPanel LabeledOptions(string label, WrapPanel options)
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, 0, timeOnly ? 10 : 14) };
            var heading = Text(label, Muted);
            heading.FontSize = 11;
            heading.FontWeight = FontWeights.SemiBold;
            group.Children.Add(heading);
            group.Children.Add(options);
            return group;
        }

        private Button CreateRepeatMenu()
        {
            var button = FlatButton(RepeatLabel(repeatChoice), null);
            var options = CardActionMenu.Create(button, "重复提醒");
            options.MinWidth = 91;
            button.Click += (sender, args) => options.IsOpen = true;
            button.ContextMenu = options;
            AutomationProperties.SetName(button, "重复提醒");
            foreach (RepeatRule rule in Enum.GetValues(typeof(RepeatRule)))
            {
                string label = RepeatLabel(rule);
                var menuItem = CardActionMenu.Item(label, null, () =>
                {
                    repeatChoice = rule;
                    button.Content = ChoiceLabel(label);
                    UpdateRepeatChecks(options);
                });
                menuItem.Tag = rule;
                menuItem.IsCheckable = true;
                var row = new DockPanel();
                var check = VectorIcon("M5,12 L10,17 L19,7", Accent, 13);
                check.Margin = new Thickness(10, 0, 0, 0);
                check.SetBinding(VisibilityProperty, new Binding(nameof(MenuItem.IsChecked))
                {
                    Source = menuItem, Converter = new BooleanToVisibilityConverter()
                });
                DockPanel.SetDock(check, Dock.Right);
                row.Children.Add(check);
                row.Children.Add(Text(label));
                menuItem.Header = row;
                options.Items.Add(menuItem);
            }
            options.Opened += (sender, args) =>
            {
                UpdateRepeatChecks(options);
                foreach (MenuItem item in options.Items)
                    if (item.IsChecked) { item.Focus(); break; }
            };
            UpdateRepeatChecks(options);
            return button;
        }

        private void UpdateRepeatChecks(ContextMenu menu)
        {
            foreach (MenuItem item in menu.Items)
                item.IsChecked = (RepeatRule)item.Tag == repeatChoice;
        }

        private UIElement CreateCustomFields()
        {
            AutomationProperties.SetName(date, "提醒日期");
            AutomationProperties.SetName(time, "提醒时间");
            date.DateValidationError += (sender, args) =>
            {
                args.ThrowException = false;
                SetStatus("请选择有效日期。");
            };
            date.SelectedDateChanged += (sender, args) => RefreshReminderValue();
            time.TextChanged += (sender, args) => RefreshReminderValue();
            time.ToolTip = "例如 18:00";
            double height = timeOnly ? 26 : 29;
            var field = Field(time, "18:00", height);
            field.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(field, Dock.Right);
            customFields.Children.Add(field);
            StyleDatePicker(date);
            date.Height = height;
            customFields.Children.Add(date);
            return customFields;
        }

        private UIElement SettingRow(string glyph, string title, FrameworkElement value)
        {
            var panel = new DockPanel { LastChildFill = true };
            var label = IconText(glyph, title, Muted, 14);
            DockPanel.SetDock(label, Dock.Left);
            panel.Children.Add(label);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(value);
            var row = new Border
            {
                MinHeight = timeOnly ? 28 : 32, Child = panel,
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            row.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            return row;
        }

        private static Button FlatButton(string title, Action click)
        {
            var button = Button("", click);
            button.Content = ChoiceLabel(title);
            button.Height = 24;
            button.Padding = new Thickness(4, 0, 2, 0);
            button.Background = button.BorderBrush = Brushes.Transparent;
            return button;
        }

        private static StackPanel ChoiceLabel(string label)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Text(label, Muted));
            var arrow = VectorIcon(Chevron, Subtle, 12);
            arrow.Margin = new Thickness(5, 0, 0, 0);
            content.Children.Add(arrow);
            return content;
        }

        private static string RepeatLabel(RepeatRule repeat) => repeat == RepeatRule.None ? "不重复" : TodoStore.RepeatLabel(repeat);

        private void ApplyPreset(int index)
        {
            presetChoice = index;
            SetStatus("");
            bool editable = index == Custom;
            customFields.IsEnabled = editable;
            time.SetResourceReference(ForegroundProperty, editable ? DesktopCardTheme.Ink : DesktopCardTheme.Icon);
            repeatButton.IsEnabled = index != NoReminder;
            if (index == Today) SetDateAndTime(DateTime.Today.AddHours(18));
            else if (index == Tomorrow) SetDateAndTime(DateTime.Today.AddDays(1).AddHours(9));
            RefreshReminderValue();
        }

        private void RefreshReminderValue()
        {
            reminderValue.Text = presetChoice == NoReminder ? "未设置" : ReadDue() is DateTime due
                ? due.ToString("M月d日 ddd HH:mm", CultureInfo.GetCultureInfo("zh-CN")) : "自定义";
        }

        private DateTime? ReadDue(bool commitDate = false)
        {
            DateTime? selected = date.SelectedDate;
            if (commitDate && presetChoice == Custom && date.Template?.FindName("PART_TextBox", date) is TextBox input &&
                date.IsKeyboardFocusWithin)
            {
                if (!DateTime.TryParse(input.Text, date.Language.GetSpecificCulture(), DateTimeStyles.None, out var typedDate))
                    return null;
                selected = typedDate;
            }
            if (!selected.HasValue || !DateTime.TryParseExact(time.Text.Trim(), new[] { "H:mm", "HH:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
                return null;
            return selected.Value.Date + clock.TimeOfDay;
        }

        private void SetDateAndTime(DateTime value)
        {
            date.SelectedDate = value.Date;
            time.Text = value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private void SetStatus(string message)
        {
            status.Text = message;
            status.Visibility = message.Length != 0 ? Visibility.Visible : timeOnly ? Visibility.Collapsed : Visibility.Hidden;
        }

        private void Commit()
        {
            if (!AppliesToCurrentScope)
            {
                SetStatus("待办的数据范围已切换，请重新打开。");
                return;
            }
            if (!timeOnly && TodoText.Length == 0)
            {
                SetStatus("请输入待办内容。");
                content.Focus();
                return;
            }
            Due = null;
            Repeat = RepeatRule.None;
            if (presetChoice != NoReminder)
            {
                Due = ReadDue(commitDate: true);
                if (!Due.HasValue)
                {
                    SetStatus("请选择日期并填写有效时间，例如 18:00。");
                    return;
                }
                Repeat = repeatChoice;
            }
            SetStatus("");
            var confirmation = new CancelEventArgs();
            Confirmed?.Invoke(this, confirmation);
            if (!confirmation.Cancel) Close();
            else if (status.Text.Length == 0) SetStatus("暂时无法保存，请重试。");
        }
    }
}
