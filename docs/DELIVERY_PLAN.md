# SockTuner delivery plan — diagnose, localise, fix

Detailed expansion of [ROADMAP](ROADMAP.md) steps 8–10. The roadmap says *what* ships in each
step; this says *how*, and tracks the remaining work as independent workstreams.

## Product goal

Diagnose the whole chain — NIC and driver, LAN, router, access link, ISP, external hops, remote
endpoint — then fix what SockTuner legitimately controls, guide precisely on what it does not,
and be explicit about what nobody local can fix.

## Non-negotiable architecture

**Three layers, no leakage between them.** Each is separately testable and extendable.

| Layer | Namespace | Responsibility | Must never |
| --- | --- | --- | --- |
| Collection | `Services.Collection` | Probes, tests, inventory. Produces facts. | Interpret or judge |
| Diagnosis | `Services.Diagnosis` | Classify problem, locate segment, assign owner. Pure functions over collected facts. | Touch the network or the system |
| Remediation | `Services.Remediation` | Propose and apply reversible actions. | Decide *whether* there is a problem |

Diagnosis takes collected data in and returns findings out. Every classification rule is therefore
testable against a fixture with no network and no host mutation — the property the existing suite
already relies on.

**Every automatic action is logged and reversible.** Remediation emits change requests that flow
through the existing `SettingTransactionService`: snapshot → apply → read-back verify → audit →
exact rollback. No remediation path may write outside that engine.

## Responsibility model

Every finding carries exactly one owner. This is the spine of the product: it decides what the
user is shown and what SockTuner is allowed to do.

| Owner | Meaning | SockTuner behaviour |
| --- | --- | --- |
| `Automatic` | Safe, reversible, no user choice needed | Offer one-click apply (still previewed and audited) |
| `PresetOrManual` | Needs a target or a judgement call | Offer presets (bandwidth / max ping / max jitter, or use-case profile) or manual edit |
| `Router` | Needs router configuration | Give the specific parameter, expected value and reason; apply via OpenWrt SSH when explicitly enabled |
| `OutOfScope` | ISP or infrastructure limit | Diagnose precisely, explain, export evidence. Never attempt a fix |

Router guidance must be actionable: *which* parameter, *what* value, *why*. "Check your QoS
settings" is not acceptable output.

---

## Workstreams

### W1 — Routing diagnosis — **done**

Per-hop quality, not a single traceroute.

- `HopMeasurement`: TTL, address, per-hop statistics, private / CGNAT / public classification.
- `RoutePathDiagnostic`: repeated multi-hop sampling (mtr-style), hop stability across rounds,
  route-change detection.
- Rate-limit awareness: an intermediate hop that deprioritises ICMP shows loss *at that hop* but
  not beyond it. A hop is only called faulty when the degradation **persists downstream**. This is
  the most common false positive in traceroute-based tools, and the rule that prevents it is the
  core of this workstream.

### W2 — Bottleneck localisation — **done**

Where the chain first degrades, not just that it does.

- `NetworkSegment`: LocalNicDriver → Lan → RouterOrAccess → IspAccess → IspCore → ExternalHop →
  RemoteEndpoint.
- `BottleneckLocator` walks the chain outward and returns the first segment where latency, jitter
  or loss steps up beyond threshold, with supporting and contradicting observations.
- Local evidence (NIC error and discard counters, link speed, driver power settings) is folded in,
  so a local driver fault is not misattributed to the ISP.
- **Inconclusive stays a valid result.**

### W3 — Responsibility assignment — **done**

- Owner on every finding, per the table above.
- `ResponsibilityAssigner` maps (segment, problem kind, local capability) → owner.
- The owner is *derived*, never hand-written per finding, so it cannot drift between findings.

### W4 — Probe archive and device coverage — **done**

- `alpha-tester-output/` becomes the tracked capability archive: one file per adapter model, named
  `<vendor>-<model>-<driver>.json`, plus a generated `INDEX.md` showing covered versus missing
  hardware so gaps are visible at a glance.
- Probe output enriched with the structured CIM constraints (valid values, min/max/step, default)
  already used by the tuning path, so archive entries and the live tuning surface share one shape.
- Wi-Fi adapters use the identical structure as Ethernet — no separate Wi-Fi format.

### W5 — Active measurement suite — **done**

- `ThroughputProbe`: multi-stream HTTP against a chosen endpoint, user-initiated only, bounded
  window. A run stopped early keeps its rate and is labelled incomplete rather than discarded.
- `LoadedLatencyProbe`: the same latency probe measured idle and again while one direction is
  saturated, with the load stopped as soon as the measurement window closes.
- `LoadedLatencyAnalyzer`: latency-increase grade (A+ to F), plus loaded jitter and loss increase.
  Bufferbloat is owned by the router — the queue is not on the endpoint — so it is never offered
  as a local change.
- Long-window stability: `NetworkMonitorService` already samples for minutes to hours with bounded
  memory and explicit start and stop; `StabilityAnalyzer` finds the loss bursts and latency spikes
  inside that window, judged against the run's own baseline. Two consecutive bad samples make an
  episode; one is noise. A truncated window says so in its verdict.
- Local saturation: `LinkUtilization` turns adapter counter deltas into a rate, and a baseline
  taken while this machine was filling its own link is reported as such instead of being graded.
  The NIC link speed is a poor capacity proxy, so saturation is claimed against a *measured*
  capacity where one exists. Per-process attribution needs ETW and stays deferred.

