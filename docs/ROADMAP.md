# SockTuner Delivery Roadmap

SockTuner is built in small, testable increments. A step is complete only when its exit criteria pass; unfinished work is not hidden behind a polished screen.

## Priorities

| Priority | Meaning |
| --- | --- |
| **P0** | Required for a trustworthy usable core; blocks writable features |
| **P1** | Required for private beta and controlled tuning |
| **P2** | Release hardening or valuable follow-up work |

## Safety gates

These apply to every step:

- Production uses native Windows/.NET APIs first and has no PowerShell runtime dependency.
- Material under `research/` is reference-only: never executed, bundled, or copied without validation and license review.
- Read-only inventory and diagnostics run unelevated where Windows permits.
- Every writable setting needs an allowlisted type, exact target, validation, snapshot, read-back verification, and exact rollback.
- Real writes are tested only in an explicitly prepared disposable VM. Default tests use fakes.
- The UI remains write-locked until the complete low-risk transaction path passes its VM gate.

## Execution order

### Step 1 — Complete shell and inventory integrity (`0.1`) — **P0, complete**

**Goal:** finish the dark Windows desktop shell and make adapter/driver discovery reliable enough to support later diagnostics and plans.

Deliverables:

- Complete dark Metro/Aero2 treatment for the window, title bar, navigation, cards, inputs, grids, selection, focus, disabled states, scrollbars, and dialogs.
- Embed the SockTuner application icon in the window and executable.
- Inventory OS, CPU, adapters, stable interface GUIDs, addresses, gateways, DNS, link state/speed, driver identity/version, and driver-advertised NDIS properties.
- Distinguish supported, unsupported, unavailable, and partial inventory instead of reporting an empty result as success.
- Add refresh, adapter/property filtering, copy, and visible per-surface errors.
- Keep all discovery read-only and localization-resilient.

Exit criteria:

- No unintended light surfaces remain in the dark theme at 100%, 125%, 150%, or 200% DPI.
- Keyboard focus and normal text meet contrast requirements.
- Physical, virtual, disconnected, and unsupported adapters are identified without hard-coded adapter names.
- Supported Intel/Realtek adapters expose raw NDIS keywords, current values, defaults, and driver-advertised ranges/enums.
- Build, format, tests, and supervisor gate pass.

### Step 2 — Complete read-only Windows network inventory (`0.2`) — **P0, complete**

**Goal:** show the actual Windows network state before adding more tests or any writes.

Deliverables:

- Active routes, default route, interface metrics, IPv4/IPv6 MTU, network profiles, and DNS configuration.
- TCP global/template state, RSS/RSC/offload state, bindings, QoS policy inventory, Winsock providers, and relevant NIC/link counters.
- Search/filter across inventory and copy selected values.
- Versioned JSON snapshot export and redacted support snapshot.
- Structured local application log with bounded retention and an **Export logs** action.

Exit criteria:

- Exported data matches the visible snapshot and carries schema/tool versions and capture time.
- Sensitive identifiers are explicitly included or redacted by the user, never silently leaked.
- Errors identify the failed surface and do not discard successful inventory from other surfaces.
- English and Italian Windows validation finds no dependency on localized property labels.

### Step 3 — Diagnostic and monitoring workbench (`0.3`) — **P0, complete**

**Goal:** turn the current short diagnostic into a complete, observable test workflow.

Deliverables:

- Configurable quick, standard, and extended test profiles.
- Concurrent timestamped probes to gateway, neutral reference, custom/game endpoint, and first reachable ISP boundary when discoverable.
- Sent/received/lost, minimum, median, average, P95, P99, maximum, named jitter, and spike timeline.
- DNS timing, optional TCP connection timing, repeated route sampling, path-MTU discovery, and NIC counter deltas.
- Continuous read-only monitoring with explicit start/stop, duration, interval, cancellation, and bounded in-memory samples.
- Clear states for timeout, blocked/deprioritized ICMP, DNS failure, route failure, refusal, cancellation, and local API failure.

Exit criteria:

- Deterministic fixtures test every calculation and classification.
- No network test starts automatically or generates uncontrolled traffic.
- Intermediate-hop ICMP behavior is not mislabeled as end-to-end loss.
- Monitoring can run and stop repeatedly without leaking tasks, sockets, or memory.

