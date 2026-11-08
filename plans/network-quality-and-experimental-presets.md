# Network quality and experimental presets

Requested by Lorenzo, 2026-11-07. Plan first, then implement within the existing preview/apply/audit/rollback workflow.

## 1. Driver-specific power control

- Stop automatically proposing the legacy `PnPCapabilities=24` override from use-case profiles and Interfaces.
- Prefer advertised, validated `*SelectiveSuspend` and `*IdleRestriction` controls. Do not disable wake, ARP/NS sleep offload or overwrite opaque power bitmasks as a side effect.
- Retain the legacy setting backend for old audit rollback; accurately label it experimental instead of claiming universal operational support.
- Reuse the live capability inventory and transaction read-back. Distinguish stored configuration from measured performance.

## 2. Experimental presets

- Keep the Synez candidates AutoDisableGigabit, EnablePME, ULPMode and SipsEnabled as usable templates, explicitly marked untested on this hardware when no archived capture matches.
- Never fabricate a hardware verification entry. Require live driver enumeration and accepted values; reject inverted vendor labels and unsupported settings.
- Make aggressive offload/interrupt tuning a named experimental preset, with an apply warning and the existing exact-state rollback. Retain a conservative ordinary gaming preset.
- Reuse the existing before/after diagnosis and add UDP before/after comparison. Report measured changes and tradeoffs; do not label one comparison a certification.

## 3. Native UDP quality

- Add a bounded client and a .NET responder with no new third-party dependencies. The responder can run on the user's Windows development VM or another selected host.
- Use authenticated, bounded datagrams, unique sessions, sequence numbers and a retried final receiver summary. Ignore foreign, malformed or unauthenticated packets. No automatic firewall/router changes in the app.
- Default: 30 seconds, 20 packets/second. Report upload/download loss separately, RTT median/P95, RTT jitter, late replies, duplicates, reordering and local scheduling delays.
- A missing responder or missing final summary is inconclusive, not zero loss. RTT is not a one-way latency measurement.
- Add an accessible WPF view with progress, cancellation, results, baseline/repeat comparison, and export without the session secret. Require matching endpoint, options, local interface and explicit confirmation of matching external conditions before comparison.

## 4. Verification

- Unit tests for capability decisions, experimental templates, power/wake separation and rollback-compatible legacy settings.
- Protocol/loopback integration tests for direction-specific loss, authentication, malformed packets, final-summary failure and cancellation.
- Build the app and responder; run relevant and full tests, then inspect the UI using the installed desktop harness.
- Read-only capture of the host Intel I226-V settings; do not tune the host's active adapter.
- Use the Proxmox helpers under `the private homelab workspace` and verify identity `win11-dev`/VMID 211. Transfer/run the responder in a unique test directory, scope any temporary firewall rule to the host IP and remove it and the responder after the test. Do not reset/reinstall/snapshot the shared development VM.
- Run a real host-to-VM UDP session. This validates LAN transport, not ISP packet loss or tuning improvements on physical Intel/Realtek hardware.
- Update graphify after code changes and record actual results and remaining limitations here.

## Status

Implemented steps 1–3. The ordinary gaming preset is now balanced; the existing competitive-gaming ID names the aggressive experimental preset, so its intent and confirmation remain explicit. Experimental provenance survives the editable rows, preview and audit. EnablePME is available as an experimental manual wake control, not silently included in gaming presets.

Verification completed:

- Baseline before edits: 967 passed, 14 pre-existing skips, 981 total.
- Full suite after implementation: 989 passed, 14 pre-existing skips, 1003 total.
- Release build: zero warnings and zero errors. Release UDP/experimental subset: 21 passed.
- Authenticated loopback tests cover upload/download drops, duplicate/reordered/late echoes, a retried final summary, missing summary, wrong key, corruption, bounds and cancellation.
- Native UI exercised through agent-desktop: 600-packet/30-second run, baseline capture, another 600-packet repeat with the expected no-measurable-change verdict, and cancellation on the Release build. During measurement the tuning-plan load/apply controls were verified disabled. No host NIC settings were applied.
- Companion CLI tested as separate Release responder/client processes: 20/20 replies with 1024-byte UDP payloads, zero measured losses and zero scheduling delays. Temporary responder and key removed.
- Read-only host capture: Intel I226-V exposes IdleRestriction, interrupt moderation and IPv4/IPv6 LSO. This confirms advertised controls only, not performance benefits of changing them.
- Graphify updated: 4266 nodes, 12162 edges. No LLM extraction used.

Real LAN verification completed after Lorenzo started win11-dev at the verified VM address. Identity and pinned SSH key were verified. A 20-packet VM self-test and a 20-packet host-to-VM smoke test passed, followed by two complete 600-packet runs from the host (the test host) to UDP port 52347:

| Metric | Run 1 | Run 2 |
| --- | --- | --- |
| Sent / received by responder / echoed to client | 600 / 600 / 600 | 600 / 600 / 600 |
| Upload loss / download loss | 0 / 0 | 0 / 0 |
| Median RTT | 0.566 ms | 0.528 ms |
| P95 RTT | 1.061 ms | 0.745 ms |
| RTT jitter | 0.140 ms | 0.108 ms |
| Late / duplicate / reordered echoes | 0 / 0 / 0 | 0 / 0 / 0 |
| Locally delayed sends | 0 | 0 |

The app's comparison analyzer was run on the two actual reports: no measurable change beyond its heuristic noise floors. No preset or NIC settings were applied between runs, and background activity was not experimentally controlled; this verifies transport and the comparison path, not tuning benefits. See [the saved verification artifact](udp-proxmox-verification.json).

The initial remote launch exposed a test-harness issue: Windows OpenSSH killed a background Start-Process child when the SSH command closed. A separate process-lifetime repro confirmed it. A temporary hidden scheduled task kept the responder alive across SSH commands. Copied build hashes were verified; the inbound firewall rule was restricted to the client IP and the one test port. After testing, the responder process, scheduled task, firewall rule, remote test directory and local shared-key file were removed; the test port was verified released. The development VM was left running. Published network identifiers in the evidence JSON use documentation-only placeholders. No Proxmox configuration, VM reset/snapshot or host NIC tuning was performed.

Performance benefits of experimental presets on additional physical Intel/Realtek hardware and measurements of an Internet path remain separate validation work; a local Proxmox VM does not establish those results.