### W6 — Topology and path facts — **done**

- `TopologyAnalyzer`: CGNAT and double NAT from the route, the router-reported WAN address and the
  observed public address — all three optional, and the verdict degrades to `Unknown` rather than
  guessing. CGNAT is `OutOfScope` and stated not to cost latency; double NAT is `Router` with the
  exact fix (bridge mode, or one device stops translating). Two private hops before the first
  public one are only medium confidence: some ISPs number their own access equipment out of
  private space. The router-reported WAN address arrives with W8; the observed public address
  needs an external reflector and is only fetched on explicit request.
- PMTUD black hole: a DF probe that times out is retried fragmentable. If the same packet gets
  through, the path drops oversized packets silently — `PathMtuState.IcmpBlackHole` — and the
  workaround is local and exact even though the cause is not.
- Wi-Fi radio: `WindowsWifiInventory` reads RSSI, band, channel and occupied width through the
  native WLAN API, taking the *cached* scan so the radio is never interrupted. Width comes from the
  beacon's HT/VHT operation elements, so a 40 or 80 MHz neighbour is counted across the spectrum it
  really occupies rather than the channel number it advertises. `WifiRadioAnalyzer` separates weak
  signal (placement), wrong band (the same SSID reachable on 5/6 GHz) and channel congestion
  (`Router`, with an exact 2.4 GHz channel — 1, 6 and 11 are legal everywhere, which is why an
  exact number is only given on that band).

### W7 — Remediation engine

- `RemediationAction`: owner, target segment, the change requests it would emit, expected effect,
  trade-off, and how to verify it worked.
- Automatic tier: only reversible, low-risk, no-choice actions.
- Preset tier: bandwidth / max ping / max jitter targets, plus use-case profiles (competitive
  gaming, streaming and upload, calls and remote work) — these weight *different* objectives; for
  streaming, upload capacity matters more than ping.
- Research corpus integration (table below): every setting the research scripts touch becomes an
  individually gated, evidence-labelled, reversible catalog entry.

### W8 — Router integration

- Non-OpenWrt: specific guidance per finding — parameter, value, reason.
- OpenWrt over SSH: **optional**, key-based authentication only, never stored credentials, and only
  for settings the user explicitly enabled. Read-only inspection first; any write follows the same
  snapshot / verify / rollback contract as local changes. Primary target is SQM/CAKE for
  bufferbloat, which is genuinely router-side and cannot be fixed from the endpoint.

### W9 — Reporting, baseline, watchdog

- Historical baseline and trend ("median ping degraded 20% in two weeks"); the history store and
  comparison service already exist and extend to this.
- Before/after report per applied fix, reusing the existing comparison path.
- Exportable evidence report for ISP escalation (the redacted HTML exporter already exists).
- Background watchdog with threshold alerts, so the user learns *when* a problem started.

---

## Research corpus integration

Everything under `research/` is reference-only and is never executed. Its capabilities map to
SockTuner as follows. Blanket "disable everything" behaviour is deliberately **not** reproduced:
the architecture forbids blanket-disabling bindings, IPv6 and hidden adapters, and several of
these settings are actively harmful on some hardware.

| Research source | Capability | Destination | Notes |
| --- | --- | --- | --- |
| `NetworkDiagnostics.ps1`, `gaming_net_diagnostic.ps1` | ping, jitter, traceroute, adapter facts | W1, W2 | Already largely native |
| `WinMTR` | continuous per-hop loss and latency | W1 | Native ICMP, no bundled binary |
| Bufferbloat projects | idle versus loaded latency | W5, W8 | Fix is router-side SQM |
| `GameNetAnalyzer` | jitter / burst / spike scoring, endpoint discovery | W5, W6 | tshark dependency dropped; native sockets and ETW instead |
| `Auto MTU.bat` | MTU probing | W6 | Native path-MTU already present |
| TCP global tweaks (`netsh int tcp ...`) | autotuning, ECN, timestamps, heuristics, RSS/RSC, initial RTO, templates | W7 | Per-setting catalog entries, evidence-labelled |
| Offload scripts | checksum, LSO, RSC, RSS, USO, VMQ, RDMA | W7 | Already driver-advertised via CIM |
| MMCSS and `Tasks\Games` | throttling index, responsiveness, task priorities | W7 | Two entries shipped; task priorities to add |
| `TcpAckFrequency`, `TcpDelAckTicks`, `TCPNoDelay` | per-interface ACK behaviour | shipped | Experimental, typed confirmation |
| NIC power scripts | power saving, interrupt moderation | shipped | Driver-advertised |
| NetBT / NetBIOS, Teredo / ISATAP / 6to4 | legacy protocol disable | W7 | Individually gated, never blanket |
| AFD, Dnscache, NDIS parameters | socket and resolver buffers | W7 | Needs documentation review before exposure |

## Sequencing

1. ~~**W1–W4** — diagnosis core and probe archive.~~ **Done.**
2. ~~**W5, W6** — active measurement and topology facts.~~ **Done.**
3. **W7** — remediation engine and research-derived catalog growth.
4. **W8** — router guidance, then optional OpenWrt SSH.
5. **W9** — baseline, watchdog, reporting.

Each workstream lands with its own tests and keeps the default suite host-independent.