### Step 4 — Reports, history, and comparison (`0.4`) — **P0, complete**

**Goal:** make observations reproducible and useful for before/after analysis or support escalation.

Deliverables:

- Versioned JSON and self-contained offline HTML diagnostic reports.
- Raw sample export, run metadata, targets, intervals, route samples, load conditions, and calculation method.
- Local run history with bounded retention, delete, redaction, and export.
- Baseline/post-change comparison using identical test parameters.
- Multi-run metric trends and a redacted ISP/support evidence report.

Exit criteria:

- Reports render without a CDN or internet connection.
- Imported SockTuner reports are schema-validated and never execute content.
- “Improvement” is shown only for valid comparable runs.

### Step 5 — Transaction engine and write preview (`0.5`) — **P0, complete**

**Goal:** connect the existing typed transaction core to a safe plan UI without enabling uncontrolled writes.

Deliverables:

- Change cart with selected adapter, current value, proposed value, source, evidence, risk, trade-off, and restart requirement.
- Dry-run diff, stale-plan rejection, deterministic ordering, exact snapshot, read-back verification, audit history, and rollback preview.
- Same signed executable elevated-worker mode accepting only versioned typed operations.
- Strict operation and registry-address allowlists; no executable paths, scripts, shell fragments, or arbitrary registry paths in plans.
- In-memory failure injection for partial apply, verification failure, cancellation boundaries, external drift, and rollback failure.

Exit criteria:

- The UI cannot bypass the transaction service or create an unlisted operation.
- Default and CI tests cannot mutate the host.
- The complete fake-backed path passes:

```text
read → plan → snapshot → apply → verify → rollback → verify original state
```

- The production write backend remains unavailable until Step 6 VM validation passes.

### Step 6 — First writable settings in a disposable VM (`0.6`) — **P1, complete**

**Goal:** prove a very small end-to-end writable surface before expanding coverage.

Deliverables:

- Select a few documented, exactly reversible, low-disruption settings.
- Re-detect machine, adapter, driver, capability, and current value after UAC.
- Apply, verify, persist audit state, roll back, and verify the original value.
- Recovery behavior for interruption, stale values, access denied, target disappearance, and pending restart.
- Explicit VM-only operator gate and documented recovery path.

Exit criteria:

- Every supported setting passes repeated read/apply/read/rollback/read testing on disposable Windows 10/11 VMs.
- External drift is refused rather than overwritten.
- No adapter restart, reboot, or connectivity interruption occurs without explicit preview and confirmation.

### Step 7 — NIC and driver controls (`0.7`) — **P1, current (7a complete, 7b in alpha)**

**Goal:** expose only capabilities actually advertised by the selected driver.

Step 7 is split into two gates:

- **7a — capability collection (no disposable hardware required):** the read-only `--probe` mode captures a redacted inventory from collaborator PCs with real Intel/Realtek NICs. Personal data (machine name, IPs, MAC device octets, user-assigned values) is masked; hardware identity (driver, PNP ID, NDIS keywords, defaults, ranges/enums) is preserved. Probe reports seed the capability matrix and fake-platform fixtures.
- **7b — write unlock (alpha):** NIC/driver writes are enabled behind versioned in-app consent, UAC elevation, driver-advertised validation re-read inside the elevated worker, and a typed confirmation for high-risk or experimental changes. The static per-setting allowlist is retained for registry-backed catalog entries only; for NIC properties the driver's own advertised constraints are the allowlist, so an unsupported keyword or value cannot be planned or written. Capability coverage is currently Intel I226-V and Wireless-AC 3168, Realtek RTL8125 and 8852CE, and MediaTek MT7925 — keywords outside that corpus are exposed but reported as high risk and uncharacterised.

Deliverables:

- RSS, moderation, flow control, buffers/queues, supported offloads, jumbo frames, EEE, power, wake, and link controls.
- Driver enum/range validation and absent/unsupported-state handling.
- A refusal list for keywords that are unsafe at any value (`NicKeywordCatalog` `Rejected` → evidence level `Blocked`): the undocumented Realtek `HwOption*` bitmasks, `ThreadPoll`, `DisablePhyReset`, `PnPCapabilities`, and `DropHighlyFragmentedPacket`. They stay visible in the read-only inventory with the reason attached, and `AdapterSettingCapability.Validate` refuses them inside the elevated worker.
- Adapter restart planning, reconnection verification, and exact rollback.
- Transparent **Balanced**, **Low latency**, and **Custom** proposal profiles for validated hardware only.

