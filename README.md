# SockTuner

**Advanced, all-in-one network tuning and diagnostics for Windows 10 and Windows 11.**

> **Status: pre-alpha development.** Read-only inventory and gaming diagnostics are implemented. The typed tuning catalog and transaction engine are under isolated test; UI-triggered mutations remain disabled.

SockTuner is intended for tweakers, technicians, competitive gamers, system integrators, and power users who need one place to inspect and control the Windows networking stack, network adapters, and NIC driver settings.

It is not a generic “make my ping lower” button. SockTuner will show the current value, proposed value, scope, expected trade-off, restart requirement, and rollback data for every change.

## Product goals

- Inspect the system before recommending or changing anything.
- Cover Windows TCP/IP, Winsock, IP interfaces, NIC offloads, RSS, interrupts, power management, MTU, DNS, QoS, and driver-advertised advanced properties.
- Prefer documented Windows APIs and driver capabilities over scripts, hard-coded adapter names, or localized property labels.
- Preview every change as a diff and verify it after application.
- Snapshot exact original values and provide reliable per-session rollback.
- Measure latency, jitter, packet loss, path MTU, DNS response, route quality, and before/after results.
- Diagnose gaming connectivity layer by layer: PC/NIC, LAN or Wi-Fi, router/modem, access link, ISP, routing/peering, server region, and remote endpoint.
- Run without third-party command-line tools for normal operation.
- Remain useful to experts: no hidden presets and no unexplained “recommended” values.

## Planned feature areas

| Area | Scope |
| --- | --- |
| System inventory | OS build, CPU topology, active routes, interfaces, driver identity/version, link state, addresses, DNS, bindings, and supported NIC properties |
| NIC tuning | Interrupt moderation, RSS, RSC, LSO, checksum offloads, USO/URO, queue and buffer settings, flow control, jumbo frames, EEE, wake, and power-saving controls |
| Windows networking | TCP templates and global settings, per-interface settings, MTU, metrics, DNS, QoS policies, TCP ACK/Nagle controls, and Winsock catalog inspection |
| Gaming diagnostics | Layered PC-to-game-server testing, latency/loss/jitter percentiles, route and region analysis, DNS and connection timing, path MTU, loaded latency, likely-cause ranking, and targeted fixes |
| Change management | Dry run, compatibility gates, risk labels, snapshots, read-back verification, audit history, export, and exact rollback |
| Profiles | Transparent, editable profiles built only from independently supported and tested settings |

See the [Documentation Index](docs/README.md) and [Product Scope](docs/PRODUCT_SCOPE.md) for the proposed feature boundary.

## Proposed technology

- **Language:** C#
- **Runtime:** .NET 10 LTS
- **Desktop UI:** WPF
- **Initial target:** Windows 10 22H2 and supported Windows 11 releases, x64
- **Distribution:** signed, self-contained Windows build

WPF is the deliberate choice for a Windows-only administrative tool: it is mature, works across Windows 10 and 11, integrates cleanly with native Windows management surfaces, and avoids a browser runtime or an unnecessary UI platform dependency.

Read the full [Architecture](docs/ARCHITECTURE.md), [Implementation Roadmap](docs/ROADMAP.md), and [Development Guide](docs/DEVELOPMENT.md).

## Engineering position

Network settings are workload-, driver-, OS-, and topology-dependent. Disabling RSS, ECN, auto-tuning, offloads, or interrupt moderation is not universally beneficial. Nagle-related changes affect TCP, while many games primarily use UDP. Client-side throttling is not a universal replacement for router-side SQM/AQM and cannot eliminate every form of bufferbloat.

The reference scripts in this private workspace are research inputs, not production code or verified recommendations. Their conflicting values and undocumented registry edits must be validated against official documentation, driver-advertised capabilities, repeatable benchmarks, and rollback tests before they can become SockTuner features.

## Current stage

- Architecture and product scope approved.
- Step 1 is complete: the dark Metro shell, application icon, OS/adapter inventory, driver-advertised NDIS discovery, filtering/copy, and DPI validation pass.
- Native IPv4/IPv6 routes and metrics, interface indexes/MTU/DNS, network profiles and bindings, NIC counters, TCP templates, QoS policies, global/RSS/RSC/LSO/checksum/USO/URO offload state, Winsock catalog, bounded structured logs, full/redacted snapshot export, retention preferences, and log export provide the Step 2 inventory.
- Steps 1–2 now pass inventory, export, search/copy, dark-theme, and 100–200% WPF DPI validation.
- Step 3 adds explicit quick/standard/extended diagnostics, concurrent boundary probes, full statistics/timelines, route/MTU/counter evidence, and bounded continuous monitoring.
- Step 4 includes versioned JSON, self-contained offline HTML, bounded local history, redacted history export, identical-parameter before/after comparison, and multi-run trends.
- Step 5 is complete with an allowlisted dry-run cart, exact rollback preview, bounded audit history, deterministic transactions, failure injection, and a strict typed elevated-worker protocol.
- Production apply remains locked while the first low-risk settings enter the disposable-VM Step 6 gate.

## Important notice

Changing network, registry, adapter, or driver settings can interrupt connectivity or reduce stability and throughput. Future pre-release builds must be tested on disposable systems or with a known recovery path.

License and public contribution policy are not yet defined.
