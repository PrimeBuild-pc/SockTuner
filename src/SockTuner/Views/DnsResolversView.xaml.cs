using System.Net;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;

namespace SockTuner.Views;

/// <summary>
/// Benchmarks DNS resolvers and applies one to an adapter. Owns its own controls; the window
/// supplies the inventory and shows the status, activity and refresh it asks for.
/// </summary>
public partial class DnsResolversView : UserControl
{
    private readonly DnsBenchmarkProbe _dnsBenchmark = new();
    private CancellationTokenSource? _dnsBenchmarkCancellation;
    private readonly ElevatedWorkerClient _dnsWorker = new();
    private DnsBenchmarkReport? _lastDnsReport;
    private IReadOnlyList<AdapterInfo> _adapters = [];

    public DnsResolversView() => InitializeComponent();

    /// <summary>A line for the window's status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>True while a benchmark runs, for the window's activity bar.</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>A resolver change succeeded, so the inventory is out of date.</summary>
    public event EventHandler? Applied;

    /// <summary>
    /// The adapters from the latest inventory. Their configured resolvers join the benchmark, and
    /// the physical ones with a stable identity are the ones a resolver can be applied to.
    /// </summary>
    public void SetAdapters(IReadOnlyList<AdapterInfo> adapters)
    {
        _adapters = adapters;
        var dnsCandidates = adapters
            .Where(item => Guid.TryParse(item.Id, out _) && item.Kind == AdapterKind.Physical)
            .ToArray();
        var previouslySelected = (DnsAdapterComboBox.SelectedItem as AdapterInfo)?.Id;
        DnsAdapterComboBox.ItemsSource = dnsCandidates;
        DnsAdapterComboBox.SelectedItem = dnsCandidates.FirstOrDefault(item => item.Id == previouslySelected)
            ?? dnsCandidates.FirstOrDefault(item => item.Status == System.Net.NetworkInformation.OperationalStatus.Up)
            ?? dnsCandidates.FirstOrDefault();
    }

    public void CancelActiveWork() => _dnsBenchmarkCancellation?.Cancel();

    private void SetStatus(string text) => StatusChanged?.Invoke(this, text);

    private void CancelDnsBenchmark_Click(object sender, RoutedEventArgs e)
    {
        _dnsBenchmarkCancellation?.Cancel();
        SetStatus(Loc.T("Stopping the resolver benchmark\u2026"));
    }