Exit criteria:

- No missing driver property is created or guessed.
- Profiles remain editable diffs and touch only the selected adapter.
- Intel I219/I225/I226 and Realtek RTL8111/RTL8125-class validation records expected trade-offs and failures.

### Step 8 — TCP/IP, interfaces, QoS, and Winsock (`0.8`) — **P1**

**Goal:** expand controlled writes beyond NIC properties without introducing broad reset behavior.

Deliverables:

- Supported TCP global/template controls, selected interface settings, MTU, metrics, DNS, and QoS policy editing.
- Carefully scoped TCP ACK/Nagle controls with TCP-only and experimental warnings.
- Winsock inspection and separately gated targeted repair workflows.
- Remote-session and connectivity-disruption warnings.

Exit criteria:

- Capability gates use the active Windows surface, not OS-name guesses.
- IPv6, bindings, and hidden adapters are never blanket-disabled.
- Rollback restores exact captured state; broad resets are not presented as rollback.

### Step 9 — Gaming root-cause analysis (`0.9`) — **P1**

**Goal:** correlate inventory, counters, routes, and measurements into evidence-ranked findings.

Deliverables:

- Guided game/region profiles and direct endpoint input.
- Candidate causes across PC/NIC, LAN/Wi-Fi, router/access link, ISP, routing/peering, region, and remote endpoint.
- Supporting and contradicting observations plus confidence and ownership of the fix.
- Controlled loaded-latency testing with an explicitly selected endpoint.
- Local proposals only when SockTuner controls the observed cause; otherwise router/ISP/region guidance.

Exit criteria:

- **Inconclusive** remains a valid result.
- DNS is not blamed for steady in-session RTT without endpoint-selection evidence.
- TCP-only changes are not presented as generic UDP game optimizations.

### Step 10 — Private beta and `1.0` (`0.9.x` → `1.0`) — **P2**

Deliverables:

- Windows 10 22H2 and supported Windows 11 matrix testing at multiple DPI levels and English/Italian locales.
- Accessibility, performance, long-run stability, elevated-worker threat model, recovery drills, and privacy review.
- Signed installer/update path, SBOM, license, vulnerability policy, support documentation, and release checklist.
- Remove private captures, nested repositories, external binaries, archives, and unknown-license material from release inputs.

Exit criteria:

- No critical privilege-boundary, rollback, data-loss, or connectivity-recovery defects.
- All distributed files have known provenance and valid licenses.
- Signed builds install, update, repair, and uninstall cleanly.

## Current queue

The queue defines completion gates. A read-only prerequisite from the next item may land in the same reviewed increment, but no step is marked complete and no writable scope unlocks until every earlier exit criterion passes.

1. **P1 / Step 7b:** broaden real-hardware validation of capability-advertised NIC and driver controls, and grow the characterised-keyword corpus as more probe reports arrive.

The Group Policy QoS Packet Scheduler limits are catalogued: reservable bandwidth and the outstanding-packet limit, both verified present in `pacer.sys`. The third policy in that node, timer resolution, is recorded as inert instead — `pacer.sys` contains neither the string nor any sign of reading it, while it does contain the other two.

Parity with SG TCP Optimizer is deliberate rather than total. Most of its surface is already covered by the CIM TCP provider and the NIC keyword path; the rest is declined on the record in `InertSettingCatalog`, which now cites the System32 evidence behind each decline — including that the widely copied `HostPriority` spelling appears nowhere in `mswsock.dll`, whose actual value is `HostsPriority`.

A DNS resolver benchmark queries each candidate directly over UDP so resolvers this machine does not use can still be measured, ranks them by median lookup time, and refuses to call a sub-5 ms difference an improvement. The fastest one can be applied to a chosen adapter through `SetInterfaceDnsSettings`, as a typed setting the transaction engine snapshots, verifies and rolls back like any other; restoring DHCP-assigned resolvers is an explicit state rather than a deletion. An opt-in tick applies a winner automatically, using the same noise floor as the verdict so it cannot churn on run-to-run variation.

