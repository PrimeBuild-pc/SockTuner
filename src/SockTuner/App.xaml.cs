using System.IO;
using System.Windows;
using SockTuner.Persistence;
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

        if (e.Args.Length == 1 && string.Equals(e.Args[0], "--probe", StringComparison.Ordinal))
        {
            Shutdown(RunProbe());
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

    // Read-only hardware capability probe for collaborators: captures the inventory,
    // redacts personal data, and writes a shareable JSON report. Mutates nothing.
    private static int RunProbe()
    {
        try
        {
            var snapshot = new SystemInventoryService().Capture();
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"socktuner-probe-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, SnapshotExporter.Serialize(snapshot, probe: true));
            MessageBox.Show(
                $"Probe report saved to:\n{path}\n\nNothing on this PC was changed. Share this file with the SockTuner team.",
                "SockTuner probe",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Probe failed: {exception.Message}",
                "SockTuner probe",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }
}
