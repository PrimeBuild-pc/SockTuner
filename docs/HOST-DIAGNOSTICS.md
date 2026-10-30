# Host diagnostics and optional driver cleanup

Accepted scope for [#32](https://github.com/PrimeBuild-pc/SockTuner/issues/32), revised with the
owner on 2026-10-30. This supersedes the original recommend-only decisions for driver cleanup and
ancillary-device disable. Analysis never applies a change automatically.

## Two independent decisions

### Driver-store cleanup

After a network check, enumerate driver-store packages and **all** signed-device INF bindings.
Group packages by original INF name, provider and device class. Propose unused, strictly older
versions; retain the newest version (including equal-version copies) and **every** version bound
to any device, even when that version is older. Missing bindings or unparseable versions make
cleanup unavailable for the affected inventory/family rather than allowing a guess.

This is optional disk cleanup, not evidence that an old package caused latency. An unused GPU or
storage package is not labelled a latency culprit; in-use versions remain protected regardless of
class. Removing the active driver, upgrading devices, `/uninstall`, and `/force` are out of scope.

Each proposal names the removed and retained packages. The user can dismiss it, open the official
[Driver Store Explorer (RAPR) releases](https://github.com/lostindark/DriverStoreExplorer/releases),
or choose Apply. Apply requires a package-specific confirmation (default No), live-change consent
and elevation. The elevated worker re-enumerates bindings and eligibility immediately before
`pnputil /delete-driver`; Windows also refuses in-use packages without `/uninstall` or `/force`.
Removal has no automatic undo: keep a vendor installer if an older package is needed for recovery.

### Driver activity and latency

Opt-in, elevated ETW capture stays attached to an explicit network/gaming check:

1. Resolve the same endpoint used by that check.
2. Take 12 initial ICMP samples, then enable the kernel session without replacing another tool's session.
3. Collect five approximately one-second windows. Each contains four ICMP samples **while ETW is
   active**, using the same 100 ms interval and 150 ms timeout as the initial samples.
4. Attribute ISR/DPC events to the matching window by timestamp and preserve the `.sys` filename
   when mapping activity back to a device. Resolve routine addresses against the timestamped
   kernel image ranges directly: TraceEvent 3.1.21 does not populate its ISR/DPC code-address index.
   Activity outside the measured windows is excluded.

The busiest total remains visible for inspection. Affinity/disable suggestions require at least
three usable windows and a strict majority of all windows in which that same driver is uniquely
busiest **and** jitter exceeds the initial baseline by more than the one-millisecond resolution of
Windows ICMP RTT measurements. Usable windows require at least three replies, no loss, and sample
timestamps inside the ETW window. A missing/failed baseline, one spike, ties, unknown driver,
missing replies, or sequential measurements do not qualify. The device image must map to exactly
one device; a shared image does not identify one particular device.

This is repeated co-occurrence, **not causal attribution** or a validated hardware-specific
threshold. Remote-network jitter and scheduling can still explain the result. This check does not
generate a synthetic throughput load; repeat under the same real workload and compare before/after.
ISR/DPC totals can span CPUs, so their share of wall time is not overall CPU utilisation.

Affinity reuses the existing reversible write path and topology-aware core selection. Device-disable
Apply is restricted, in both UI and elevated worker, to MEDIA, Bluetooth, Camera, Image and Printer
classes; GPU/display, storage, system/chipset, NIC, input and unknown classes are excluded. The user
must confirm the named device and can re-enable it in Device Manager. No change is automatic.

## Verification

- Unit tests: duplicate grouping, protection of every bound version, missing/ambiguous metadata,
  repeated versus isolated observations, measurement overlap, loss, ICMP resolution and class limits.
- Process regression: an actual WPF worker must read and reject an invalid-duration request within
  ten seconds, without starting a capture or changing the system.
- Packaged executable (elevated, ideally in the clean Windows 11 VM):
  `powershell -NoProfile -File tools/Test-HostDiagnostics.ps1 -ExecutablePath <published-SockTuner.exe>`.
  This collects loopback ETW/ICMP data, verifies that routine addresses resolve to kernel modules,
  and verifies refusal of an invalid package name and a
  System-class device. It never requests an eligible removal or disable. It requires the kernel
  trace session to be free and verifies that driver-store output is unchanged.

The VM is reset to `Clean-NeuroTune-Alpha2` after testing; no new permanent checkpoints are needed.
Actual driver removal, device-disable effects, and affinity performance benefit require dedicated
hardware validation and are not established by a loopback VM test.
