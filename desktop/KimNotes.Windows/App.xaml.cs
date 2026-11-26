using System;
using System.Windows;
using KimNotes.Windows.Runtime;

namespace KimNotes.Windows
{
    public partial class App : Application
    {
        private DesktopRuntime runtime;
        private DesktopInstance instance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (sender, error) =>
            {
                Program.LogError(error.Exception);
                MessageBox.Show(error.Exception.Message, "小羊便签", MessageBoxButton.OK, MessageBoxImage.Warning);
                error.Handled = true;
            };
            try
            {
                instance = new DesktopInstance(Dispatcher);
                if (!instance.IsPrimary) { Shutdown(); return; }
                if (DesktopInstance.IsLegacyProfileInUse())
                {
                    MessageBox.Show("请先从系统托盘退出旧版小羊便签，再打开 WPF 版。", "小羊便签");
                    Shutdown(1);
                    return;
                }
                runtime = new DesktopRuntime(this);
                instance.ActivateRequested += runtime.Recall;
                runtime.Start();
            }
            catch (Exception error)
            {
                Program.LogError(error);
                MessageBox.Show(error.Message, "无法启动小羊便签");
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            runtime?.Dispose();
            instance?.Dispose();
            base.OnExit(e);
        }
    }
}