Capture reports produced by an external analyzer can be imported. The value of one is the single thing SockTuner cannot work out by itself — which server the game actually talked to, and how its packets were spaced — so the import supplies the endpoint and SockTuner then measures it directly rather than trusting the numbers. Flow quality is judged against the game's own tick rate rather than a fixed threshold, since the same jitter figure means different things on a 20 ms and a 50 ms tick. The file is treated as untrusted input: size-limited, parsed defensively, never executed and never turned into a path.

The health check also reports running kernel-level anti-cheat services, so an adapter or driver change is not applied mid-session and later mistaken for something else.

Device interrupt affinity is exposed as its own typed setting: which processors service a device's interrupts, across all device classes rather than only network ones, since the device competing with the NIC for a core is usually not another NIC. The mechanism is the documented policy under each device's own `Enum` key, and `pci.sys` contains every value name it uses. Automatic placement is deliberately not offered — there is no generally correct one — so the app shows what Windows currently has, validates a chosen placement against the processors that exist, warns about CPU 0 and about stacking devices on one core, and makes restoring the Windows default a first-class action.

The research corpus under `research/` has been audited against the product rather than imported: its NDIS keyword work, `netsh int tcp` settings, MTU probing, Nagle and power-saving tweaks are all already covered by the tuning plan, the CIM TCP provider and the diagnostics. What it turned up that was genuinely missing is the port-reuse range the provider advertises and the app did not expose, and the interrupt affinity work above. A short list of external references — the Microsoft documentation behind each surface, and the tools SockTuner does not replace — is linked from Preferences; nothing is bundled or downloaded.

A health check runs on every inventory refresh: it reads the state Windows already reported and names what it can see — a driver years out of date, a gigabit-class adapter negotiated at 100 Mbit/s, link power saving on the adapter carrying the traffic, local and public resolvers mixed on one interface, wired and wireless both holding a gateway, capture or virtualisation filters bound to the datapath, and offloads someone disabled and forgot. Each finding carries its evidence, what to do, and the tab that acts on it, so the first screen routes into the rest of the app instead of being a dead end. It measures nothing and changes nothing.

Every service in the codebase is now reachable from the UI. Measurement (throughput, loaded latency), diagnosis (bufferbloat grade, bottleneck location, NAT topology, stability episodes, Wi-Fi radio, baseline drift, live watchdog) and remediation (per-finding actions, use-case profiles, receive-window advice, router guidance) each have a surface, and remediation hands proposed changes to the tuning plan rather than applying anything itself. The header badge reports the real write state — `INVENTORY ONLY` until the change consent is accepted, `CHANGES ARMED` after — instead of the permanent `READ-ONLY PREVIEW` label, which stopped being true once Step 7b unlocked the transaction path.

Writable surfaces: the twelve registry-backed catalog entries (three experimental TCP-ACK behind typed confirmation, four MMCSS, interface MTU, NetBIOS over TCP/IP, TIME_WAIT delay, two DNS cache caps) and every property the selected driver advertises, minus the keywords the catalog rejects outright. Nothing is written that the driver does not currently advertise for that adapter.

Every catalog entry carries an `EvidenceNote` recording what actually backs its evidence level — the Microsoft documentation, or the Windows component observed to consume the value (`tcpipreg.sys` for the TCP-ACK and TIME_WAIT entries, `dnsrslvr.dll`/`dnsapi.dll` for the DNS caps, `avrt.dll` and `mmcss.sys` for MMCSS). Two entries state plainly that nothing has been verified: `TCPNoDelay` and `TcpDelAckTicks`. A test refuses any entry without a note, and refuses either of those two claiming the Documented level.

## Deferred until measured demand

- ETW process-to-endpoint correlation and optional PCAP import.
- Additional loaded-latency/throughput service integrations.
- Wi-Fi radio/roaming controls and additional NIC validation packs.
- CLI automation, signed profile exchange, ARM64, enterprise deployment.
- A custom capture driver, permanent service, plugin marketplace, or cloud platform; each requires a separate security and architecture review.
