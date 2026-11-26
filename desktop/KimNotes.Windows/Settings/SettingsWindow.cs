using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using Form = System.Windows.Forms;
using ShapePath = System.Windows.Shapes.Path;

namespace KimNotes.Windows.Settings
{
    internal sealed class SettingsWindow : FeatureWindow
    {
        private static readonly string[] HotkeyNames = { "快速截屏", "最近便签", "唤回贴图", "上一张", "下一张" };
        private static readonly string[] HotkeyKeys = { "shortcutKey", InitConfig.LatestNoteHotkeyKey, "recallKey", "previousImageKey", "nextImageKey" };
        private static readonly string[] ToggleKeys = { "checkBox1", "checkBox3", "showInTaskbar", InitConfig.FileTransferBackgroundReceiveKey };
        private static readonly string[] ToggleLabels = { "开机启动", "无痕模式", "显示任务栏", "文件传输" };
        private static readonly ControlTemplate PathInputTemplate = CreatePathInputTemplate();
        private static readonly ControlTemplate OptionTemplate = CreateOptionTemplate();
        private static readonly ControlTemplate ReminderTemplate = CreateOptionTemplate(radio: true);

        private readonly Func<Dictionary<string, string>, string> applySettings;
        private readonly Action<bool> captureHotkeys;
        private readonly CheckBox[] toggles = new CheckBox[ToggleKeys.Length];
        private readonly Button[] keyButtons = new Button[HotkeyKeys.Length];
        private readonly string[] keyValues = new string[HotkeyKeys.Length];
        private readonly TextBox notesPath = new TextBox();
        private readonly TextBox imagesPath = new TextBox();
        private readonly RadioButton popup = new RadioButton { Content = "仅弹窗", GroupName = "ReminderMode" };
        private readonly RadioButton sound = new RadioButton { Content = "弹窗+铃声", GroupName = "ReminderMode" };
        private readonly TextBlock status = Text("", Warning);
        private int capture = -1;

