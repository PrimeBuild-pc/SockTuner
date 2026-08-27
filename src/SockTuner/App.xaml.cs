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
        // Elevated worker mode: connects back to the launching process over the named pipe it
        // was given and serves exactly one typed request.
        if (e.Args.Length == 2 && string.Equals(e.Args[0], ElevatedWorkerClient.WorkerArgument, StringComparison.Ordinal))
        {
            Shutdown(ElevatedWorkerHost.RunAsync(e.Args[1], CancellationToken.None).GetAwaiter().GetResult());
            return;
        }

        if (e.Args.Length == 1 && string.Equals(e.Args[0], "--probe", StringComparison.Ordinal))
        {
            Shutdown(RunProbe());
            return;
        }

        if (e.Args.Length == 1 && string.Equals(e.Args[0], VerifyTcpWritesArgument, StringComparison.Ordinal))
        {
            Shutdown(RunTcpWriteVerification());
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

    internal const string VerifyTcpWritesArgument = "--verify-tcp-writes";

    /// <summary>
    /// Set to 1 in the guest to arm the write verification. The argument alone is not enough: this
    /// mode writes to the live TCP stack, so it must be impossible to trigger by mistyping --probe
    /// on a real desktop.
    /// </summary>
    internal const string VerifyTcpWritesGate = "SOCKTUNER_VM_WRITE_TEST";

    /// <summary>
    /// VM-only. Flips one deliberately harmless TCP property on each template through the real
    /// transaction engine and puts it back, to find out which templates accept a write at all and
    /// whether the one carrying traffic is among them. Every change is snapshotted and rolled back;
    /// a rollback that fails is reported as the headline rather than buried.
    /// </summary>
    private static int RunTcpWriteVerification()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(VerifyTcpWritesGate), "1", StringComparison.Ordinal))
        {
            MessageBox.Show(
                $"This mode writes to the live TCP stack and is for a disposable VM only." + Environment.NewLine + Environment.NewLine
                + $"Set {VerifyTcpWritesGate}=1 in the guest to arm it.",
                "SockTuner write verification",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 2;
        }

        try
        {
            var capabilities = WindowsGlobalSettingInventory.Read().Capabilities;
            var resolution = WindowsTcpTemplateResolver.Read();
            var store = new CimGlobalSettingStore();
            var verifier = new TcpWriteVerifier(new SettingTransactionService(
                SettingSpecifications.From([], capabilities)));

            var report = verifier
                .RunAsync(capabilities, resolution, store, () => store.IneffectiveWrites, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"socktuner-tcp-write-verification-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, SnapshotExporter.SerializeTcpWriteVerification(report));
            MessageBox.Show(
                $"{report.Verdict}" + Environment.NewLine + Environment.NewLine + "Report saved to:" + Environment.NewLine + path,
                "SockTuner write verification",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Write verification failed: {exception.Message}",
                "SockTuner write verification",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
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
