using System;
using System.Linq;
using System.Windows;
using KimNotes.utils;
using KimNotes.Windows.Capture;
using KimNotes.Windows.Notes;
using KimNotes.Windows.Presentation;
using KimNotes.Windows.Settings;
using KimNotes.Windows.Todos;
using KimNotes.Windows.Transfer;

namespace KimNotes.Windows.Runtime
{
    internal sealed class DesktopRuntime : IDisposable
    {
        private readonly Application app;
        private readonly NoteWorkspace notes = new NoteWorkspace();
        private CloudSync cloud;
        private CaptureWorkspace capture;
        private TransferWorkspace transfer;
        private TodoReminderService reminders;
        private DesktopUpdates updates;
        private GlobalHotkeys hotkeys;
        private DesktopTray tray;
        private DesktopSettings settings;

        internal DesktopRuntime(Application app) { this.app = app; }

        internal void Start()
        {
            InitConfig.InitSettings();
            DesktopCardTheme.Ensure();
            cloud = new CloudSync(openNotes: () => notes.OpenNotes);
            capture = new CaptureWorkspace(notes.InsertImage);
            notes.IsCaptureActive = () => capture.IsActive;
            transfer = new TransferWorkspace(cloud, ShowAccount);
            reminders = new TodoReminderService(ShowTodo);
            updates = new DesktopUpdates();
            hotkeys = new GlobalHotkeys(capture.StartCapture, capture.Recall, capture.Previous, capture.Next, Recall);
            settings = new DesktopSettings(notes, hotkeys, cloud, ApplyTaskbar, () =>
            {
                capture.ApplyPrivacyMode();
                transfer.ApplyBackgroundPreference();
            });
            notes.CaptureRequested += capture.StartCapture;
            notes.ToolsRequested += transfer.Show;
            notes.ToolsAvailable = () => transfer.CanOpenToolbox;
            notes.TodoRequested += () => ShowTodo(-1);
            notes.SettingsRequested += ShowSettings;
            notes.TodoCreated += todo => ShowTodo(todo.Id);
            notes.PinImageRequested += capture.ShowPreview;
            try { hotkeys.Apply(GlobalHotkeys.Read()); }
            catch (InvalidOperationException error) { Program.LogError(error); }
            tray = new DesktopTray(Recall,
                ("新建便签", () => notes.New()), ("便签列表", () => OpenFromTray(notes.ShowCatalog)),
                ("待办清单", () => OpenFromTray(() => ShowTodo(-1))), ("检查更新", () => OpenFromTray(() => updates.Check(true))),
                ("设置", () => OpenFromTray(ShowSettings)), ("退出", Exit));
            notes.Restore();
            reminders.Start();
            transfer.ApplyBackgroundPreference();
            updates.Start();
        }

        internal void Recall() => notes.Recall();

        private void OpenFromTray(Action open)
        {
            notes.ApplyRecentCardTheme();
            open();
        }

        private T Show<T>(Func<T> create) where T : Window
        {
            var window = app.Windows.OfType<T>().FirstOrDefault();
            if (window == null)
            {
                window = create();
                window.Show();
            }
            else
            {
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
            return window;
        }

        private void ShowSettings() => Show(() => new SettingsWindow(settings.Apply, ShowAccount, active =>
        {
            try { hotkeys.SetSuspended(active); }
            catch (InvalidOperationException error) { Program.LogError(error); }
        }));

        private void ShowAccount() => Show(() => new AccountWindow(cloud));

        private void ShowTodo(int id)
        {
            var window = Show(() => new TodoWindow(file =>
            {
                try { notes.Open(file); }
                catch (Exception error) { MessageBox.Show(error.Message, "无法打开便签"); }
            }));
            if (id >= 0) window.ShowTodo(id);
        }

        private void ApplyTaskbar(bool show)
        {
            foreach (var window in app.Windows.OfType<Window>().Where(window => window is SettingsWindow ||
                window is AccountWindow || window is TodoWindow || window is TodoEditor || window is NoteCatalogWindow).ToArray())
                window.ShowInTaskbar = show;
            notes.ChangeTaskbarVisibility(show);
        }

        private void Exit()
        {
            if (!CloudSync.FlushOpen() || !TodoStore.TryFlush())
            {
                MessageBox.Show("仍有内容未保存，请处理后再退出。", "小羊便签");
                return;
            }
            notes.Remember();
            DesktopSession.Exiting = true;
            app.Shutdown();
        }

        public void Dispose()
        {
            tray?.Dispose();
            hotkeys?.Dispose();
            updates?.Dispose();
            reminders?.Dispose();
            transfer?.Dispose();
            capture?.Dispose();
            cloud?.Dispose();
        }
    }
}
