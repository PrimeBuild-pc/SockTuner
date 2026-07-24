# SockTuner Architecture Proposal

## 1. Context

SockTuner is a Windows 10/11 desktop application for advanced inspection, testing, and modification of:

- the Windows TCP/IP and Winsock layers;
- IPv4 and IPv6 interfaces, routes, DNS, MTU, and QoS;
- NDIS and global offload behavior;
- physical and virtual network adapters;
- driver-advertised NIC properties;
- network quality before and after a change.

The application is for technical users. The interface can expose advanced controls, but applying them must still be predictable, reversible, and auditable.

## 2. Architectural decisions

| Decision | Choice | Reason |
| --- | --- | --- |
| Language | C# on .NET 10 LTS | Strong Windows interop, type safety, mature diagnostics APIs, long support window |
| UI | WPF | Stable on Windows 10/11, low dependency surface, good desktop tooling and accessibility support |
| Process model | One executable with normal UI mode and short-lived elevated worker mode | Keeps read-only use unelevated without introducing a service or second installed product |
| Platform access | Supported API first, registry only where it is the documented/driver-defined interface | Avoids fragile parsing and unsupported global edits |
| External tools | None for normal operation; allowlisted Windows inbox fallback only when no usable API exists | Avoids PowerShell, curl, tshark, and similar runtime dependencies |
| Persistence | Versioned JSON snapshots, plans, reports, and logs | Human-inspectable, exportable, and supported by .NET without a database |
| Production projects | One WPF application plus one test project initially | No speculative service, plugin system, or library split |

### Why not the alternatives?

- **PowerShell** remains useful for research, but it is not the application runtime: property names can be localized, errors are often suppressed, and large GUI scripts are difficult to validate and maintain.
- **WinUI 3** adds Windows App SDK and deployment complexity without providing a required capability for this product.
- **Electron or another web shell** adds a browser runtime and a privileged bridge to a Windows-only utility.
- **C++ or Rust** would increase development and interop cost. A small native component can be introduced later only if a measured requirement cannot be met from C#.

## 3. Runtime shape

```text
┌──────────────────────────────────────────────────────────┐
│ SockTuner UI — normal user                              │
│ inventory · diagnostics · plan editor · reports         │
└───────────────────────┬──────────────────────────────────┘
                        │ typed, validated change plan
                        │ UAC only when Apply is requested
┌───────────────────────▼──────────────────────────────────┐
│ SockTuner elevated worker — same signed executable      │
│ snapshot · validate again · apply · verify · rollback   │
└───────────────────────┬──────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────┐
│ Windows management surfaces                            │
│ IP Helper · Winsock · ICMP · SetupAPI/Configuration     │
│ Manager · WMI/CIM · Registry · ETW where appropriate    │
└──────────────────────────────────────────────────────────┘
```

The elevated mode accepts only versioned, typed, allowlisted operations. It never executes arbitrary commands from a plan. It re-reads the target and current value immediately before writing, so a stale UI plan cannot silently overwrite a changed system.

A permanent Windows service is explicitly deferred. It should exist only if a future feature needs continuous privileged monitoring or policy enforcement.

## 4. Internal modules

These are folders/namespaces in one production project, not separate assemblies at the start.

| Module | Responsibility |
| --- | --- |
| Presentation | WPF views, navigation, validation, accessibility, and progress |
| Inventory | OS, CPU, adapter, driver, interface, route, DNS, binding, and capability discovery |
| Settings | Setting catalog, current/proposed values, plans, risk, compatibility, and evidence metadata |
| Apply | Snapshot, execution ordering, read-back verification, journaling, and rollback |
| Diagnostics | Ping, loss, jitter, DNS, TCP-connect timing, traceroute, path MTU, and comparison |
| Platform.Windows | Small wrappers around Windows APIs, CIM/WMI, and registry access |
| Persistence | Versioned JSON snapshots, reports, preferences, and logs |

A plugin API, dependency-injection framework, command bus, database, cloud backend, and background agent are not needed for the first release.

## 5. Windows integration strategy

