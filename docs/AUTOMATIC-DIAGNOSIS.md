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
