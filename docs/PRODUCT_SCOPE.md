# Product Scope

## 1. Product definition

SockTuner is an expert Windows network workbench: inventory, diagnostics, controlled tuning, verification, and recovery in one desktop application.

Its value is not the number of registry keys it can write. Its value is making supported settings discoverable, comparable, measurable, and reversible across Windows builds and NIC drivers.

## 2. Primary users

- PC and network technicians;
- system integrators and performance tuners;
- competitive gamers who understand networking trade-offs;
- administrators investigating Windows endpoint networking;
- power users replacing collections of one-off scripts.

SockTuner will not hide technical terminology or pretend that one profile is optimal for every machine.

## 3. Proposed feature set

### 3.1 Inventory and baseline — v1

- Windows edition, build, architecture, and reboot state.
- CPU topology relevant to RSS and interrupt placement.
- Physical and virtual adapter classification.
- Stable adapter GUID, PNP identity, PCI vendor/device ID, driver provider/version/date, NDIS version, link speed, and status.
- IPv4/IPv6 addresses, gateways, routes, metrics, MTU, DNS servers, and active network profile.
- Adapter bindings, power-management state, global offloads, TCP templates, QoS policies, and Winsock providers.
- Driver-advertised advanced properties with raw keyword, display label, current value, valid values/range, and default when available.
- Exportable JSON diagnostic snapshot and redacted support report.

### 3.2 Change safety — v1

- Change cart with current and proposed values.
- Dry-run diff before UAC.
- Per-setting compatibility, evidence, trade-off, risk, and restart labels.
- Exact pre-change snapshot.
- Deterministic apply and independent read-back verification.
- Per-session history and rollback.
- Adapter-restart and reboot coordination.
- Partial-failure recovery and actionable errors.

### 3.3 NIC and driver tuning — v1, capability-dependent

- RSS state, profile, queue count, and supported CPU parameters.
- RSC, LSO, checksum offload, USO/URO, and other advertised offloads.
- Interrupt moderation and driver-advertised moderation rate.
- Receive/transmit buffers and queues within advertised ranges.
- Flow control, jumbo frames, VLAN/priority tagging, and link settings.
- EEE, Green Ethernet, selective suspend, wake options, and adapter power management.
- Bindings inventory; risky binding changes remain separate and never use “disable everything except IPv4/IPv6.”
- Vendor-neutral discovery, with tested guidance for common Intel and Realtek Ethernet adapters first.

Rules:

- A missing property is “unsupported,” not an invitation to inject a registry key.
- Values are discovered from the installed driver rather than guessed from the adapter brand.
- Throughput, CPU load, virtualization, Wake-on-LAN, and energy use trade-offs are always shown.

### 3.4 TCP/IP, interface, and Winsock — v1

- TCP auto-tuning, ECN, timestamps, RSC, initial RTO, and available congestion-control templates.
- IPv4/IPv6 MTU, interface metrics, DNS configuration, and selected advanced interface properties.
- Path-MTU discovery and a guided MTU change; no hard-coded ISP table as an authoritative answer.
- TCP ACK/Nagle controls scoped to selected interfaces, clearly marked as TCP-only and experimental where documentation is insufficient.
- QoS policy inventory and supported egress policy editing.
- Winsock protocol and namespace catalog inspection.
- Targeted repair actions only when exact recovery is available; broad stack resets are not normal tuning operations.

The first release excludes undocumented AFD/NDIS “magic values,” forced congestion providers that the OS does not expose as supported, blanket IPv6 removal, and global changes to every hidden adapter.

### 3.5 Gaming diagnostics — v1

The diagnostic workflow must answer more than “is ping high?” It should isolate the first boundary at which latency, jitter, or loss appears.

