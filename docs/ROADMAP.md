# Implementation Roadmap

No dates are assigned yet. Each phase ends with a working, reviewable increment and must meet its exit criteria before the next phase expands writable scope.

## Phase 0 — Specification and evidence catalog

### Deliverables

- Approve [Architecture](ARCHITECTURE.md) and [Product Scope](PRODUCT_SCOPE.md).
- Inventory all candidate settings found in the research corpus.
- Build the setting catalog: API, scope, type, supported values, OS/driver gates, risk, evidence, verification, restart, and rollback.
- Resolve contradictory recommendations in the source material through official documentation and repeatable tests.
- Define the versioned JSON schemas for inventory, plans, snapshots, and reports.
- Audit source provenance and licenses; separate private research material from future public repository content.

### Exit criteria

- No candidate setting enters implementation without an evidence classification.
- Initial Windows and hardware test matrix is agreed.
- Public claims are limited to capabilities the project can actually test.

## Phase 1 — Read-only desktop foundation (`0.1`) — In progress

The first increment provides the WPF shell, normal-user startup, initial OS/adapter inventory, visible per-adapter read errors, refresh, and unit-test foundation. Driver, route, TCP, QoS, Winsock, export, and persistence coverage remain in this phase.

### Deliverables

- Create the C#/.NET 10 WPF application and one test project.
- Implement navigation, error presentation, structured logging, and local JSON persistence.
- Discover OS, CPU, adapters, drivers, interfaces, addresses, routes, metrics, DNS, bindings, TCP state, offloads, QoS, Winsock catalog, and driver-advertised properties.
- Add refresh, search/filter, copy, JSON export, and redacted support report.
- Run normally without administrator rights where Windows permits.

### Exit criteria

- No setting can be modified.
- Inventory works on the Windows test matrix and on non-English Windows.
- Physical, virtual, disconnected, and unsupported adapters are identified without hard-coded names.
- Errors are visible; no blanket silent failure handling.

## Phase 2 — Transactional change engine (`0.2`) — In progress

The first increment implements a typed allowlist, write-boundary value/address/adapter validation, serialized apply/rollback, immediate stale checks, read-back verification, and session/machine-bound rollback that refuses external drift. Failure tests use an in-memory store. A Windows registry backend exists but is locked behind an explicit operator confirmation intended only for a disposable VM; the guard does not prove virtualization. The UI remains catalog-only and cannot invoke writes.

### Deliverables

- Add typed change plans and value validation.
- Add same-executable elevated worker mode with strict operation allowlist.
- Revalidate targets and current values after elevation.
- Implement snapshots, deterministic ordering, read-back verification, history, and exact rollback.
- Add apply locking, cancellation boundaries, restart/reboot states, and failure injection tests.
- Start with a small set of low-risk settings to prove the full lifecycle.

### Exit criteria

For every writable setting in this phase:

```text
read → plan → snapshot → apply → verify → rollback → verify original state
```

must pass on disposable systems. Arbitrary commands and arbitrary registry paths cannot enter an elevated plan.

## Phase 3 — NIC and driver controls (`0.3`)

### Deliverables

- Implement supported offload, RSS, moderation, flow-control, buffer/queue, power, wake, and link controls.
- Enumerate valid driver values and ranges from the installed driver.
- Add adapter-restart planning and reconnection verification.
- Add transparent Balanced and Low Latency proposal profiles for validated hardware.
- Validate common Intel and Realtek Ethernet families first; all other adapters remain capability-driven and may be read-only.

### Exit criteria

- Unsupported settings are never created.
- Profiles produce a visible plan and touch only the selected adapter.
- Adapter restart, network loss, driver rejection, and rollback paths are tested.
- Performance recommendations include throughput, CPU, power, virtualization, and Wake-on-LAN trade-offs.

## Phase 4 — TCP/IP, interface, QoS, and Winsock (`0.4`)

### Deliverables

- Implement supported TCP global/template controls.
- Add selected IPv4/IPv6 interface controls, MTU, metrics, DNS, and QoS policy management.
- Add carefully scoped TCP ACK/Nagle controls with protocol and evidence warnings.
- Add Winsock catalog inspection and separately gated repair workflows.
- Separate tuning, connectivity repair, and destructive reset actions in the UI.

### Exit criteria

- Every setting is gated by the active OS surface rather than an OS-name guess.
- IPv6, adapter bindings, and hidden adapters are never blanket-disabled.
- Rollback restores exact captured state; no broad reset command is presented as rollback.
- A remote-session warning appears before changes that may break connectivity.

## Phase 5 — Native diagnostic foundation (`0.5`) — In progress

