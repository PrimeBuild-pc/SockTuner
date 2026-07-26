using System.Windows;
using SockTuner.Services;

namespace SockTuner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 1 && string.Equals(e.Args[0], "--elevated-worker", StringComparison.Ordinal))
        {
            Shutdown(ElevatedWorker.RunAsync(Console.In, Console.Out, CancellationToken.None).GetAwaiter().GetResult());
            return;
        }

        if (e.Args.Length != 0)
        {
            Shutdown(2);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