| Capability | Preferred surface |
| --- | --- |
| Adapter and address inventory | IP Helper API (`GetAdaptersAddresses` and related tables) |
| Device identity and restart | SetupAPI and Configuration Manager APIs |
| Driver metadata and advertised values | NDIS/driver metadata through supported WMI/CIM providers; `Ndi\Params` metadata as a guarded fallback |
| TCP, interface, QoS, and offload state | `root/StandardCimv2` management classes where available |
| Registry-backed settings | `Microsoft.Win32.Registry`, with explicit value type and absent-value handling |
| Winsock catalog | Winsock 2 catalog APIs such as `WSAEnumProtocols`; repair operations remain separately risk-gated |
| ICMP and path MTU | .NET networking APIs and Windows ICMP APIs |
| DNS and socket tests | .NET DNS, socket, and HTTP APIs |
| Trace and event diagnostics | ETW only where it adds a validated requirement |

Implementation rules:

1. Identify an adapter by stable GUID/PNP identity, never by a name such as `Ethernet`.
2. Use driver registry keywords and advertised enum/range metadata, not English `DisplayName` strings.
3. Never create an unsupported driver parameter merely to make it appear in the adapter UI.
4. Gate settings by OS build, adapter capability, driver version, and current topology.
5. Do not invoke a command shell. If an inbox utility is temporarily required, call its absolute path with fixed argument construction, capture its exit code, and verify state independently.
6. PowerShell is a development/reference tool, not an end-user prerequisite.

## 6. Setting catalog

Every exposed setting needs a catalog entry before implementation:

- stable setting ID and category;
- target scope: global, TCP template, interface, adapter, driver, or process;
- read and write mechanism;
- data type, allowed values, and driver-advertised range;
- supported Windows builds and hardware constraints;
- default behavior and how to represent an absent registry value;
- restart requirement: none, adapter, sign-out, or system reboot;
- connectivity and stability risk;
- expected trade-off, not just an expected benefit;
- evidence level and source references;
- exact verification and rollback procedure.

### Evidence levels

1. **Documented** — supported by Microsoft or the hardware vendor.
2. **Driver-advertised** — exposed as a supported property by the installed driver.
3. **Experimental** — plausible but requires an explicit benchmark and warning.
4. **Blocked** — undocumented, obsolete, contradictory, security-sensitive, or not exactly reversible.

Profiles may include levels 1 and 2. Level 3 remains opt-in and cannot be silently applied by a preset. Level 4 is never writable through SockTuner.

## 7. Transaction and rollback model

Each Apply operation follows one path:

1. Acquire the single apply lock.
2. Re-detect the OS, adapter, driver, and capabilities.
3. Re-read every current value and reject stale or unsupported entries.
4. Save a versioned snapshot containing exact values and value absence.
5. Show or confirm the final diff and disruption requirements.
6. Apply operations in a deterministic order.
7. Read every value back from Windows.
8. Record success, unsupported status, or failure without suppressing errors.
9. On failure, restore already changed values in reverse order where safe.
10. Persist the result and any pending restart state.

Rollback restores the captured values; it does not run broad commands such as `netsh int ip reset`, remove devices, or guess “Windows defaults.” A snapshot belongs to the same system and adapter identity and cannot be imported blindly onto unrelated hardware.

Operations must be idempotent. Reapplying an already-matching plan should result in no writes.

## 8. Safety and security

- Read-only screens run without elevation; UAC is requested only for protected changes.
- The elevated worker exposes a strict operation allowlist and validates all values again.
- Plans cannot contain executable paths, scripts, shell fragments, or arbitrary registry paths.
- Active-link disruption, adapter restart, reboot, and remote-session risk are shown before confirmation.
- Destructive repair actions are separate from tuning and require stronger confirmation.
- Exported reports warn before including public IP addresses, MAC addresses, hostnames, routes, or adapter identifiers.
- No telemetry, remote control, account system, or cloud synchronization is planned for v1.
- Logs use bounded retention and redact unnecessary personal data.
- Public builds must be code-signed; updates must verify signatures before installation.

## 9. Gaming diagnostics and root-cause model

Gaming diagnosis is a first-class product area, not a generic ping screen. SockTuner should test consecutive network boundaries so it can identify where degradation first appears.