    private async void RunDnsBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DnsRoundsText.Text.Trim(), out var rounds) || rounds is < 1 or > 10)
        {
            SetStatus(Loc.T("Queries per name must be between 1 and 10."));
            return;
        }

        var candidates = BuildResolverCandidates();
        if (candidates.Count == 0)
        {
            SetStatus(Loc.T("No resolver to measure."));
            return;
        }

        _dnsBenchmarkCancellation?.Dispose();
        _dnsBenchmarkCancellation = new CancellationTokenSource();
        BusyChanged?.Invoke(this, true);
        RunDnsBenchmarkButton.IsEnabled = false;
        CancelDnsBenchmarkButton.IsEnabled = true;
        DnsVerdictText.Text = Loc.T("Measuring\u2026");
        DnsResolverGrid.ItemsSource = null;
        SetStatus(Loc.F($"Benchmarking {candidates.Count} resolver(s)\u2026"));
        AppLog.Write("dns.benchmark_started", $"Resolvers={candidates.Count}; Rounds={rounds}.");

        try
        {
            var report = await _dnsBenchmark.RunAsync(
                candidates,
                DnsBenchmarkProbe.DefaultHostnames,
                rounds,
                DnsBenchmarkProbe.DefaultTimeout,
                _dnsBenchmarkCancellation.Token);

            DnsResolverGrid.ItemsSource = report.Results
                .OrderBy(result => result.Usable ? 0 : 1)
                .ThenBy(result => result.MedianMs ?? double.MaxValue)
                .ToArray();
            DnsVerdictText.Text = report.Verdict;
            _lastDnsReport = report;
            ApplyBestDnsButton.IsEnabled = WorthApplying(report);
            SetStatus(Loc.T("Resolver benchmark complete."));
            AppLog.Write("dns.benchmark_completed", report.Verdict);

            if (DnsAutoApplyCheck.IsChecked == true && WorthApplying(report))
            {
                DnsApplyStatusText.Text = Loc.T("A worthwhile resolver was found; applying automatically\u2026");
                await ApplyDnsAsync(report.Fastest!.Resolver.Address);
            }
        }
        catch (OperationCanceledException)
        {
            DnsVerdictText.Text = Loc.T("Cancelled before every resolver was measured.");
            SetStatus(Loc.T("Resolver benchmark cancelled."));
            AppLog.Write("dns.benchmark_cancelled", "Cancelled by the operator.");
        }
        catch (Exception exception)
        {
            DnsVerdictText.Text = Loc.F($"Benchmark failed: {exception.Message}");
            SetStatus(Loc.T("Resolver benchmark failed."));
            AppLog.Write("dns.benchmark_failed", exception.Message);
        }
        finally
        {
            BusyChanged?.Invoke(this, false);
            RunDnsBenchmarkButton.IsEnabled = true;
            CancelDnsBenchmarkButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// The well-known list, the resolvers this machine is actually configured to use, and anything
    /// typed in. Without the ones in use there is nothing to compare against, so a "faster" result
    /// would have no meaning.
    /// </summary>
    private IReadOnlyList<DnsResolverCandidate> BuildResolverCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<DnsResolverCandidate>();

        foreach (var address in _adapters
            .Where(adapter => adapter.Status == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(adapter => adapter.DnsServers))
        {
            if (IPAddress.TryParse(address, out var parsed)
                && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && seen.Add(address))
            {
                candidates.Add(new DnsResolverCandidate("Currently configured", address, InUse: true));
            }
        }

        foreach (var extra in DnsExtraResolversText.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(extra)) candidates.Add(new DnsResolverCandidate("Custom", extra));
        }

        foreach (var known in DnsBenchmarkProbe.WellKnown)
        {
            if (seen.Add(known.Address)) candidates.Add(known);
        }

        return candidates;
    }

    /// <summary>
    /// Whether a measured winner is worth acting on. The same noise floor the verdict uses: a gain
    /// under 5 ms is inside run-to-run variation, and acting on it automatically would be churn.
    /// </summary>
    private static bool WorthApplying(DnsBenchmarkReport report) =>
        report.Fastest is not null
        && !ReferenceEquals(report.Fastest, report.Current)
        && report.ImprovementMs is >= 5;

    private async void ApplyBestDns_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDnsReport?.Fastest is not { } fastest)
        {
            DnsApplyStatusText.Text = Loc.T("Run a benchmark first.");
            return;
        }

        await ApplyDnsAsync(fastest.Resolver.Address);
    }

    private async void RevertDns_Click(object sender, RoutedEventArgs e) => await ApplyDnsAsync(null);

    /// <summary>
    /// Applies a resolver list, or restores DHCP when <paramref name="primary"/> is null. Goes
    /// through the same elevated worker and the same snapshot, verify and rollback path as every
    /// other change; nothing here writes directly.
    /// </summary>
    private async Task ApplyDnsAsync(string? primary)
    {
        if (!WriteConsentDialog.Ensure(out var consentProblem))
        {
            DnsApplyStatusText.Text = consentProblem;
            return;
        }

        if (DnsAdapterComboBox.SelectedItem is not AdapterInfo adapter || !Guid.TryParse(adapter.Id, out _))
        {
            DnsApplyStatusText.Text = Loc.T("Select an adapter to change.");
            return;
        }

        var specification = new DnsServerSpecification();
        var store = new DnsServerStore();
        var address = specification.ResolveAddress(adapter.Id);

        try
        {
            var before = await store.ReadAsync(address, CancellationToken.None);
            var after = primary is null
                ? StoredSettingValue.Missing
                : new StoredSettingValue(true, DnsServerSpecification.Canonical([primary]));

            if (before == after)
            {
                DnsApplyStatusText.Text = primary is null
                    ? Loc.F($"{adapter.Name} already takes its resolvers from DHCP.")
                    : Loc.F($"{adapter.Name} is already using {primary}.");
                return;
            }

            var request = new ElevatedWorkerRequest(
                ElevatedWorker.SchemaVersion,
                Guid.NewGuid(),
                WorkerOperationKind.Apply,
                [new WorkerSettingOperation(
                    address.SettingId,
                    address.TargetId,
                    new WorkerStoredValue(before.Exists, before.Value),
                    new WorkerStoredValue(after.Exists, after.Value),
                    ChangeSource.Manual)]);

            DnsApplyStatusText.Text = Loc.T("Approve the Windows elevation prompt to continue\u2026");
            var response = await _dnsWorker.ExecuteAsync(request, CancellationToken.None);
            DnsApplyStatusText.Text = response.Status;
            AppLog.Write("dns.applied", $"Adapter={adapter.Name}; To={after.Value}; Success={response.Success}; {response.Status}");
            if (response.Success) Applied?.Invoke(this, EventArgs.Empty);
        }
        catch (ElevatedWorkerDeclinedException exception)
        {
            DnsApplyStatusText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            DnsApplyStatusText.Text = Loc.F($"Resolver change failed: {exception.Message}");
            AppLog.Write("dns.apply_failed", exception.Message);
        }
    }
}
