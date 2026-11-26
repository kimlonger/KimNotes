using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KimNotes.Windows.Runtime
{
    internal sealed class DesktopTray : IDisposable
    {
        private readonly Forms.NotifyIcon icon;
        private readonly Icon image;
        private readonly ContextMenu menu;
        private HwndSource menuOwner;
        private int menuVersion;
        private bool disposed;

        internal DesktopTray(Action recall, params (string Label, Action Click)[] commands)
        {
            image = Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath);
            menu = DesktopTrayMenu.Create(commands);
            menu.Closed += MenuClosed;
            icon = new Forms.NotifyIcon { Icon = image ?? SystemIcons.Application, Text = "小羊便签", Visible = true };
            icon.MouseClick += (sender, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left)
                {
                    menu.IsOpen = false;
                    recall();
                }
                else if (args.Button == Forms.MouseButtons.Right) ShowMenu();
            };
        }

        private void ShowMenu()
        {
            if (disposed || menu.IsOpen) return;

            var cursor = Forms.Cursor.Position;
            if (menuOwner == null)
            {
                const int PopupWindow = unchecked((int)0x80000000);
                const int ToolWindow = 0x80;
                var anchor = new Border { Width = 1, Height = 1, IsHitTestVisible = false };
                menuOwner = new HwndSource(new HwndSourceParameters("KimNotes Tray Menu", 1, 1)
                {
                    WindowStyle = PopupWindow,
                    ExtendedWindowStyle = ToolWindow,
                    UsesPerPixelOpacity = true,
                    PositionX = cursor.X,
                    PositionY = cursor.Y
                }) { RootVisual = anchor };
                menuOwner.AddHook(MenuOwnerMessage);
                menu.PlacementTarget = anchor;
            }

            const uint MoveOnlyWithoutActivation = 0x0001 | 0x0004 | 0x0010;
            const int ShowWithoutActivation = 4;
            IntPtr handle = menuOwner.Handle;
            SetWindowPos(handle, IntPtr.Zero, cursor.X, cursor.Y, 0, 0, MoveOnlyWithoutActivation);
            ShowWindow(handle, ShowWithoutActivation);
            // The shell callback is our activation opportunity. WPF popup capture needs a foreground owner.
            if (GetForegroundWindow() != handle && !SetForegroundWindow(handle))
            {
                HideMenuOwner();
                return;
            }
            menuVersion++;
            try { menu.IsOpen = true; }
            finally { if (!menu.IsOpen) HideMenuOwner(); }
        }

        private IntPtr MenuOwnerMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (menu.IsOpen)
            {
                var popup = PresentationSource.FromVisual(menu) as HwndSource;
                if (ShouldDismiss(message, wParam, lParam, popup?.Handle ?? IntPtr.Zero))
                {
                    int version = menuVersion;
                    menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        if (!disposed && version == menuVersion) menu.IsOpen = false;
                    }));
                }
            }
            return IntPtr.Zero;
        }

        private static bool ShouldDismiss(int message, IntPtr wParam, IntPtr nextWindow, IntPtr popup)
        {
            const int Activate = 0x0006;
            const int CancelMode = 0x001F;
            const int ActivateApp = 0x001C;
            return message == CancelMode || (message == ActivateApp && wParam == IntPtr.Zero) ||
                (message == Activate && (wParam.ToInt64() & 0xffff) == 0 && (popup == IntPtr.Zero || popup != nextWindow));
        }

        private void MenuClosed(object sender, RoutedEventArgs args)
        {
            if (!menu.IsOpen) HideMenuOwner();
        }

        private void HideMenuOwner()
        {
            if (menuOwner == null || menuOwner.IsDisposed) return;
            const int Hide = 0;
            const int NullMessage = 0;
            ShowWindow(menuOwner.Handle, Hide);
            PostMessage(menuOwner.Handle, NullMessage, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            menu.IsOpen = false;
            icon.Visible = false;
            icon.Dispose();
            image?.Dispose();
            menu.Closed -= MenuClosed;
            menu.PlacementTarget = null;
            menuOwner?.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