        internal SettingsWindow(Func<Dictionary<string, string>, string> applySettings, Action openAccount = null, Action<bool> captureHotkeys = null)
            : base("设置", 301, 401)
        {
            this.applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));
            this.captureHotkeys = captureHotkeys;
            var layout = new DockPanel { Margin = new Thickness(12, 7, 12, 8) };
            var footer = new StackPanel();
            status.TextWrapping = TextWrapping.Wrap;
            footer.Children.Add(status);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 5, 0, 0) };
            var reset = Button("恢复默认", () => SaveSettings(true));
            reset.Margin = new Thickness(0, 0, 4, 0);
            buttons.Children.Add(reset);
            var save = Button("保存配置", () => SaveSettings(false), true);
            save.Margin = new Thickness(4, 0, 0, 0);
            buttons.Children.Add(save);
            footer.Children.Add(buttons);

            var content = new StackPanel();
            var scroll = Scroll(content);
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            layout.Children.Add(scroll);

            var firstRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            if (openAccount != null) AddAccountEntry(firstRow, openAccount);
            content.Children.Add(firstRow);

            var options = new UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < toggles.Length; i++)
            {
                toggles[i] = new CheckBox
                {
                    Content = ToggleLabels[i], Template = OptionTemplate, FocusVisualStyle = null,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                toggles[i].SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
                options.Children.Add(toggles[i]);
            }
            toggles[3].ToolTip = "登录并开启后，在托盘运行时也能接收文件请求";
            firstRow.Children.Add(options);

            var reminders = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 5) };
            popup.SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            sound.SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            popup.Template = sound.Template = ReminderTemplate;
            reminders.Children.Add(Text("待办提醒", Muted));
            reminders.Children.Add(popup);
            reminders.Children.Add(sound);
            content.Children.Add(reminders);

            content.Children.Add(Text("点快捷键后按键，Delete 停用", Muted));
            for (int i = 0; i < keyButtons.Length; i++)
            {
                int index = i;
                var button = Button("", () => BeginCapture(index));
                button.Width = 111;
                button.Height = 22;
                AutomationProperties.SetName(button, HotkeyNames[i]);
                keyButtons[i] = button;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                DockPanel.SetDock(button, Dock.Right);
                row.Children.Add(button);
                row.Children.Add(Text(HotkeyNames[i], Muted));
                content.Children.Add(row);
            }

            content.Children.Add(PathField("笔记", notesPath));
            content.Children.Add(PathField("图片", imagesPath));
            var version = typeof(SettingsWindow).Assembly.GetName().Version.ToString(3);
            var about = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var website = Button("访问官网", () => SetStatus(OpenWebsite("")));
            website.Height = 22;
            DockPanel.SetDock(website, Dock.Right);
            about.Children.Add(website);
            about.Children.Add(Text("版本 " + version, Muted));
            content.Children.Add(about);
            Body.Content = layout;
            PreviewKeyDown += CaptureKey;
            Deactivated += (sender, args) => EndCapture();
            Closed += (sender, args) => EndCapture();
            LoadValues();
        }

        private void AddAccountEntry(DockPanel content, Action openAccount)
        {
            var sync = CloudSync.Current;
            var account = Button("未登录", openAccount);
            account.Width = 61;
            account.Height = 59;
            account.Padding = new Thickness(0);
            account.Margin = new Thickness(5, 0, 0, 0);
            account.Background = Brushes.Transparent;
            account.BorderBrush = Brushes.Transparent;
            account.SetResourceReference(ForegroundProperty, DesktopCardTheme.Accent);
            var avatar = new Image { Width = 34, Height = 34, Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed,
                Clip = new EllipseGeometry(new Point(17, 17), 17, 17) };
            var defaultAvatar = new TitleLogo { Width = 24, Height = 24 };
            var avatarSlot = new Grid { Width = 34, Height = 34 };
            var avatarBackground = new Border { CornerRadius = new CornerRadius(17), Child = defaultAvatar };
            avatarBackground.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Field);
            avatarSlot.Children.Add(avatarBackground);
            avatarSlot.Children.Add(avatar);
            var status = Text("未登录", Accent);
            status.HorizontalAlignment = HorizontalAlignment.Center;
            var identity = new StackPanel();
            identity.Children.Add(avatarSlot);
            identity.Children.Add(status);
            account.Content = identity;
            AutomationProperties.SetName(account, "登录账号");
            DockPanel.SetDock(account, Dock.Right);
            content.Children.Add(account);
            if (sync == null) return;
            Action refresh = () =>
            {
                bool signedIn = sync.Session != null && !sync.NeedsLogin;
                status.Text = signedIn ? "已登录" : "未登录";
                AutomationProperties.SetName(account, signedIn ? "已登录，打开账号" : "登录账号");
                avatar.Source = signedIn ? Avatar(sync.AvatarImage) : null;
                avatar.Visibility = avatar.Source == null ? Visibility.Collapsed : Visibility.Visible;
            };
            Action changed = () => Dispatcher.BeginInvoke(refresh);
            sync.Changed += changed;
            Closed += (sender, args) => sync.Changed -= changed;
            refresh();
        }

        private static FrameworkElement PathField(string label, TextBox field)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var description = Text(label, Muted);
            description.Width = 34;
            row.Children.Add(description);

            var browse = Button("", () =>
            {
                using (var picker = new Form.FolderBrowserDialog { SelectedPath = field.Text, Description = "选择" + label + "目录" })
                    if (picker.ShowDialog() == Form.DialogResult.OK) field.Text = picker.SelectedPath;
            });
            browse.Content = VectorIcon("M3,7 H9 L11,9 H21 V19 H3 Z M3,11 H21", Accent, 16);
            browse.Width = 27;
            browse.Height = 20;
            browse.Margin = new Thickness(1, 0, 1, 0);
            browse.Padding = new Thickness(0);
            browse.Background = Brushes.Transparent;
            browse.BorderBrush = Brushes.Transparent;
            browse.ToolTip = "选择" + label + "目录";
            AutomationProperties.SetName(browse, (string)browse.ToolTip);

            AutomationProperties.SetName(field, label + "目录");
            field.Template = PathInputTemplate;
            field.Padding = new Thickness(0);
            field.SetResourceReference(ForegroundProperty, DesktopCardTheme.Ink);
            field.SetBinding(ToolTipProperty, new Binding(nameof(TextBox.Text)) { RelativeSource = RelativeSource.Self });

            var input = new Grid();
            input.ColumnDefinitions.Add(new ColumnDefinition());
            input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            input.Children.Add(field);
            Grid.SetColumn(browse, 1);
            input.Children.Add(browse);

            var outlineStyle = new Style(typeof(Border));
            outlineStyle.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Line)));
            var focused = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent)));
            outlineStyle.Triggers.Add(focused);
            var pathBox = new Border
            {
                Height = 24, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Style = outlineStyle, Child = input
            };
            pathBox.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Field);
            row.Children.Add(pathBox);
            return row;
        }

        private static ControlTemplate CreatePathInputTemplate()
        {
            var layout = new FrameworkElementFactory(typeof(Grid));
            layout.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var textInset = new Thickness(6, 0, 5, 0);
            var content = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            content.SetValue(FocusableProperty, false);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(MarginProperty, textInset);
            content.SetValue(OpacityProperty, 0d);
            content.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            layout.AppendChild(content);

            var preview = new FrameworkElementFactory(typeof(TextBlock), "PathPreview");
            preview.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(TextBox.TextProperty));
            preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            preview.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            preview.SetValue(MarginProperty, textInset);
            preview.SetValue(IsHitTestVisibleProperty, false);
            layout.AppendChild(preview);

            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = layout };
            var editing = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
            editing.Setters.Add(new Setter(OpacityProperty, 1d, "PART_ContentHost"));
            editing.Setters.Add(new Setter(VisibilityProperty, Visibility.Hidden, "PathPreview"));
            template.Triggers.Add(editing);
            return template;
        }

        private static ControlTemplate CreateOptionTemplate(bool radio = false)
        {
            var layout = new FrameworkElementFactory(typeof(DockPanel));
            layout.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var square = new FrameworkElementFactory(typeof(Border), "Mark");
            square.SetValue(DockPanel.DockProperty, Dock.Left);
            square.SetValue(WidthProperty, 14d);
            square.SetValue(HeightProperty, 14d);
            square.SetValue(Border.CornerRadiusProperty, new CornerRadius(radio ? 7 : 3));
            square.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            square.SetResourceReference(Border.BorderBrushProperty, DesktopCardTheme.Line);
            square.SetResourceReference(Border.BackgroundProperty, DesktopCardTheme.Field);
            square.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
            square.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            layout.AppendChild(square);

            var tick = new FrameworkElementFactory(typeof(ShapePath), "Tick");
            tick.SetValue(ShapePath.DataProperty, radio ? (Geometry)new EllipseGeometry(new Point(3, 3), 3, 3) : Geometry.Parse("M1,4 L3.5,6.5 L8,2"));
            tick.SetResourceReference(radio ? ShapePath.FillProperty : ShapePath.StrokeProperty, DesktopCardTheme.Accent);
            if (!radio) tick.SetValue(ShapePath.StrokeThicknessProperty, 1.5d);
            tick.SetValue(ShapePath.StrokeStartLineCapProperty, PenLineCap.Round);
            tick.SetValue(ShapePath.StrokeEndLineCapProperty, PenLineCap.Round);
            tick.SetValue(ShapePath.StrokeLineJoinProperty, PenLineJoin.Round);
            tick.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            tick.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            tick.SetValue(VisibilityProperty, Visibility.Hidden);
            square.AppendChild(tick);

            var label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            label.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            label.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            layout.AppendChild(label);

            var template = new ControlTemplate(radio ? typeof(RadioButton) : typeof(CheckBox)) { VisualTree = layout };
            var checkedStyle = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedStyle.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(DesktopCardTheme.Tint), "Mark"));
            checkedStyle.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent), "Mark"));
            checkedStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "Tick"));
            template.Triggers.Add(checkedStyle);
            foreach (var property in new[] { IsMouseOverProperty, IsKeyboardFocusedProperty })
            {
                var cue = new Trigger { Property = property, Value = true };
                cue.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(DesktopCardTheme.Accent), "Mark"));
                template.Triggers.Add(cue);
            }
            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(OpacityProperty, 0.5d));
            template.Triggers.Add(disabled);
            return template;
        }

        private void LoadValues(Dictionary<string, string> defaults = null)
        {
            string Value(string key) => defaults != null && defaults.TryGetValue(key, out var value)
                ? value : InitConfig.GetConfigValue(key);
            for (int i = 0; i < toggles.Length; i++)
                toggles[i].IsChecked = bool.TryParse(Value(ToggleKeys[i]), out var enabled) && enabled;
            sound.IsChecked = Value("remindMode") == "1";
            popup.IsChecked = sound.IsChecked != true;
            notesPath.Text = Value("notesPath") ?? "";
            imagesPath.Text = Value("imagesPath") ?? "";
            for (int i = 0; i < keyValues.Length; i++) keyValues[i] = Value(HotkeyKeys[i]) ?? "None";
            EndCapture();
        }

        private void BeginCapture(int index)
        {
            if (capture < 0) captureHotkeys?.Invoke(true);
            capture = index;
            UpdateKeycaps();
        }

        private void EndCapture()
        {
            if (capture >= 0)
            {
                capture = -1;
                captureHotkeys?.Invoke(false);
            }
            UpdateKeycaps();
        }

        private void CaptureKey(object sender, KeyEventArgs args)
        {
            if (capture < 0) return;
            args.Handled = true;
            var key = args.Key == Key.System ? args.SystemKey : args.Key;
            if (key == Key.Escape) { EndCapture(); return; }
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin) return;

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0) return;
            string value = key == Key.Back || key == Key.Delete ? "None" : ((Form.Keys)virtualKey).ToString();
            int conflict = Array.FindIndex(keyValues, existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase));
            if (value != "None" && conflict >= 0 && conflict != capture)
            {
                SetStatus("与“" + HotkeyNames[conflict] + "”重复。");
                return;
            }
            keyValues[capture] = value;
            SetStatus(null);
            EndCapture();
        }

        private void UpdateKeycaps()
        {
            for (int i = 0; i < keyButtons.Length; i++)
                if (keyButtons[i] != null) keyButtons[i].Content = capture == i ? "Ctrl + …" : FormatKey(keyValues[i]);
        }

        private static string FormatKey(string key)
        {
            switch (key)
            {
                case null:
                case "None": return "已停用";
                case "Left": return "Ctrl + ←";
                case "Right": return "Ctrl + →";
                case "Up": return "Ctrl + ↑";
                case "Down": return "Ctrl + ↓";
                default: return "Ctrl + " + (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key.Substring(1) : key);
            }
        }

        private void SaveSettings(bool restoreDefaults)
        {
            EndCapture();
            var defaults = restoreDefaults ? InitConfig.DefaultValues() : null;
            if (defaults != null) LoadValues(defaults);
            if (keyValues.Where(key => key != "None").GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            {
                SetStatus("快捷键重复，请选择不同的键位。");
                return;
            }
            var values = defaults ?? new Dictionary<string, string>();
            values["remindMode"] = sound.IsChecked == true ? "1" : "0";
            values["pasteShortcutVersion"] = "3";
            try
            {
                values["notesPath"] = Path.GetFullPath(notesPath.Text.Trim());
                values["imagesPath"] = Path.GetFullPath(imagesPath.Text.Trim());
                for (int i = 0; i < keyValues.Length; i++) values[HotkeyKeys[i]] = keyValues[i];
                for (int i = 0; i < toggles.Length; i++) values[ToggleKeys[i]] = (toggles[i].IsChecked == true).ToString();
                var error = applySettings(values);
                if (string.IsNullOrEmpty(error)) Close();
                else SetStatus(error);
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException || error is NotSupportedException)
            {
                SetStatus("无法保存设置，请检查目录是否有效和可写。");
            }
        }

        private void SetStatus(string value) => status.Text = value ?? "";
    }
}
