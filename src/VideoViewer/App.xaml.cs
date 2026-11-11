using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VideoViewer.Services;

namespace VideoViewer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log(a.ExceptionObject as Exception);
            base.OnStartup(e);
            new MainWindow().Show();
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception);
            e.Handled = true; // keep the app alive; details go to the log
        }

        static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.Root, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
            }
            catch { }
        }
    }
}
