using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Settings;
using Microsoft.Win32;

namespace KimNotes.Windows.Todos
{
    internal sealed class TodoReminderService : IDisposable
    {
        private readonly Action<int> showTodo;
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private readonly HashSet<ReminderWindow> popups = new HashSet<ReminderWindow>();
        private readonly DispatcherTimer timer;
        private MemoryStream soundData;
        private SoundPlayer player;
        private bool started, checking, disposed;
        private int queued;

        internal TodoReminderService(Action<int> showTodo)
        {
            this.showTodo = showTodo ?? throw new ArgumentNullException(nameof(showTodo));
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += (sender, args) => Check();
        }

        internal void Start()
        {
            if (started || disposed) return;
            started = true;
            TodoStore.Changed += AfterStoreChange;
            SystemEvents.PowerModeChanged += OnPowerMode;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            timer.Start();
            QueueCheck();
        }

        private void OnPowerMode(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Resume) QueueCheck();
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionUnlock) QueueCheck();
        }

        private void AfterStoreChange()
        {
            if (dispatcher.CheckAccess()) CloseOtherScopeReminders();
            if (!checking) QueueCheck();
        }

        private void CloseOtherScopeReminders()
        {
            foreach (var popup in popups.Where(window => !window.AppliesToCurrentScope).ToArray())
                popup.Close();
        }

        private void QueueCheck()
        {
            if (dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref queued, 0);
                Check();
            }), DispatcherPriority.Background);
        }

        private void Check()
        {
            if (!started || checking || disposed) return;
            checking = true;
            try
            {
                CloseOtherScopeReminders();
                var due = TodoStore.TakeDue();
                if (due.Count == 0) return;
                var popup = new ReminderWindow(due, showTodo);
                popups.Add(popup);
                popup.Closed += (sender, args) => popups.Remove(popup);
                try { popup.Show(); }
                catch
                {
                    popups.Remove(popup);
                    throw;
                }
                TodoStore.MarkReminded(due);
                PlaySound();
            }
            catch (Exception error)
            {
                Trace.TraceWarning("Todo reminder: " + error.GetType().Name);
            }
            finally
            {
                checking = false;
                try { timer.Interval = TimeSpan.FromMilliseconds(TodoStore.NextCheckInterval(DateTime.Now)); }
                catch (Exception error)
                {
                    Trace.TraceWarning("Todo reminder schedule: " + error.GetType().Name);
                    timer.Interval = TimeSpan.FromSeconds(30);
                }
            }
        }

        private void PlaySound()
        {
            if (InitConfig.GetConfigValue("remindMode") != "1") return;
            try
            {
                if (player == null)
                {
                    using (var source = typeof(TodoReminderService).Assembly.GetManifestResourceStream("KimNotes.remind.wav"))
                    {
                        if (source == null) { SystemSounds.Exclamation.Play(); return; }
                        soundData = new MemoryStream();
                        source.CopyTo(soundData);
                        soundData.Position = 0;
                        player = new SoundPlayer(soundData);
                    }
                }
                player.Play();
            }
            catch (Exception error) when (error is IOException || error is InvalidOperationException || error is TimeoutException)
            {
                SystemSounds.Exclamation.Play();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            if (started)
            {
                TodoStore.Changed -= AfterStoreChange;
                SystemEvents.PowerModeChanged -= OnPowerMode;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
            foreach (var popup in popups.ToArray()) popup.Close();
            popups.Clear();
            player?.Dispose();
            soundData?.Dispose();
        }

        private sealed class ReminderWindow : FeatureWindow
        {
            private readonly bool incognitoAtOpen = Storage.Incognito;
            private readonly string userAtOpen = CloudStore.UserId;

            internal bool AppliesToCurrentScope => incognitoAtOpen == Storage.Incognito &&
                string.Equals(userAtOpen, CloudStore.UserId, StringComparison.Ordinal);

            internal ReminderWindow(IReadOnlyList<TodoItem> due, Action<int> showTodo)
                : base("待办提醒", 336, 176)
            {
                var occurrences = due.Select(item => (item.Id, item.Created, item.Due)).ToArray();
                MinHeight = 0;
                MaxHeight = 448;
                SizeToContent = SizeToContent.Height;
                ShowActivated = false;
                ShowInTaskbar = false;
                Topmost = true;
                WindowStartupLocation = WindowStartupLocation.Manual;
                var layout = new DockPanel { Margin = new Thickness(13) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
                DockPanel.SetDock(buttons, Dock.Bottom);
                layout.Children.Add(buttons);
                buttons.Children.Add(Button("完成", () =>
                {
                    Close();
                    foreach (var occurrence in occurrences)
                    {
                        if (!AppliesToCurrentScope) return;
                        var current = FindTask(occurrence.Id, occurrence.Created);
                        if (current != null && !current.Done && current.Due == occurrence.Due)
                            TodoStore.SetDone(current, true);
                    }
                }));
                var view = Button("查看", () =>
                {
                    Close();
                    if (!AppliesToCurrentScope) return;
                    var current = occurrences.Select(item => FindTask(item.Id, item.Created)).FirstOrDefault(item => item != null);
                    if (current != null) showTodo(current.Id);
                }, true);
                view.Margin = new Thickness(6, 0, 0, 0);
                buttons.Children.Add(view);
                var description = Text(string.Join("\n", due.Select(item => "· " + item.Text)));
                description.TextWrapping = TextWrapping.Wrap;
                description.FontSize = 13;
                var scroll = Scroll(description);
                scroll.MaxHeight = 337;
                layout.Children.Add(scroll);
                Body.Content = layout;
                Loaded += (sender, args) =>
                {
                    var workArea = SystemParameters.WorkArea;
                    Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - 12);
                    Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - 12);
                };
            }

            private static TodoItem FindTask(int id, DateTime created)
            {
                var item = TodoStore.Find(id);
                return item != null && item.Created == created ? item : null;
            }
        }
    }
}