| Boundary | Signals | Likely findings | Local action when possible |
| --- | --- | --- | --- |
| PC and NIC | Link state/speed, driver state, error/discard counters, offloads, power state, local resource pressure | Driver fault, power transition, bad setting, local packet drops | Verified driver/NIC change or exact rollback |
| LAN or Wi-Fi | Gateway RTT, loss, jitter, link rate, retransmission/error indicators | Wi-Fi interference, weak signal, duplex/link problem, local congestion | Adapter fix, channel/cabling guidance, or router-side recommendation |
| Router/modem and access link | Gateway versus first public-hop behavior; idle versus loaded latency | Router overload, queueing, bufferbloat, access-link saturation | Safe endpoint QoS guidance; explain when router SQM/AQM is required |
| ISP edge | Repeated first-hop and downstream measurements | Access or ISP loss, congestion, unstable first mile | Evidence package for the ISP; no fake local fix |
| Routing and peering | Repeated traceroute samples, route changes, latency step, destination comparison | Bad route, congested transit/peering, wrong region | Region/endpoint guidance and ISP escalation data |
| Game endpoint | RTT, jitter, loss, reachability, protocol, selected region, and session timing | Remote server load, distant region, game-specific path issue | Prefer a nearer available region or report the remote issue |
| Name resolution | Resolver timing, failures, returned addresses, and cache behavior | Slow/failing DNS or region selection affected during connection setup | Verified resolver/configuration change |

A DNS result must not be blamed for steady in-match latency: DNS normally affects lookup and connection setup, not packet RTT after the game session is established.

### Diagnostic sequence

1. Capture adapter, route, DNS, and relevant Windows counters before probing.
2. Run concurrent, timestamped probes to the gateway, first reachable ISP boundary, neutral reference targets, and the selected game endpoint.
3. Report minimum, median, average, P95, P99, maximum, loss, and a named jitter calculation rather than relying on an average alone.
4. Sample the route repeatedly; a single traceroute is insufficient.
5. Test DNS resolution and TCP connection setup separately from game-path latency.
6. Run path-MTU checks and detect explicit ICMP blocking rather than guessing an MTU.
7. When loaded-latency testing is enabled, measure the same boundaries before, during, and after controlled download and upload load.
8. Compare the result with an identical baseline and retain raw samples in the report.

The game endpoint can initially come from a game/region profile or direct user input. Automatic process-to-flow detection can later use native ETW events; it does not justify a custom packet-capture driver in v1.

### Diagnosis output

The analysis engine correlates where a symptom begins and returns:

- one or more candidate causes ranked by confidence;
- the observations supporting and contradicting each cause;
- whether the cause is local, LAN/router, ISP/routing, or remote-server controlled;
- a targeted SockTuner change only when the application can safely affect it;
- otherwise, concrete router guidance or an exportable ISP/support report;
- an **inconclusive** result when the evidence cannot isolate the fault.

Traceroute hops may rate-limit or deprioritize ICMP, game servers may block probes, and geographic data may be approximate. SockTuner must not label an intermediate hop as faulty unless end-to-end and repeated measurements support that conclusion.

Base RTT remains constrained by route, physical distance, access technology, and server behavior. TCP-only changes are not presented as UDP game optimizations, and throughput or bufferbloat results are never inferred from registry state alone.

## 10. Data and packaging

Application-owned data is stored under `%LocalAppData%\PrimeBuild\SockTuner`:

```text
Snapshots/   exact rollback data
Reports/     diagnostic and comparison reports
Logs/        bounded operational logs
Settings/    user preferences and profile copies
```

Initial distribution is a signed, self-contained x64 Windows build. Installer technology, auto-update, ARM64, portable mode, and Microsoft Store packaging are deferred until the core works and deployment constraints are known.

## 11. Test strategy

- Unit tests for plan validation, setting serialization, ranges, ordering, and rollback semantics.
- Integration tests for each Windows surface on disposable VMs.
- Read/apply/read/rollback/read tests for every writable setting.
- Tests on English and Italian Windows to detect localization assumptions.
- Hardware coverage for common Intel and Realtek Ethernet drivers first; unsupported hardware remains read-only rather than receiving guessed values.
- Failure injection around access denied, driver rejection, adapter disappearance, network loss, cancellation, and reboot-required states.
- Performance benchmarks only where a setting is claimed to affect latency, jitter, throughput, or CPU use.

## 12. Approval points

Implementation should start only after agreement on these decisions:

- C#/.NET 10 LTS and WPF;
- Windows 10 22H2 and supported Windows 11 x64 releases as the initial matrix;
- normally unelevated UI plus same-executable elevated worker;
- native/API-first behavior with no third-party runtime tools;
- evidence-gated settings and exact rollback rather than an aggressive “apply everything” model;
- no packet-capture driver, service, cloud backend, or plugin system in v1.
