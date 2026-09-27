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
        if (e.Args.Length == 3
            && string.Equals(e.Args[0], UpdateInstaller.Argument, StringComparison.Ordinal)
            && int.TryParse(e.Args[1], out var parentProcessId))
        {
            var result = UpdateInstaller.Apply(parentProcessId, e.Args[2]);
            if (!result.Success)
            {
                MessageBox.Show(
                    $"The update could not be installed.{Environment.NewLine}{Environment.NewLine}{result.Error}",
                    "SockTuner update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown(result.Success ? 0 : 1);
            return;
        }

        // Elevated worker mode: connects back to the launching process over the named pipe it
        // was given and serves exactly one typed request.
        if (e.Args.Length == 2 && string.Equals(e.Args[0], ElevatedWorkerClient.WorkerArgument, StringComparison.Ordinal))
        {
            Shutdown(ElevatedWorkerHost.RunAsync(e.Args[1], CancellationToken.None).GetAwaiter().GetResult());
            return;
        }

        if (e.Args.Length == 1 && string.Equals(e.Args[0], VerifyTcpWritesArgument, StringComparison.Ordinal))
        {
            Shutdown(RunTcpWriteVerification());
            return;
        }

        if (e.Args.Length == 1 && string.Equals(e.Args[0], VerifyDeviceWritesArgument, StringComparison.Ordinal))
        {
            Shutdown(RunDeviceWriteVerification());
            return;
        }

        if (e.Args.Length != 0)
        {
            Shutdown(2);
            return;
        }

        // Replace only the shared palette in High Contrast. Native controls and all semantic text
        // remain intact, while Windows owns the actual accessible colours.
        ApplyAccessibilityPalette();

        // Before the window is built: UiTranslator rewrites what XAML produced, so the language
        // has to be settled first. The console modes above stay English — they answer a terminal.
        Loc.Use(AppPreferences.Load().Language);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void ApplyAccessibilityPalette()
    {
        if (!SystemParameters.HighContrast) return;

        Resources["PageBrush"] = SystemColors.WindowBrush;
        Resources["PanelBrush"] = SystemColors.ControlBrush;
        Resources["ControlBrush"] = SystemColors.ControlBrush;
        Resources["PanelHoverBrush"] = SystemColors.HighlightBrush;
        Resources["NavigationBrush"] = SystemColors.WindowBrush;
        Resources["BorderBrush"] = SystemColors.ActiveBorderBrush;
        Resources["ControlBorderBrush"] = SystemColors.ActiveBorderBrush;
        Resources["PrimaryBrush"] = SystemColors.HighlightBrush;
        Resources["PrimaryTextBrush"] = SystemColors.HotTrackBrush;
        Resources["PrimaryHoverBrush"] = SystemColors.HighlightBrush;
        Resources["PrimaryPressedBrush"] = SystemColors.HighlightBrush;
        Resources["PrimaryMutedBrush"] = SystemColors.ControlBrush;
        Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Resources["SelectionTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["MutedTextBrush"] = SystemColors.GrayTextBrush;
        Resources["WarningBrush"] = SystemColors.HotTrackBrush;
        Resources["WarningTextBrush"] = SystemColors.HotTrackBrush;
        Resources["WarningMutedBrush"] = SystemColors.ControlBrush;
        Resources["DangerBrush"] = SystemColors.WindowTextBrush;
        Resources["GoodBrush"] = SystemColors.WindowTextBrush;
    }

    internal const string VerifyTcpWritesArgument = "--verify-tcp-writes";
    internal const string VerifyDeviceWritesArgument = "--verify-device-writes";

    /// <summary>
    /// Set to 1 in the guest to arm the write verification. The argument alone is not enough: this
    /// mode writes to the live TCP stack, so an argument alone must not arm it on a real desktop.
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

    /// <summary>
    /// VM-only. Applies each device-level setting for real and puts it back, through the same
    /// transaction engine the tuning plan uses. Disabling an adapter is only attempted against one
    /// that carries no default route, so a validation run cannot cut off the machine it validates.
    /// </summary>
    private static int RunDeviceWriteVerification()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(VerifyTcpWritesGate), "1", StringComparison.Ordinal))
        {
            Report(
                "SockTuner device write verification",
                "This mode writes to live device settings and is for a disposable VM only."
                + Environment.NewLine + $"Set {VerifyTcpWritesGate}=1 in the guest to arm it.",
                MessageBoxImage.Warning);
            return 2;
        }

        try
        {
            var snapshot = new SystemInventoryService().Capture();
            var store = new CompositeSettingStore(
                WindowsRegistrySettingStore.CreateWritable(), new CimAdapterSettingStore());
            var verifier = new DeviceWriteVerifier(new SettingTransactionService(SettingSpecifications.Live()));

            var report = verifier
                .RunAsync(snapshot.Adapters, store, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"socktuner-device-write-verification-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, SnapshotExporter.SerializeDeviceWriteVerification(report));

            var lines = new List<string> { report.Verdict, string.Empty };
            lines.AddRange(report.Outcomes.Select(outcome => "  " + outcome.Summary));
            lines.AddRange(report.Skipped.Select(skip => "  skipped: " + skip));
            lines.AddRange(report.Notes.Select(note => "  note: " + note));
            lines.Add(string.Empty);
            lines.Add("Report saved to: " + path);

            Report(
                "SockTuner device write verification",
                string.Join(Environment.NewLine, lines),
                report.Outcomes.All(outcome => outcome.Restored) ? MessageBoxImage.Information : MessageBoxImage.Error);
            return report.Outcomes.All(outcome => outcome.Restored) ? 0 : 3;
        }
        catch (Exception exception)
        {
            Report(
                "SockTuner device write verification",
                $"Device write verification failed: {exception.Message}",
                MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>
    /// Reports the outcome of a console-invoked verification mode. This writes to the parent
    /// terminal when one exists; otherwise a message box is the only visible channel.
    /// </summary>
    /// <remarks>
    /// A WPF process is built without a console, so it has to borrow the parent's. When it cannot,
    /// nothing has been written and the modal is the only remaining channel.
    /// </remarks>
    private static void Report(string title, string message, MessageBoxImage severity)
    {
        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                var stream = Console.OpenStandardOutput();
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                writer.WriteLine();
                writer.WriteLine(message);
                return;
            }
            catch (IOException)
            {
                // The parent's console went away between attaching and writing; fall through.
            }
        }

        MessageBox.Show(message, title, MessageBoxButton.OK, severity);
    }

    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
