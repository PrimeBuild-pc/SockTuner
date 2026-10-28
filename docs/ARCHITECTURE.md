# SockTuner Architecture

SockTuner is a Windows 10/11 desktop application that inspects, measures and — when the user asks —
changes the Windows TCP/IP and Winsock layers, interfaces, routes, DNS, MTU, QoS, offloads, interrupt
placement and driver-advertised NIC properties. It is built for technical users: controls can be
advanced, but every change is predictable, verified, audited and reversible.

## 1. Decisions

| Decision | Choice | Reason |
| --- | --- | --- |
| Language | C# on .NET 10 LTS | Strong Windows interop, type safety, mature diagnostics APIs, long support window |
| UI | WPF with the Fluent theme | Native on Windows 10/11, no browser runtime, low dependency surface |
| Process model | One executable: a normal-user UI and a short-lived elevated worker mode | Read-only use never needs elevation; no service or second installed product |
| Platform access | Supported APIs first; registry only where it is the documented or driver-defined interface | Avoids fragile parsing and unsupported global edits |
| External tools | None at runtime; the app never starts a shell, `netsh` or PowerShell | Localized output, suppressed errors and unverifiable side effects |
| Persistence | Versioned JSON files under the user's profile | Human-inspectable, exportable, no database |
| Projects | One WPF application and one xUnit test project | No speculative service, plugin system or library split |

WinUI 3 would add Windows App SDK deployment without a needed capability; Electron would add a browser
runtime and a privileged bridge to a Windows-only tool; C++ or Rust would raise interop cost for no
measured gain.

## 2. Runtime shape

```text
┌──────────────────────────────────────────────────────────┐
│ SockTuner UI — normal user                               │
│ inventory · diagnostics · tuning plan · reports          │
└───────────────────────┬──────────────────────────────────┘
                        │ typed request over a private named pipe
                        │ UAC only when a change is applied
┌───────────────────────▼──────────────────────────────────┐
│ SockTuner --elevated-worker — same executable            │
│ re-read · validate again · apply · verify · audit        │
└───────────────────────┬──────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────┐
│ Windows management surfaces                              │
│ .NET networking · CIM (root/StandardCimv2) · Registry    │
│ WLAN API · Winsock catalog · ICMP                        │
└──────────────────────────────────────────────────────────┘
```

The worker serves exactly one request and exits. It accepts only versioned, typed operations on
catalogued settings, re-reads every target immediately before writing, and refuses a value that drifted
since the preview. Views hand a change to it through one helper (`Views/ElevatedChange`), which turns
every failure — a declined UAC prompt, a worker that dies without answering — into a status line.

## 3. Source layout

| Folder | Responsibility |
| --- | --- |
| `Views/` | One WPF view per page (tuning plan, Wi-Fi, throughput, DNS, interfaces, interrupts, recommendations, history, preferences, updates); `MainWindow` hosts them and owns inventory, gaming diagnostics and the dashboard |
| `Models/` | Immutable records for inventory, measurements, settings and reports |
| `Services/` | Windows inventories, setting specifications and stores, the transaction service, the elevated worker, updates and localization |
| `Services/Collection/` | Active probes and passive collectors (throughput, loaded latency, route quality, DNS benchmark, Wi-Fi, interrupt affinity) |
| `Services/Diagnosis/` | Pure analyzers over collected facts: playability, bottleneck location, stability, Wi-Fi, health check, drift |
| `Services/Remediation/` | Turns findings into tuning-plan changes and router guidance |
| `Persistence/` | Preferences, transaction audit, diagnostic history, logs and exports |

## 4. Windows integration rules

1. Identify an adapter by its GUID/PnP identity, never by a name such as `Ethernet`.
2. Use driver registry keywords and advertised enum/range metadata, not English display strings.
3. Never create a driver parameter the driver does not advertise.
4. Gate settings by OS build, adapter capability, driver version and current topology.
5. Never invoke a command shell or inbox utility to read or change state.
6. Keyword characterisation is advisory, never an allowlist. `NicKeywordCatalog` annotates what a driver
   already advertises; it never adds or hides one. The one exception runs the other way: a keyword marked
   `Rejected` resolves to evidence level `Blocked` and is refused, so characterisation can withhold a
   write but never authorise one.

## 5. Settings and evidence

Every writable setting has a catalog entry or a driver-derived specification with: a stable ID and scope,
its read/write mechanism, type and accepted values, how an absent value is represented, the restart it
needs, its risk and trade-off, its evidence level, and how it is verified and rolled back.

1. **Documented** — supported by Microsoft or the hardware vendor.
2. **Driver-advertised** — exposed as a supported property by the installed driver.
3. **Experimental** — plausible, needs a benchmark, and needs a typed `APPLY` to confirm.
4. **Blocked** — undocumented, obsolete, contradictory, security-sensitive or not exactly reversible;
   never writable.

## 6. Transactions and rollback

A change goes through one path:

1. **Prepare** reads every current value through a read-only store and builds the plan the user previews.
2. **Apply** takes the single apply lock and, for each change in a deterministic order, re-reads the value,
   refuses it if it no longer matches the preview, writes, and reads it back.
3. On any failure the changes already made are restored in reverse order.
4. The result is written to the transaction audit, then touched adapters are restarted and checked back
   to their previous link state.

Rollback writes back exactly the values the audit captured, and only where the current value is still the
one SockTuner wrote; anything changed externally since is reported, not overwritten. It never runs broad
resets such as `netsh int ip reset` or guesses "Windows defaults". Reapplying a matching plan writes nothing.

## 7. Safety and privacy