The first diagnostic increment provides concurrent gateway/reference/game-endpoint ICMP sampling, DNS and optional TCP-connect timing, percentile/jitter/loss calculations, cancellation, and evidence-ranked findings. Repeated routes, path MTU, NIC counters, longer profiles, and report export remain.

### Deliverables

- Add concurrent ICMP latency/loss runs, named jitter calculation, DNS timing, TCP-connect timing, repeated traceroute, path-MTU discovery, and relevant NIC/link counters.
- Support gateway, first reachable ISP boundary, neutral reference, game endpoint, and custom targets.
- Record timestamped raw samples and minimum, median, average, P95, P99, maximum, loss, and spikes.
- Add baseline and post-change runs using identical parameters.
- Add versioned JSON and self-contained HTML reports.
- Distinguish timeouts, blocked/deprioritized ICMP, DNS failure, route failure, remote refusal, and local API failure.

### Exit criteria

- Calculations have deterministic tests using recorded fixtures.
- Reports state sample count, duration, interval, targets, route samples, load conditions, and calculation method.
- Intermediate-hop behavior is not called packet loss unless end-to-end evidence supports it.
- No metric is labeled “gaming improvement” without a valid before/after comparison.

## Phase 6 — Gaming root-cause analysis and recommendations (`0.6`)

### Deliverables

- Add a guided gaming diagnostic using a game/region profile or user-provided game endpoint.
- Correlate PC/NIC, gateway, LAN/Wi-Fi, router/access link, ISP edge, route/peering, region/distance, DNS, and remote-endpoint evidence.
- Add confidence-ranked candidate causes with supporting and contradicting observations.
- Separate local fixes from router guidance, ISP escalation, region selection, and remote-server findings.
- Add controlled loaded-latency testing with explicit test-service selection.
- Add multi-run comparison, profile result tracking, and an exportable ISP/support evidence report.
- Add report redaction and import/export for SockTuner-owned schemas.
- Refine recovery UX and provide an offline emergency-restore workflow.

### Exit criteria

- The analyzer can return **inconclusive** instead of inventing a cause.
- DNS is not blamed for steady in-session RTT without evidence that endpoint selection changed.
- Every local recommendation links to observed facts, evidence level, expected trade-off, and proposed diff.
- External causes produce evidence and guidance, not ineffective Windows tweaks.
- Profiles remain editable plans, not opaque macros.
- Recovery works after interrupted application and after reboot-required changes.

## Phase 7 — Hardening and private beta (`0.9`)

### Deliverables

- Test clean Windows 10 22H2 and supported Windows 11 installations.
- Expand hardware and driver matrix.
- Complete accessibility, localization resilience, performance, and long-run stability checks.
- Threat-model the elevated worker and update path.
- Add code signing, installer, SBOM, privacy notice, license, and vulnerability reporting policy.
- Remove private research artifacts and third-party material from the release tree.

### Exit criteria

- No critical rollback, privilege-boundary, data-loss, or connectivity-recovery defects.
- All distributed files have known provenance and licenses.
- Signed builds install, update, repair, and uninstall cleanly.

## Phase 8 — Public `1.0`

### Deliverables

- Publish signed stable builds and complete user/technical documentation.
- Publish the validated setting catalog and known-hardware matrix.
- Provide troubleshooting, recovery, and responsible issue templates.
- Enable public repository features only after release artifacts and policies are ready.

## Post-1.0 candidates

Prioritize these only from measured demand:

1. External bufferbloat report import and correlation.
2. Additional loaded-latency/throughput service integrations.
3. ETW flow analysis and optional PCAP import.
4. Process/game endpoint correlation.
5. Wi-Fi-specific controls and additional NIC validation packs.
6. CLI automation and signed profile exchange.
7. ARM64 and managed-enterprise deployment.

A custom capture driver, permanent service, plugin marketplace, or cloud platform requires a separate architecture and security review.

## Initial validation matrix

| Dimension | Initial coverage |
| --- | --- |
| OS | Windows 10 22H2 x64; currently supported Windows 11 x64 releases |
| Locale | English and Italian minimum |
| Ethernet | Common Intel I219/I225/I226 and Realtek RTL8111/RTL8125-class drivers |
| Other adapters | Inventory first; writable support only after capability and rollback validation |
| Network states | DHCP/static, IPv4/dual-stack, disconnected, VPN present, virtual adapters present |
| Failure states | Access denied, unsupported value, driver rejection, adapter restart failure, target disappears, ICMP blocked, offline system, reboot pending |

The matrix should expand from real test results, not from unverified model-name profiles.
