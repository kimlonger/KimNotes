using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KimNotes.utils;
using KimNotes.Windows.Presentation;
using KimNotes.Windows.Settings;
using Microsoft.Win32;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;

namespace KimNotes.Windows.Capture
{
    internal sealed class CaptureWorkspace : IDisposable
    {
        private readonly Action<BitmapSource> insertIntoNote;
        private readonly HashSet<PinnedImageWindow> viewers = new HashSet<PinnedImageWindow>();
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private readonly CaptureArchiveWrites archiveWrites = new CaptureArchiveWrites();
        private readonly CaptureClipboard clipboard = new CaptureClipboard();
        private CaptureSelection selection;
        private Action finishHandoff;
        private PinnedImageWindow captureEditor;
        private PinnedImageWindow recent;
        private string recentId;
        private bool recentIncognito;
        private bool disposing;
        private bool privateMode;
        private CancellationTokenSource textRequest;
        private CaptureNotice notice;

        internal bool IsActive => selection != null;

        internal CaptureWorkspace(Action<BitmapSource> insertIntoNote)
        {
            this.insertIntoNote = insertIntoNote ?? throw new ArgumentNullException(nameof(insertIntoNote));
            privateMode = Storage.Incognito;
        }

        internal void StartCapture()
        {
            if (disposing || selection != null) return;
            try
            {
                var displays = CaptureScreen.GetDisplays();
                var prepared = new CaptureSelection(displays, CompleteSelection);
                selection = prepared;
                _ = AcquireAsync(Task.Run(() => CaptureScreen.Take(displays)), prepared);
                prepared.Prepare();
            }
            catch (Exception error)
            {
                selection?.Dispose();
                Warn(null, error.Message, "无法截图");
            }
        }

        private async Task AcquireAsync(Task<ScreenSnapshot> readScreen, CaptureSelection prepared)
        {
            ScreenSnapshot snapshot = null;
            Exception failure = null;
            try { snapshot = await readScreen.ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            if (dispatcher.HasShutdownStarted) return;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => OpenSelection(prepared, snapshot, failure)));
        }

        private void OpenSelection(CaptureSelection prepared, ScreenSnapshot snapshot, Exception failure)
        {
            if (disposing || !ReferenceEquals(selection, prepared)) return;
            if (failure != null)
            {
                prepared.Dispose();
                Warn(null, failure.Message, "无法截图");
                return;
            }
            try
            {
                prepared.Show(snapshot);
            }
            catch (Exception error)
            {
                prepared.Dispose();
                Warn(null, error.Message, "无法截图");
            }
        }

        internal void Recall() => Navigate(null, 0, true);
        internal void Previous() => Navigate(null, 1, false);
        internal void Next() => Navigate(null, -1, false);

        internal void ApplyPrivacyMode()
        {
            bool enabled = Storage.Incognito;
            if (enabled == privateMode) return;
            privateMode = enabled;
            clipboard.CancelPending();
            selection?.Dispose();
            if (!enabled) return;
            textRequest?.Cancel();
            foreach (var viewer in viewers) viewer.MoveToPrivateHistory();
            recentId = null;
        }

        internal void ShowPreview(BitmapSource image)
        {
            if (disposing || image == null) return;
            Safely(null, "无法打开图片", () => Open(image, NearCursor(), Storage.Incognito, true, null));
        }

        private void CompleteSelection(BitmapSource image, DrawingRect area, CaptureTool choice, Action finish)
        {
            finishHandoff = finish;
            if (disposing || image == null)
            {
                var abandoned = captureEditor;
                abandoned?.SuppressHistory();
                FinishSelection();
                abandoned?.Close();
                return;
            }

            var current = selection;
            try
            {
                if (choice == CaptureTool.Copy || choice == CaptureTool.Confirm)
                {
                    FinishSelection();
                    CopyCapture(image);
                    return;
                }
                if (choice == CaptureTool.Insert)
                {
                    FinishSelection();
                    insertIntoNote(image);
                    return;
                }
                if (choice == CaptureTool.Ocr)
                {
                    FinishSelection();
                    ProcessText(image);
                    return;
                }
                if (choice == CaptureTool.Pin)
                {
                    CopyCapture(image);
                    var pin = Open(image, area.Location, Storage.Incognito, false, null);
                    FinishAfterRender(current, () => SaveHistory(pin));
                    return;
                }

                if (current.AttachEditor(RunCaptureEditorTool, out var toolbar, out var toolbarBounds, out var owner))
                {
                    captureEditor = Open(image, area.Location, Storage.Incognito, false, null, isCaptureEditor: true,
                        show: viewer => viewer.OpenInSelection(area, toolbar, toolbarBounds, owner));
                    if (choice != CaptureTool.Edit) RunCaptureEditorTool(choice);
                    return;
                }

                var fallback = Open(image, area.Location, Storage.Incognito, false, null, showEditor: true, isCaptureEditor: true);
                FinishAfterRender(current, () =>
                {
                    if (choice == CaptureTool.Edit) SaveHistory(fallback);
                    else fallback.InvokeTool(choice);
                });
            }
            catch (Exception error)
            {
                FinishSelection();
                Warn(null, error.Message, "截图操作未完成");
            }
        }