- Configurable, concurrent probes to the local gateway, first reachable ISP boundary, neutral reference targets, and a selected game endpoint.
- Sent/received/lost counts plus minimum, average, maximum, median, P95, and P99 RTT.
- A documented jitter calculation, raw sample timeline, and spike distribution.
- Repeated route sampling to detect path changes and persistent latency steps without misreading ICMP rate limiting as packet loss.
- DNS resolution timing, failures, returned-address comparison, and a clear distinction between connection setup and in-session latency.
- TCP connection timing and UDP/game-endpoint reachability where the protocol permits a valid test.
- IPv4 path-MTU discovery using the Don’t Fragment behavior, multiple targets, and explicit ICMP-blocked results.
- NIC/link counters and configuration correlation to detect local errors, power-saving transitions, driver problems, or incompatible tuning.
- Guided classification of likely causes: PC/NIC, Ethernet/Wi-Fi LAN, router/modem, access-link saturation, ISP edge, routing/peering, server distance/region, or remote service.
- Confidence-ranked findings with supporting and contradicting evidence; “inconclusive” is a valid result.
- Targeted local fixes only when SockTuner controls the cause; otherwise router guidance or an exportable ISP/support report.
- Baseline and post-change runs with identical targets, intervals, sample counts, and load conditions.
- JSON and self-contained HTML export.

Loaded-latency testing should measure gateway, ISP, reference, and game-path behavior before, during, and after controlled download/upload load. It requires an explicitly selected test endpoint and must explain when router-side SQM/AQM, ISP intervention, a different game region, or server-side action is the real remedy.

Initial game targets may come from a known profile or direct user input. Automatic process-to-remote-endpoint correlation is a later native ETW feature.

### 3.6 Profiles and recommendations — after the change engine

Initial profiles should be few and transparent:

- **Windows Default / Restore Snapshot** — restores captured values, not guessed defaults.
- **Balanced** — preserves throughput and power behavior unless a documented issue is detected.
- **Low Latency** — proposes supported latency/CPU trade-offs for the selected adapter.
- **Custom** — user-selected values with validation.

A profile is an editable change plan. It must never silently change unrelated devices or settings.

Recommendations use system facts and measured results. They do not claim that disabling ECN, RSS, auto-tuning, checksum offload, or interrupt moderation is universally correct.

### 3.7 Reporting and comparison — after native diagnostics

- Before/after timelines and metric deltas.
- Multiple-run history with consistent test parameters.
- Machine-readable JSON schema with a version number.
- Self-contained HTML reports without CDN dependencies.
- Optional report redaction before sharing.
- Clear separation of observation, interpretation, and recommendation.

## 4. Later features

These remain outside v1 until the core is stable:

- import of Waveform, LibreQoS, or other bufferbloat reports;
- browser-assisted test launch and result correlation;
- import and correlation for additional loaded-latency and throughput test services;
- automatic process-to-remote-endpoint correlation and game session analysis;
- ETW-based packet/flow timing analysis;
- optional PCAP import;
- Wi-Fi-specific radio and roaming controls;
- Marvell/Aquantia, Mellanox/NVIDIA, Killer, USB, and enterprise NIC validation packs;
- CLI automation and signed profile import/export;
- ARM64 builds and managed deployment.

## 5. Explicitly deferred or rejected

| Item | Decision |
| --- | --- |
| One-click “optimize everything” | Rejected: hides scope and produces unsafe global changes |
| Custom packet-capture kernel driver | Deferred: signing, security, stability, and maintenance cost are not justified for v1 |
| Required Wireshark, Npcap, tshark, curl, or PowerShell | Rejected for normal operation |
| Automatic driver download/update | Deferred: separate supply-chain and recovery problem |
| Router configuration | Out of scope for the Windows desktop core |
| Cloud accounts and telemetry | Not planned for v1 |
| Community tweak marketplace/plugins | Deferred until a secure, evidence-based format is proven necessary |
| Creating unknown driver registry keys | Rejected |
| Device removal as “rollback” | Rejected |
| Broad IP/Winsock reset as profile rollback | Rejected |

## 6. Accuracy boundaries

SockTuner can tune and measure the Windows endpoint. It cannot:

- reduce propagation delay or move a game server closer;
- guarantee lower ping, jitter, packet loss, or bufferbloat;
- control congestion inside an ISP or remote network;
- replace router-side SQM/AQM for every bufferbloat problem;
- infer gaming performance from DNS response time;
- make a TCP tweak improve a UDP workload by definition;
- guarantee that a driver update preserves vendor-specific setting semantics.

These boundaries should remain visible in product documentation and recommendations.

## 7. Source-material policy

The local scripts, reports, binaries, nested repositories, and third-party tools are a private research corpus. Before public release:

- verify every technical claim independently;
- identify copyright and redistribution terms;
- do not copy third-party code without compatible permission;
- remove external executables, captures, personal reports, and nested `.git` directories from the distributable repository;
- keep only original documentation, validated implementation, test fixtures with clear provenance, and required notices.
