# Automatic diagnosis and guided changes

The beginner entry is Dashboard → Start diagnosis. It selects the standard 30-sample profile,
public IPv4 reference 1.1.1.1 and TCP 443. It reads inventory, counters, cached Wi-Fi information,
driver packages and MSI/affinity configuration. It sends bounded small ICMP probes (including
route and path-MTU discovery) and one TCP connection. It neither enables bandwidth load nor
elevated ETW capture. Gaming diagnostics retains custom targets and explicit DPC/ISR opt-in.

`RouteGatewayResolver` asks Windows routing for the local address selected for the target and
maps it to exactly one inventoried interface. It never guesses the first physical interface.
An ambiguous or changed route disables adapter recommendations; a VPN selection does not claim
to identify its underlying Ethernet/Wi-Fi hardware. Probes still follow Windows routing, which
can change during a run; the app does not pin or alter routes.

The dashboard and HTML/JSON report share observations, confidence, hypotheses and next steps.
Health checks describe configuration and driver age, not proof that an update exists or a tweak
will help. A public reference cannot establish the behavior of a game's server. ICMP silence and
an intermediate slow hop cannot establish ISP responsibility. Repeat over Ethernet, at comparable
times, against an alternate endpoint/network, and send the full report to the provider when needed.
Full export asks for confirmation because it includes addresses and interface identity; redacted
export removes the new interface and health details as well as existing network details.

## Actions

Automatic recommendations use the current diagnosis and its Wi-Fi observations. They do not fold
older throughput runs or a use-case profile into a purported diagnosis. Manual recommendations can
still use explicit throughput evidence. No game preset is selected automatically.

Each checkbox corresponds to one change, starts unchecked, and carries its effect, cost and
verification. Reviewing chosen actions starts a fresh proposal set; the existing preview,
confirmation, live-change consent, elevation, drift checks, audit and exact-value rollback remain.
Gateway jitter plus enabled EEE on a characterised wired adapter can justify a **low-confidence
experiment**, not a causal finding. Generic local errors, DNS errors, Wi-Fi issues or provider
hypotheses cannot trigger arbitrary EEE/flow-control tuning. MTU changes require an actual
black-hole measurement and a characterised adapter.

The embedded per-driver hardware archive is a write allowlist, not a performance guarantee.
NIC writes require physical model, driver provider/version/component identity, characterised
property, identical enum labels/values or numeric range/step, default, optionality and registry
type. Live inventory is re-read inside the elevated worker. Unknown models, versions, changed
constraints, unbounded strings and old keyword-only reports stay read-only. Adapter power and
interface-property writes are also guarded by the live resolver. Report compatibility from the
dashboard or Preferences to supply missing evidence. Recovery can also be refused if hardware or
driver compatibility changes after applying; it does not override the new driver's constraints.

MSI and affinity are reported as registry configuration, not measured delivery. When the registry
omits the NIC PnP ID, a read-only
[MSFT_NetAdapter GUID/PnP mapping](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/netadaptercimprov/msft-netadapter)
identifies the selected network device, refusing ambiguous mappings. Affinity changes remain in the
existing explicit ETW workflow, gated by repeated evidence, device mapping and CPU topology.
Neither is changed by the default diagnosis. Driver package cleanup remains a separate confirmed
host action with no automatic undo, and is never added to the reversible proposal set.

## Before/after

Successful tuning apply captures the current diagnosis as baseline. Failed writes do not capture a
new baseline. Re-run restores the exact profile, game/payload and optional port and asks the user
to restore external load/location conditions. Comparisons check requested and resolved endpoints,
gateway, interface, local address, link speed and Wi-Fi association. Both runs need at least ten
endpoint replies and the after-run must confirm external conditions. Legacy context-free reports
require a new baseline. Outcomes are improved, worse, no demonstrated difference, mixed trade-off
or insufficient/not comparable. Saved Wi-Fi history deliberately removes access-point identity and
cannot certify the same association; use the in-memory verification pair or collect a new baseline.
Noise floors use both runs' jitter and ICMP resolution. One pair is
indicative: repeat before retaining a costly change. Undo remains in the dashboard/audit history.

## Verification without changing the host

Run `dotnet test SockTuner.sln -c Release`. Tests use archived hardware records and in-memory stores
to exercise changed model/driver/constraints, unverified worker rejection, evidence-to-action gates,
route selection, DNS/MTU separation, comparison sufficiency, mixed results and export redaction.
The separately gated live tests do not establish apply/restart/rollback behavior on real hardware.
Do not run a tweak or driver cleanup on a development PC to test this path. Hardware qualification
still needs an explicitly reserved lab session and its own recovery checks.

### VM validation — 2026-11-06

Validated on the shared `NeuroTune-W11` Hyper-V guest (Windows 11 Pro, build 26200), with Secure Boot,
TPM and VBS enabled. The VM was checked for active users/workloads and reserved before testing.
The self-contained Release build was run with a guest administrator token. The harness exchanged
typed requests over a named pipe with the actual `SockTuner.exe --elevated-worker` process, using
its production resolver, backend stores and audit path.

Observed results:

- The uncharacterised Hyper-V NIC exposed 25 advanced properties, all blocked for writes. A real
  worker request to change one was rejected and its current value remained unchanged; adapter
  power management was also refused by the hardware gate.
- A temporary QoS policy for a nonexistent canary application was created and read back. A
  request with a stale expected value was rejected without changing it. Rollback restored the
  original absent policy, and both successful operations appeared in the real audit trail.
- Two real 30-sample runs used the same public target, profile, route-selected interface and
  conditions, before the canary and after its restoration. Median RTT was 9 ms in both; P95 was
  10.0 vs 10.55 ms. The analyzer returned **NoMeasurableChange**. This is validation of the
  comparison path, not evidence that the canary changes network performance.
- The test found an existing worker dispatch bug: dedicated QoS/DNS/CIM settings were incorrectly
  checked against the static DWORD catalog. The catalog gate now applies only to `SettingDefinition`;
  dedicated specifications retain their own address/value checks and live compatibility gates.
  New regression tests reproduce the original refusal and verify apply/rollback for those backends.
- The complete suite passed **981/981**, including the explicitly enabled read-only Windows inventory
  tests. Administrative rights were needed for this system's TCP connection/template query. Driver
  cleanup correctly remains unavailable when Windows returns incomplete device-to-driver bindings.

Local raw evidence and the VM-only harness are in `.codex/vm-validation-20261106/`. The tested
`SockTuner.dll` SHA-256 is `1979F97E492580D6305054796DA6CDC2B06AACED2FB96C8B56274E3E86A0C240`.
The VM is restored to its sole `Clean-NeuroTune-Alpha2` checkpoint after the run; no permanent
checkpoint is added. No host tuning, driver cleanup, physical-NIC write/restart, or desktop UAC
interaction is exercised by this validation. Those remain separate hardware/UI qualification work.