        private void FinishAfterRender(CaptureSelection current, Action after)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (disposing || !ReferenceEquals(selection, current)) return;
                FinishSelection();
                Safely(null, "截图操作未完成", after);
            }));
        }

        private void RunCaptureEditorTool(CaptureTool choice)
        {
            var viewer = captureEditor;
            if (viewer == null) return;
            Safely(viewer, "截图操作未完成", () =>
            {
                if (choice == CaptureTool.Cancel)
                {
                    viewer.Close();
                    return;
                }
                if (choice == CaptureTool.Pin || choice == CaptureTool.Insert || choice == CaptureTool.Ocr || choice == CaptureTool.Translate)
                    FinishSelection();
                viewer.InvokeTool(choice);
            });
        }

        private void FinishSelection()
        {
            var editor = captureEditor;
            var release = finishHandoff;
            captureEditor = null;
            selection = null;
            finishHandoff = null;
            try { editor?.DetachSelectionToolbar(); }
            finally { release?.Invoke(); }
        }

        private PinnedImageWindow Open(BitmapSource image, DrawingPoint anchor, bool incognito, bool previewOnly, string id,
            bool showEditor = false, bool isCaptureEditor = false, Action<PinnedImageWindow> show = null)
        {
            var viewer = new PinnedImageWindow(image, incognito, previewOnly, Execute) { IsCaptureEditor = isCaptureEditor };
            viewer.ColorPicked += CopyColor;
            if (id != null) viewer.Replace(image, id);
            if (showEditor) viewer.InvokeTool(CaptureTool.Edit);
            viewers.Add(viewer);
            if (!previewOnly)
            {
                viewer.Activated += (sender, args) => Remember(viewer);
                viewer.Closing += (sender, args) =>
                {
                    if (ReferenceEquals(captureEditor, viewer))
                    {
                        viewer.SuppressHistory();
                        FinishSelection();
                        return;
                    }
                    try { FlushHistory(viewer); }
                    catch (Exception)
                    {
                        if (!disposing)
                            args.Cancel = MessageBox.Show(viewer, "贴图历史未能保存。仍要关闭这张贴图吗？", "贴图未保存",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
                    }
                };
            }
            viewer.Closed += (sender, args) =>
            {
                viewers.Remove(viewer);
                if (ReferenceEquals(viewer, recent)) recent = null;
            };
            try
            {
                if (show != null) show(viewer);
                else viewer.Open(anchor);
                if (!previewOnly) Remember(viewer);
                return viewer;
            }
            catch
            {
                viewer.SuppressHistory();
                viewer.Close();
                throw;
            }
        }

        private void Remember(PinnedImageWindow viewer)
        {
            recent = viewer;
            recentId = viewer.HistoryId;
            recentIncognito = viewer.Incognito;
        }

        private void SaveHistory(PinnedImageWindow viewer)
        {
            if (viewer.PreviewOnly || viewer.HistorySuppressed) return;
            viewer.CommitPendingAnnotation();
            if (!viewer.HistoryChanged) return;
            var image = viewer.ImageForAction();
            var id = viewer.HistoryId ?? ScreenshotArchive.NewId();
            if (viewer.Incognito)
            {
                using (var bitmap = CaptureBitmap.ToDrawing(image))
                    ScreenshotArchive.Save(bitmap, true, id);
            }
            else archiveWrites.Queue(id, image);
            viewer.HistoryId = id;
            viewer.HistoryChanged = false;
            Remember(viewer);
        }

        private void FlushHistory(PinnedImageWindow viewer)
        {
            SaveHistory(viewer);
            if (viewer.Incognito || viewer.HistorySuppressed || viewer.HistoryId == null) return;
            var failure = archiveWrites.Wait(viewer.HistoryId);
            if (failure == null) return;
            viewer.HistoryChanged = true;
            throw failure;
        }

        private void Navigate(PinnedImageWindow source, int direction, bool latest)
        {
            if (disposing || selection != null) return;
            Safely(source, "贴图历史未能打开", () =>
            {
                bool incognito = Storage.Incognito;
                var viewer = source ?? recent;
                if (viewer?.Incognito != incognito || viewer.PreviewOnly) viewer = null;
                if (viewer != null)
                {
                    if (viewer.BusyEditing)
                    {
                        viewer.Activate();
                        return;
                    }
                    FlushHistory(viewer);
                }
                if (!incognito) archiveWrites.Drain();
                var entries = ScreenshotArchive.List(incognito);
                if (entries.Length == 0) return;
                string id = viewer?.HistoryId ?? (recentIncognito == incognito ? recentId : null);
                int current = Array.IndexOf(entries, id);
                int next = latest ? 0 : Math.Max(0, Math.Min(entries.Length - 1, Math.Max(0, current) + direction));
                if (viewer != null && viewer.HistoryId == entries[next])
                {
                    viewer.Activate();
                    return;
                }
                using (var bitmap = ScreenshotArchive.Load(entries[next], incognito))
                {
                    if (bitmap == null) return;
                    var image = CaptureBitmap.FromDrawing(bitmap);
                    if (viewer == null)
                        viewer = Open(image, NearCursor(), incognito, false, entries[next]);
                    else
                        viewer.Replace(image, entries[next]);
                }
                viewer.Title = "贴图 " + (next + 1) + " / " + entries.Length;
                Remember(viewer);
                viewer.Activate();
            });
        }

        private static DrawingPoint NearCursor()
        {
            var point = CaptureScreen.Cursor;
            var area = CaptureScreen.DisplayNear(point, CaptureScreen.GetDisplays()).WorkArea;
            return new DrawingPoint(area.Left + 24, area.Top + 24);
        }

        private void Execute(PinnedImageWindow viewer, CaptureTool tool)
        {
            if (disposing) return;
            Safely(viewer, "截图操作未完成", () =>
            {
                switch (tool)
                {
                    case CaptureTool.Copy:
                        CopyCapture(viewer.ImageForAction(), viewer);
                        break;
                    case CaptureTool.Confirm:
                        var confirmed = viewer.ImageForAction();
                        viewer.SuppressHistory();
                        if (ReferenceEquals(captureEditor, viewer)) FinishSelection();
                        viewer.Close();
                        CopyCapture(confirmed);
                        break;
                    case CaptureTool.Pin:
                        SaveHistory(viewer);
                        viewer.IsCaptureEditor = false;
                        CopyCapture(viewer.ImageForAction(), viewer);
                        break;
                    case CaptureTool.Save:
                        var exported = Export(viewer);
                        if (exported != null && viewer.IsCaptureEditor) CopyCapture(exported, viewer);
                        break;
                    case CaptureTool.SaveDefault:
                        var saved = viewer.ImageForAction();
                        SaveToImages(saved);
                        if (viewer.IsCaptureEditor) CopyCapture(saved);
                        viewer.Close();
                        break;
                    case CaptureTool.Insert:
                        insertIntoNote(viewer.ImageForAction());
                        viewer.Close();
                        break;
                    case CaptureTool.Ocr:
                    case CaptureTool.Translate:
                        ProcessText(viewer.ImageForAction(), viewer, translate: tool == CaptureTool.Translate);
                        break;
                    case CaptureTool.Previous:
                        Navigate(viewer, 1, false);
                        break;
                    case CaptureTool.Next:
                        Navigate(viewer, -1, false);
                        break;
                    case CaptureTool.Destroy:
                        Destroy(viewer);
                        break;
                }
            });
        }

        private async void CopyCapture(BitmapSource image, Window owner = null)
        {
            try { await clipboard.CopyAsync(image).ConfigureAwait(false); }
            catch (Exception error)
            {
                if (dispatcher.HasShutdownStarted) return;
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!disposing) Warn(owner, error.Message, "截图未能复制");
                }));
            }
        }

        private async void CopyColor(PinnedImageWindow viewer, string color)
        {
            bool copied = await clipboard.CopyTextAsync(color);
            if (!disposing) ShowNotice(copied ? color + " 已复制" : "颜色未复制，请重试。", viewer);
        }

        private void ShowNotice(string message, Window owner = null)
        {
            if (disposing || dispatcher.HasShutdownStarted) return;
            notice?.Close();
            var next = new CaptureNotice(message, owner);
            notice = next;
            next.Closed += (sender, args) => { if (ReferenceEquals(notice, next)) notice = null; };
            next.Show();
        }

        private void Destroy(PinnedImageWindow viewer)
        {
            var id = viewer.HistoryId;
            if (id != null)
            {
                if (!viewer.Incognito) archiveWrites.Wait(id);
                ScreenshotArchive.Delete(id, viewer.Incognito);
            }
            var matches = new List<PinnedImageWindow>();
            foreach (var other in viewers)
                if (ReferenceEquals(other, viewer) || (id != null && other.HistoryId == id && other.Incognito == viewer.Incognito))
                    matches.Add(other);
            if (recentId == id)
            {
                recentId = null;
                recent = null;
            }
            foreach (var match in matches)
            {
                match.SuppressHistory();
                match.Close();
            }
        }

        private static void SaveToImages(BitmapSource image)
        {
            var folder = InitConfig.GetConfigValue("imagesPath");
            if (string.IsNullOrWhiteSpace(folder))
                throw new InvalidOperationException("请先在设置中选择图片保存目录。");
            var file = DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".png";
            WriteImage(Path.Combine(folder, file), image);
        }

        private static BitmapSource Export(PinnedImageWindow viewer)
        {
            var picker = new SaveFileDialog
            {
                Title = "保存截图",
                Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
                DefaultExt = ".png",
                AddExtension = true,
                FileName = "截图 " + DateTime.Now.ToString("yyyy-MM-dd HHmmss")
            };
            string folder = InitConfig.GetConfigValue("imagesPath");
            if (Directory.Exists(folder)) picker.InitialDirectory = folder;
            if (picker.ShowDialog(viewer) != true) return null;
            var image = viewer.ImageForAction();
            WriteImage(picker.FileName, image);
            return image;
        }

        private static void WriteImage(string path, BitmapSource source)
        {
            string extension = Path.GetExtension(path);
            bool jpeg = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            Storage.Write(path, temporary =>
            {
                using (var stream = File.Create(temporary)) CaptureBitmap.Write(stream, source, jpeg);
            });
        }

        private async void ProcessText(BitmapSource source, PinnedImageWindow viewer = null, bool translate = false)
        {
            if (textRequest != null)
            {
                ShowNotice("正在处理文字，请稍等。", viewer);
                return;
            }
            var publish = translate ? null : clipboard.BeginTextCopy();
            string title = translate ? "翻译图片" : "文字识别";
            string result = null;
            using (var request = new CancellationTokenSource())
            {
                textRequest = request;
                var wait = new CaptureTextWaitCard(viewer, title, translate ? "正在翻译图片…" : "正在识别文字…");
                bool finished = false;
                wait.Closed += (sender, args) => { if (!finished) request.Cancel(); };
                try
                {
                    wait.Show();
                    if (translate)
                    {
                        var translated = await CaptureTranslation.TranslateAsync(source, request.Token);
                        if (disposing || request.IsCancellationRequested) return;
                        result = translated == null ? "图片中未识别到文字。" :
                            viewer.TryApplyTranslation(source, translated) ? "译文已替换，可撤销。" : "图片已变更，请重新翻译。";
                    }
                    else
                    {
                        var text = await CaptureOcr.RecognizeAsync(source, request.Token);
                        if (disposing || request.IsCancellationRequested) return;
                        result = await CopyRecognizedText(text, publish, request.Token);
                    }
                    if (disposing || request.IsCancellationRequested) result = null;
                }
                catch (OperationCanceledException)
                {
                    if (!request.IsCancellationRequested && !disposing) Warn(viewer, "请求超时，请重试。", title);
                }
                catch (Exception error)
                {
                    if (!disposing && !request.IsCancellationRequested) Warn(viewer, error.Message, title);
                }
                finally
                {
                    finished = true;
                    if (wait.IsLoaded) wait.Close();
                    textRequest = null;
                }
            }
            if (result != null) ShowNotice(result, viewer);
        }

        private static async Task<string> CopyRecognizedText(string text,
            Func<string, CancellationToken, Task<bool>> publish, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(text)) return "图片中未识别到文字。";
            return await publish(text, token).ConfigureAwait(false) ? "识别文字已复制" : "文字已识别，复制未完成。";
        }

        private sealed class CaptureTextWaitCard : FeatureWindow
        {
            internal CaptureTextWaitCard(Window owner, string title, string message) : base(title, 238, 102)
            {
                Owner = owner;
                ShowInTaskbar = false;
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
                var panel = new StackPanel { Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center };
                var caption = new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 12), FontSize = 14 };
                caption.SetResourceReference(TextBlock.ForegroundProperty, DesktopCardTheme.Ink);
                panel.Children.Add(caption);
                var progress = new ProgressBar { Height = 4, IsIndeterminate = true };
                progress.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.Field);
                progress.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Accent);
                panel.Children.Add(progress);
                Body.Content = panel;
            }
        }

        private static void Safely(Window owner, string label, Action action)
        {
            try { action(); }
            catch (Exception error) { Warn(owner, error.Message, label); }
        }

        private static void Warn(Window owner, string message, string title)
        {
            if (owner != null && owner.IsLoaded)
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void Dispose()
        {
            if (disposing) return;
            disposing = true;
            notice?.Close();
            clipboard.CancelPending();
            textRequest?.Cancel();
            selection?.Dispose();
            foreach (var viewer in new List<PinnedImageWindow>(viewers)) viewer.Close();
            archiveWrites.Drain();
        }
    }
}
