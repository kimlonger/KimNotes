using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using DrawingPoint = System.Drawing.Point;

namespace KimNotes.Windows.Notes
{
    internal sealed class NoteWorkspace
    {
        private readonly List<NoteWindow> windows = new List<NoteWindow>();
        private NoteWindow recent;
        private NoteCatalogWindow catalog;
        internal NoteRepository Repository { get; } = new NoteRepository();
        internal Func<bool> IsCaptureActive { get; set; }
        internal Func<bool> ToolsAvailable { get; set; }
        internal bool CanOpenTools => ToolsAvailable?.Invoke() == true;
        internal IEnumerable<ICloudNote> OpenNotes => windows.ToArray();
        internal event Action CaptureRequested;
        internal event Action ToolsRequested;
        internal event Action TodoRequested;
        internal event Action SettingsRequested;
        internal event Action<TodoItem> TodoCreated;
        internal event Action<BitmapSource> PinImageRequested;
        internal Action Capture => () => CaptureRequested?.Invoke();
        internal Action ShowTools => () => ToolsRequested?.Invoke();
        internal Action ShowTodos => () => TodoRequested?.Invoke();
        internal Action ShowSettings => () => SettingsRequested?.Invoke();

        internal NoteWindow New(string text = null) => OpenWindow(null, text);

        internal NoteEdgeTab CreateEdgeTab(NoteWindow note, DrawingPoint cursor, DrawingPoint travel, string title, NoteTheme theme)
        {
            var tabs = windows.Where(window => window != note).Select(window => window.DockedTab).Where(tab => tab != null).ToArray();
            var indexes = new HashSet<int>(tabs.Select(tab => tab.Index));
            int index = 1;
            while (indexes.Contains(index)) index++;
            return NoteEdgeTab.TryCreate(note, cursor, travel, index, tabs.Select(tab => tab.PixelBounds), title, note.Reveal, theme);
        }

        internal NoteWindow Open(string file)
        {
            var path = Repository.Resolve(file);
            var existing = windows.FirstOrDefault(note => string.Equals(note.FilePath, path, StringComparison.OrdinalIgnoreCase));
            return existing == null ? OpenWindow(path, null) : Reveal(existing);
        }

        internal void Restore()
        {
            var session = DesktopSession.Read();
            DesktopSession.Restoring = true;
            try
            {
                if (session != null)
                    foreach (var path in session)
                        try { Open(path); } catch (Exception error) { Program.LogError(error); }
                else if (!Storage.Incognito)
                {
                    var file = Repository.VisibleFiles().FirstOrDefault();
                    if (file != null)
                        try { Open(file); } catch (Exception error) { Program.LogError(error); }
                }
                if (windows.Count == 0) New();
            }
            finally
            {
                DesktopSession.Restoring = false;
                Remember();
            }
        }

        private NoteWindow OpenWindow(string path, string text)
        {
            var window = new NoteWindow();
            window.Connect(this, path, text);
            windows.Add(window);
            window.Activated += (sender, args) => recent = window;
            window.Closed += (sender, args) =>
            {
                windows.Remove(window);
                if (recent == window) recent = windows.LastOrDefault();
                Remember();
            };
            recent = window;
            window.ApplyCardTheme();
            window.Show();
            Remember();
            return window;
        }

        private static NoteWindow Reveal(NoteWindow window)
        {
            window.ApplyCardTheme();
            window.Reveal();
            return window;
        }

        internal void Recall()
        {
            if (recent == null) New();
            else Reveal(recent);
        }

        internal void ApplyRecentCardTheme()
        {
            if (recent == null) DesktopCardTheme.Apply(NoteTheme.Current().Id);
            else recent.ApplyCardTheme();
        }

        internal void InsertImage(BitmapSource image) => (recent ?? New()).InsertImage(image);
        internal void PinImage(BitmapSource image) => PinImageRequested?.Invoke(image);
        internal void OnTodoCreated(TodoItem item) => TodoCreated?.Invoke(item);
        internal void Remember() => DesktopSession.RememberOpenPaths(windows.Select(note => note.FilePath).Where(path => path != null));

        internal void ChangeTaskbarVisibility(bool show)
        {
            foreach (var window in windows.ToArray()) window.ShowInTaskbar = show;
        }

        internal void UpdateDefaultTheme()
        {
            foreach (var window in windows.ToArray()) window.UpdateDefaultTheme();
        }

        internal void CloseNotesWithoutSaving()
        {
            foreach (var window in windows.ToArray()) window.DiscardAndClose();
        }

        internal void RelocateNotes(string fromDirectory, string destination)
        {
            foreach (var window in windows.ToArray()) window.Relocate(fromDirectory, destination);
            Remember();
        }

        internal void ShowCatalog()
        {
            if (catalog == null)
            {
                catalog = new NoteCatalogWindow(this);
                catalog.Closed += (sender, args) => catalog = null;
                catalog.Show();
            }
            else
            {
                if (catalog.WindowState == WindowState.Minimized) catalog.WindowState = WindowState.Normal;
                catalog.Activate();
            }
        }

        internal void Delete(string path)
        {
            var open = windows.FirstOrDefault(note => string.Equals(note.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (open != null && !open.SaveCurrentNoteSafe()) throw new InvalidOperationException("便签尚未保存，请完成输入或保存后再删除。");
            Repository.Delete(path);
            open?.DiscardAndClose();
        }
    }
}
