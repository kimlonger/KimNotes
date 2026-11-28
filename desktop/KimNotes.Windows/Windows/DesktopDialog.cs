using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KimNotes.Windows.Settings;

namespace KimNotes.Windows.Presentation
{
    internal sealed class DesktopDialog : CardWindow
    {
        private bool accepted;

        private DesktopDialog(string title, string message, string confirmLabel, bool destructive) : base(title)
        {
            Width = 348;
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var layout = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 };
            layout.Children.Add(new ScrollViewer
            {
                Content = text,
                MaxHeight = 210,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var confirm = FeatureWindow.Button(confirmLabel ?? "确定", Accept, primary: !destructive);
            confirm.MinWidth = 76;
            Button initialFocus = confirm;
            if (confirmLabel != null)
            {
                var cancel = FeatureWindow.Button("取消", Close);
                cancel.MinWidth = 76;
                cancel.Margin = new Thickness(0, 0, 8, 0);
                buttons.Children.Add(cancel);
                initialFocus = cancel;
            }
            if (destructive)
            {
                confirm.SetResourceReference(Control.BackgroundProperty, DesktopCardTheme.DangerTint);
                confirm.SetResourceReference(Control.ForegroundProperty, DesktopCardTheme.Danger);
                confirm.SetResourceReference(Control.BorderBrushProperty, DesktopCardTheme.Danger);
            }
            buttons.Children.Add(confirm);
            layout.Children.Add(buttons);
            Body.Children.Add(layout);

            initialFocus.IsDefault = true;
            Loaded += (sender, args) => initialFocus.Focus();
            PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Escape) return;
                args.Handled = true;
                Close();
            };
        }

        internal static void Show(string title, string message, Window owner = null)
        {
            var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
            if (!CanPresent(dispatcher)) return;

            Action show = () =>
            {
                if (CanPresent(dispatcher)) new DesktopDialog(title, message, null, false).Present(owner);
            };
            if (dispatcher.CheckAccess()) show();
            else dispatcher.BeginInvoke(show);
        }

        internal static bool Confirm(Window owner, string title, string message, string confirmLabel, bool destructive = false)
        {
            var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
            if (!CanPresent(dispatcher)) return false;

            Func<bool> show = () => CanPresent(dispatcher) && new DesktopDialog(title, message, confirmLabel, destructive).Present(owner);
            try { return dispatcher.CheckAccess() ? show() : dispatcher.Invoke(show); }
            catch (OperationCanceledException) when (!CanPresent(dispatcher)) { return false; }
        }

        private bool Present(Window requestedOwner)
        {
            var owner = requestedOwner ?? Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(window => window.IsActive && !(window is DesktopDialog));
            bool blockOwner = owner != null && owner.IsLoaded && owner.IsVisible && owner.IsEnabled &&
                owner.WindowState != WindowState.Minimized;
            if (blockOwner)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            // A private frame keeps only the source window blocked. Other cards stay interactive.
            var frame = new DispatcherFrame();
            Closed += (sender, args) => frame.Continue = false;
            try
            {
                if (blockOwner) owner.SetCurrentValue(IsEnabledProperty, false);
                base.Show();
                if (IsVisible) Dispatcher.PushFrame(frame);
                return accepted;
            }
            finally
            {
                if (blockOwner) owner.SetCurrentValue(IsEnabledProperty, true);
                if (IsVisible && CanPresent(Dispatcher)) Close();
            }
        }

        private void Accept()
        {
            accepted = true;
            Close();
        }

        private static bool CanPresent(Dispatcher dispatcher) => dispatcher != null &&
            !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished;
    }
}
