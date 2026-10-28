using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Collection;

namespace SockTuner.Views;

public partial class NatDiagnosticsView : UserControl
{
    private readonly NatDiagnosticProbe _probe = new();
    private CancellationTokenSource? _cancellation;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<bool>? BusyChanged;

    public NatDiagnosticsView()
    {
        InitializeComponent();
        GameCombo.ItemsSource = GamePortPresets.All;
        GameCombo.SelectedIndex = 0;
        GuidanceGrid.ItemsSource = NatSafetyGuidance.All.Select(row => new NatGuidance(row.Priority,
            Loc.T(row.Action), Loc.T(row.When), Loc.T(row.Risk))).ToArray();
    }

    private PeerNatSession? _peerSession;
    private PairingCode? _hostCode;
    private CancellationTokenSource? _peerCancellation;
    private PeerRole SelectedRole => GuestRadio.IsChecked == true ? PeerRole.Guest : PeerRole.Host;

    public void CancelActiveWork()
    {
        _cancellation?.Cancel();
        _peerCancellation?.Cancel();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelActiveWork();

    private void Game_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GameCombo.SelectedItem is not GamePortPreset preset) return;
        PortText.Text = preset.SuggestedPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PresetText.Text = (preset.PublishedUdpPorts.Length == 0 ? "" : Loc.F($"Published UDP ports: {preset.PublishedUdpPorts}. "))
            + Loc.T(preset.Scope) + " " + Loc.T("References reviewed 2026-10-19; verify the publisher's current documentation. These are not instructions to open every listed port.");
        SourceButton.IsEnabled = preset.SourceUrl.Length > 0;
    }

    private void Port_Changed(object sender, TextChangedEventArgs e)
    {
        if (ResultText is null) return;
        ResultText.Text = Loc.T("No UDP test yet.");
        EvidenceGrid.ItemsSource = null;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortText.Text.Trim(), out var port) || port is < 0 or > 65535)
        {
            ResultText.Text = Loc.T("Choose a UDP port from 1 to 65535, or 0 for a temporary port.");
            return;
        }
        _cancellation = new CancellationTokenSource();
        InputsPanel.IsEnabled = RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        EvidenceGrid.ItemsSource = null;
        ResultText.Text = Loc.T("Measuring UDP behavior; this can take up to 35 seconds...");
        BusyChanged?.Invoke(this, true);
        try
        {
            var result = await _probe.RunAsync(port, _cancellation.Token);
            ResultText.Text = result.Failure switch
            {
                NatProbeFailure.PortUnavailable => Loc.T("Local port unavailable — no NAT verdict. Close the game or choose another port explicitly."),
                NatProbeFailure.NoBaseline => Loc.T("No baseline STUN reply — inconclusive, not proof of a blocked game port."),
                NatProbeFailure.NetworkError => Loc.T("Network test unavailable — no NAT verdict."),
                _ => Loc.T(NatGamerReading.Of(result))
            };
            var rows = new List<EvidenceRow>
            {
                new(Loc.T("Local UDP port"), result.LocalPort.ToString(), Loc.T(
                    result.Failure is NatProbeFailure.None or NatProbeFailure.NoBaseline
                        ? "Exclusively bound by SockTuner, not by the game."
                        : "No usable socket measurement; no other port was substituted.")),
                new(Loc.T("Public mapping"), result.PublicEndpoint?.ToString() ?? Loc.T("Not measured"),
                    Loc.T("Observed by STUN. A public mapping does not exclude upstream NAT or prove unsolicited inbound reachability.")),
                new(Loc.T("Mapping across destinations"), Loc.T(result.Mapping switch
                {
                    NatMappingObservation.SameOnTestedDestinations => "Same on tested destinations",
                    NatMappingObservation.ChangedOnTestedDestinations => "Different on tested destinations",
                    _ => "Not measured"
                }), Loc.T("Sampled using the same local socket, with a baseline recheck. Not a universal NAT classification.")),
                new(Loc.T("Reply from another server port"), Loc.T(result.Filtering switch
                {
                    NatFilteringObservation.AlternatePortReceived => "Received before contacting that port",
                    NatFilteringObservation.AlternatePortFiltered => "Missing before contact; received after contact",
                    NatFilteringObservation.Unsupported => "Server did not advertise the required alternate port",
                    NatFilteringObservation.Inconclusive => "Inconclusive",
                    _ => "Not measured"
                }), Loc.T("Missing-before / received-after supports port-dependent filtering on this path. Router and host firewall are not isolated; packet loss remains possible.")),
                new(Loc.T("Game inbound reachability"), Loc.T("Not verified"),
                    Loc.T("No independent unsolicited probe from another IP is available. STUN replies and a game-port preset cannot certify the game's NAT status."))
            };
            if (result.Detail is { } detail)
                rows.Add(new(Loc.T("Detail"), detail, Loc.T("An error is not a closed-port diagnosis.")));
            EvidenceGrid.ItemsSource = rows;
        }
        catch (OperationCanceledException)
        {
            ResultText.Text = Loc.T("UDP test cancelled; no NAT verdict.");
        }
        catch (Exception exception)
        {
            ResultText.Text = Loc.F($"UDP test failed: {exception.Message}");
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            InputsPanel.IsEnabled = RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            BusyChanged?.Invoke(this, false);
            StatusChanged?.Invoke(this, ResultText.Text);
        }
    }

    private void ResetPeer()
    {
        _peerSession?.Dispose();
        _peerSession = null;
        _hostCode = null;
        MyCodeText.Text = "";
        PeerCopyButton.IsEnabled = PeerRunButton.IsEnabled = false;
    }

    private void PeerRole_Changed(object sender, RoutedEventArgs e)
    {
        if (PeerStatusText is null || _peerCancellation is not null) return;
        ResetPeer();
        PeerStatusText.Text = "";
    }

    private void PeerCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(MyCodeText.Text); PeerStatusText.Text = Loc.T("Code copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { PeerStatusText.Text = Loc.T("Could not copy; select the code and copy it by hand."); }
    }

    private void PeerCancel_Click(object sender, RoutedEventArgs e) => _peerCancellation?.Cancel();

    private static string PairingMessage(PairingError error) => Loc.T(error switch
    {
        PairingError.Checksum => "The code has a typing error; copy it again.",
        PairingError.NotPublic => "The code points at a private or local address; ask your friend to create it again.",
        PairingError.Expired => "This code has expired or starts too soon; ask your friend for a new one.",
        PairingError.TooFar => "This code starts too far ahead; ask your friend for a new one.",
        _ => "This is not a SockTuner code."
    });

    private void SetPeerBusy(bool busy)
    {
        InputsPanel.IsEnabled = RunButton.IsEnabled = !busy;
        PeerPrepareButton.IsEnabled = HostRadio.IsEnabled = GuestRadio.IsEnabled = PeerCodeText.IsEnabled = !busy;
        PeerCancelButton.IsEnabled = busy;
        BusyChanged?.Invoke(this, busy);
    }

    private async void PeerPrepare_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortText.Text.Trim(), out var port) || port is < 0 or > 65535)
        {
            PeerStatusText.Text = Loc.T("Choose a UDP port from 1 to 65535, or 0 for a temporary port.");
            return;
        }
        ResetPeer();
        var role = SelectedRole;
        PairingCode? host = null;
        if (role == PeerRole.Guest)
        {
            host = PairingCode.Parse(PeerCodeText.Text, DateTimeOffset.UtcNow.AddSeconds(20), out var error);
            if (host is null) { PeerStatusText.Text = PairingMessage(error); return; }
        }
        _peerCancellation = new CancellationTokenSource();
        SetPeerBusy(true);
        PeerStatusText.Text = Loc.T("Learning your public address...");
        try
        {
            var opened = await PeerNatSession.OpenAsync(role, port, _peerCancellation.Token);
            if (opened.Session is not { } session)
            {
                PeerStatusText.Text = Loc.T(PeerNatReading.Of(new(role, port, opened.Failure)));
                return;
            }
            _peerSession = session;
            _hostCode = host;
            var mine = host is null ? session.CreateHostCode(DateTimeOffset.UtcNow.AddMinutes(3)) : session.AcceptHostCode(host);
            MyCodeText.Text = mine.Format();
            PeerCopyButton.IsEnabled = PeerRunButton.IsEnabled = true;
            PeerStatusText.Text = Loc.T(role == PeerRole.Host
                ? "Send your code to your friend, paste their reply code, then press Start before the agreed time (about 3 minutes)."
                : "Send this reply code to the host, then press Start. The test begins at the time inside the host's code.");
        }
        catch (OperationCanceledException) { PeerStatusText.Text = Loc.T("Friend test cancelled; no NAT verdict."); }
        catch (Exception exception) { PeerStatusText.Text = Loc.F($"Friend test failed: {exception.Message}"); }
        finally
        {
            _peerCancellation.Dispose();
            _peerCancellation = null;
            SetPeerBusy(false);
        }
    }

    private async void PeerRun_Click(object sender, RoutedEventArgs e)
    {
        if (_peerSession is not { } session) return;
        var role = SelectedRole;
        PairingError error = PairingError.None;
        var peer = role == PeerRole.Guest ? _hostCode : PairingCode.Parse(PeerCodeText.Text, DateTimeOffset.UtcNow, out error);
        if (peer is null) { PeerStatusText.Text = PairingMessage(error); return; }
        _peerCancellation = new CancellationTokenSource();
        EvidenceGrid.ItemsSource = null;
        SetPeerBusy(true);
        PeerRunButton.IsEnabled = PeerCopyButton.IsEnabled = false;
        ResultText.Text = Loc.T("Friend test running; keep this window open until it finishes.");
        PeerStatusText.Text = Loc.F($"Waiting for the agreed start at {peer.Start.ToLocalTime():HH:mm:ss}; then about 75 seconds of testing.");
        try
        {
            var result = await session.RunAsync(peer, _peerCancellation.Token);
            ResultText.Text = Loc.T(PeerNatReading.Of(result));
            var measured = result.Failure == PeerNatFailure.None;
            string Seen(bool value) => Loc.T(measured ? (value ? "Received" : "Nothing received") : "Not measured");
            EvidenceGrid.ItemsSource = new List<EvidenceRow>
            {
                new(Loc.T("Local UDP port"), result.LocalPort.ToString(), Loc.T("Exclusively bound by SockTuner, not by the game.")),
                new(Loc.T("Unsolicited packet from your friend"), Seen(result.ColdInbound),
                    Loc.T("Sent from a port you never contacted, before you sent anything. This is what a game's own NAT check looks for.")),
                new(Loc.T("Both sending from the shared port"), Seen(result.PunchInbound),
                    Loc.T("Shows whether two routers can connect once both sides send first, as peer-to-peer games do.")),
                new(Loc.T("Friend's public port"), Loc.T(!measured || !result.PunchInbound ? "Not measured"
                    : result.PunchSourceMatchesCode ? "Matched their code" : "Differed from their code"),
                    Loc.T("A different port points at your friend's router, not yours.")),
                new(Loc.T("Windows Firewall"), Loc.T("Not checked"),
                    Loc.T("If nothing arrived, security software on this PC may have blocked it; allow SockTuner inbound and retry."))
            };
            if (result.Detail is { } detail) PeerStatusText.Text = detail;
        }
        catch (OperationCanceledException) { ResultText.Text = Loc.T("Friend test cancelled; no NAT verdict."); }
        catch (Exception exception) { ResultText.Text = Loc.F($"Friend test failed: {exception.Message}"); }
        finally
        {
            // A code is single-use: its secret and start time belong to this run only.
            _peerCancellation.Dispose();
            _peerCancellation = null;
            ResetPeer();
            SetPeerBusy(false);
            StatusChanged?.Invoke(this, ResultText.Text);
        }
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (GameCombo.SelectedItem is not GamePortPreset { SourceUrl.Length: > 0 } preset) return;
        try { Process.Start(new ProcessStartInfo(preset.SourceUrl) { UseShellExecute = true }); }
        catch (Exception exception) { ResultText.Text = Loc.F($"Could not open reference: {exception.Message}"); }
    }

    private sealed record EvidenceRow(string Observation, string Result, string Meaning);
}
