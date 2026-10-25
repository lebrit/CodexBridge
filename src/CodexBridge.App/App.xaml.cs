using System.Windows;
using System.Windows.Threading;
using CodexBridge.Core;

namespace CodexBridge.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var window = new MainWindow(smokeTest: true)
                {
                    ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
                };
                window.Show();
                await window.RunSmokeTestAsync(e.Args[1]);
                window.Close();
                Shutdown(0);
            }
            catch (Exception exception)
            {
                // CI receives diagnostics in its isolated report directory only.
                System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "failure.txt"), exception.ToString());
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length != 0)
        {
            Shutdown(2);
            return;
        }
        new MainWindow().Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        ErrorLog.Write("Необработанная ошибка интерфейса", e.Exception.Message, e.Exception.ToString());
}