- Read-only pages run without elevation; UAC is requested per change.
- Live changes need a one-time, versioned consent that can be withdrawn in Preferences.
- A change over a remote session on the adapter it affects needs a typed confirmation.
- Requests cannot carry executable paths, scripts, shell fragments or arbitrary registry paths.
- Support exports and compatibility reports redact addresses, MAC and device identifiers, SSIDs, paths
  and the machine name. There is no telemetry, account or cloud service.
- Logs are bounded (two rotating files of a user-chosen size).

## 8. Gaming diagnostics

Gaming diagnosis tests consecutive network boundaries to find where degradation first appears.

| Boundary | Signals | Likely findings | Local action when possible |
| --- | --- | --- | --- |
| PC and NIC | Link state/speed, driver state, error/discard counters, offloads, power state | Driver fault, power transition, bad setting, local drops | Verified NIC change or exact rollback |
| LAN or Wi-Fi | Gateway RTT, loss, jitter, link rate, radio facts | Interference, weak signal, link problem, local congestion | Adapter fix, channel/cabling guidance, router recommendation |
| Router and access link | Gateway versus first public hop; idle versus loaded latency | Router overload, bufferbloat, access-link saturation | Router SQM/AQM guidance computed from the measured rate |
| ISP edge | Repeated first-hop and downstream measurements | Access or ISP loss, congestion | Evidence for the ISP; no fake local fix |
| Routing and peering | Repeated route samples, latency steps | Bad route, congested transit, wrong region | Region guidance and ISP escalation data |
| Game endpoint | RTT, jitter, loss, reachability against the game's tick rate | Remote load, distant region | Prefer a nearer region or report the remote issue |
| Name resolution | Resolver timing and failures | Slow or failing DNS at connection setup | Verified resolver change |

DNS is never blamed for steady in-match latency: it affects lookup and connection setup, not packet RTT
once a session is established.

A run captures adapters, routes, DNS and counters first; probes the gateway, a neutral reference and the
game endpoint concurrently; reports minimum, median, P95, P99, maximum, loss and a named jitter measure;
samples the route repeatedly; tests DNS and TCP setup separately; checks path MTU and detects ICMP
blocking instead of guessing. Loaded-latency and throughput tests generate traffic, so they stay separate
and opt-in. A run made before a change becomes the baseline for the identical run after it.

The analysis returns ranked candidate causes with the observations for and against each, who owns the
fix (local, LAN/router, ISP/routing or remote server), a SockTuner change only where it can safely make
one, and **inconclusive** when the evidence cannot isolate the fault. An intermediate hop is not labelled
faulty unless repeated end-to-end measurements support it, TCP-only changes are not presented as UDP game
fixes, and throughput or bufferbloat is never inferred from registry state.

### Game ports and NAT observations

`NatDiagnosticsView` owns a separate, opt-in `NatDiagnosticProbe`. It exclusively binds the chosen
IPv4 UDP port, sends validated STUN binding requests from that one socket and separately reports
mapping samples, controlled alternate-source-port behavior and unverified game inbound reachability.
It never shares a running game's socket, substitutes an occupied port, writes firewall/router rules
or converts timeouts into an Open/Moderate/Strict grade. See [method and limits](NAT-DIAGNOSTICS.md).

## 9. Passive Wi-Fi diagnostics

```text
Windows WLAN API cached state
        ↓
WindowsWifiInventory (facts only; never calls WlanScan)
        ↓
GamingWifiDiagnosticEngine (pure correlation)
        ↓
Wi-Fi view · gaming report · recommendations · redacted export
```

The collector enumerates every WLAN interface and reads the current association, negotiated PHY and rates,
authentication and cipher, radio capability, cached BSS entries and bounded information elements. HT, VHT
and HE operation data give the occupied width; RSN, PMF, WPS and BSS Load stay facts and are not latency
causes by themselves. Unknown or malformed elements are ignored rather than guessed.

The 60-second observation samples only the current association every two seconds, is cancellable, and
never requests an active scan. Gateway instability without corroborating radio evidence stays
inconclusive instead of being labelled a Wi-Fi fault. Windows location-policy denial is reported with a
link to Settings; it is not worked around with elevation. The decision record is
[ADR 0001](adr/0001-native-passive-wifi-diagnostics.md).

The dashboard draws sparklines only from samples an explicit diagnosis or observation already collected;
it never starts probes on its own, and missing replies stay visible as loss markers.

## 10. Data and distribution

Application data lives under `%LocalAppData%\PrimeBuild\SockTuner`:

```text
TransactionAudit/  one JSON record per apply or rollback (newest 100), the source of every undo
History/           saved diagnostic runs (newest 20)
Logs/              current and previous log file
Settings/          preferences.json
Updates/           downloaded update executables
```

Releases are self-contained single-file x64 builds published to GitHub Releases with a SHA-256 checksum.
Alpha builds are unsigned. The release workflow signs the executable when a certificate is configured;
automatic update installation requires both the installed app and the update to carry trusted
Authenticode signatures from the same publisher, so unsigned builds can find updates but not install them.

## 11. Tests

- Unit tests (xUnit) for specifications, plan validation, transactions and rollback, the elevated worker
  protocol, analyzers, importers, exports and localization coverage; CI runs them with a formatting check.
- Read-only live inventory tests run when `SOCKTUNER_LIVE_INVENTORY=1`.
- `--verify-tcp-writes` and `--verify-device-writes` exercise real write, read-back and rollback; they
  only arm when `SOCKTUNER_VM_WRITE_TEST=1`, for a disposable VM.
- Hardware coverage comes from compatibility reports, summarised in the
  [capability archive](../alpha-tester-output/INDEX.md); unsupported hardware stays read-only.
