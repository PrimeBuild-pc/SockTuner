# Zenit NDIS property candidates + latency-tweak annotations (research input)

> **Status: candidate list, not a catalog.** Untracked working note, companion to
> [JACKPOTS_ZENIT_4.0_DELTA.md](JACKPOTS_ZENIT_4.0_DELTA.md) /
> [JACKPOTS_ZENIT_REFERENCE.md](JACKPOTS_ZENIT_REFERENCE.md).
>
> SockTuner's NIC list is **hardware-verified**: `WindowsNdisInventory` and
> `WindowsAdapterCapabilityInventory` enumerate only what the driver actually advertises.
> [`NicKeywordCatalog`](../src/SockTuner/Services/NicKeywordCatalog.cs) then *annotates* that list —
> it never extends it. Each characterised keyword carries a tuning area, a `ChangeRisk`, and the
> trade-off in plain words; a keyword it has not characterised is reported as **high risk**, not as
> safe. `AdapterSettingCapability.Evidence` derives the level from the `*` prefix: standardized →
> driver-advertised, vendor → experimental.
>
> Characterisation can withhold a write but never authorise one. The seven §C keywords below are
> marked `Rejected`, which resolves to evidence level `Blocked` and makes
> `AdapterSettingCapability.Validate` refuse every value the driver offers for them — enforced
> inside the elevated worker, so a hand-built plan cannot bypass it. They stay visible in the
> read-only inventory with the reason attached.
>
> The ~90 properties below are the *superset the Zenit tool writes blindly by
> name*. They are kept here only as a
> **watch-list**: if a future probe report shows one on real hardware, promote it into the
> corpus with a real range/enum; until then it is a name to recognise, never a value to apply.
> **The Zenit "forced value" columns are the tool's opinion, unverified — do not import them.**

## How to read the "Class" column

The Microsoft NDIS convention is the cheap, reliable triage axis, and it maps 1:1 onto
SockTuner's existing risk model:

- **`*` (standardized)** — a Microsoft-defined [standardized keyword]; the driver publishes an
  `Ndi\params\<Keyword>` subkey with a `default`/`enum` the inventory can read. These are the
  legitimate "driver-advertised" candidates. Safe to expose *when advertised*, at the range the
  driver gives — never at a value we invented.
- **`vendor`** — no `*` prefix: a private, vendor-specific keyword. Sometimes advertised (has a
  `ParamDesc`/`enum`), often just a raw registry value with no metadata and no public datasheet.
  These are exactly the "permitted but uncharacterised → high risk" class SockTuner already
  flags. Several here are Realtek/Intel internals with no documented meaning.

---

## A. Standardized (`*`) keywords — legitimate candidates when the driver advertises them

Cross-check each against `NicKeywordCatalog`; add the ones a probe report confirms.

### Offloads (checksum / LSO / USO / RSC / IPsec / encap)
| Keyword | Zenit value | Note |
|---|---|---|
| `*IPChecksumOffloadIPv4` | 3 (Rx+Tx) | Standard checksum offload; 0=off,1=Tx,2=Rx,3=both. Enabling is normal, not a latency win per se. |
| `*TCPChecksumOffloadIPv4/IPv6` | 3 | " |
| `*UDPChecksumOffloadIPv4/IPv6` | 3 | " |
| `*TCPUDPChecksumOffloadIPv4/IPv6` | 3 | Combined variant on some drivers. |
| `*LsoV1IPv4`, `*LsoV2IPv4`, `*LsoV2IPv6` | 1 | Large Send Offload. Throughput feature; can *add* latency for small packets. |
| `*UsoIPv4`, `*UsoIPv6` | 1 | UDP Send Offload. |
| `*UdpRsc` | 1 | UDP Receive Segment Coalescing. Coalescing *raises* latency — note the contradiction with `*PacketCoalescing=0` below. |
| `*RscIPv4`, `*RscIPv6` | 1 | Receive Segment Coalescing. Same latency caveat; many low-latency guides *disable* RSC. Zenit enables it — treat as opinionated. |
| `*IPsecOffloadV1IPv4`, `*IPsecOffloadV2`, `*IPsecOffloadV2IPv4` | 3 | IPsec task offload. |
| `*EncapsulatedPacketTaskOffload`(+`Nvgre`,`Vxlan`) | 1 | NVGRE/VXLAN offload — datacenter feature, irrelevant to a gaming desktop. |
| `*QoSOffload` | 1 | 802.1p/DCB offload. |
| `*HeaderDataSplit` | 1 | RX header/data split. |

### Interrupt moderation / RSS / polling
| Keyword | Zenit value | Note |
|---|---|---|
| `*InterruptModeration` | 1 | **The core latency knob.** Standardized enum (0/1). SockTuner should expose this as advertised. Note Zenit sets it *on* (=1) while also poking the hardware ITR to 0 via MMIO — inconsistent. |
| `*Rss` / `*RSS` | 1 | Receive Side Scaling on/off. Standardized. |
| `*RSSProfile` | 4 | RSS load-balancing profile (ClosestProcessor etc.). |
| `*RssBaseProcNumber` | 2 | First CPU for RSS queues. Machine-specific — a fixed 2 is a guess. |
| `*NumRssQueues` | 2 | RSS queue count. Driver-advertised range varies. |
| `*MaxRssProcessors` | 1 | Cap on RSS CPUs. |
| `*NdisPoll` | 1 | NDIS polling mode (Win11 NDIS 6.85+). See **Unsafe** re: pairing with `ThreadPoll`. |
| `*PacketDirect` | 1 | PacketDirect provider — niche, high-throughput path. |
| `*PacketCoalescing` | 0 | Disables RX coalescing (a real latency lever). Contradicts `*UdpRsc/*Rsc=1` above. |

### Buffers / MTU
| Keyword | Zenit value | Note |
|---|---|---|
| `*ReceiveBuffers` | 4096 | RX descriptor ring. Driver-advertised max varies by NIC; 4096 may exceed advertised range → clamp. |
| `*TransmitBuffers` | 4096 | TX descriptor ring. Same clamp caveat. |
| `*JumboPacket` | 1510 | Jumbo frame size. 1510 ≈ "off/standard". |

### Power management (all forced off)
| Keyword | Zenit value | Note |
|---|---|---|
| `*EEE` | 0 | Energy-Efficient Ethernet off. Legit low-latency lever, standardized. |
| `*SelectiveSuspend` | 0 | USB/idle suspend off. |
| `*NicAutoPowerSaver` | 0 | Auto power save off. |
| `*DeviceSleepOnDisconnect` | 0 | |
| `*EnableDynamicPowerGating` | 0 | |
| `*SSIdleTimeout`, `*SSIdleTimeoutScreenOff` | 1 | Selective-suspend idle timers. |
| `*WakeOnMagicPacket`, `*WakeOnPattern` | 0 | WoL off. Fine, unrelated to latency. |
| `*IdleRestriction` | 1 | |

### Virtualization (off — irrelevant on a desktop)
| Keyword | Zenit value | Note |
|---|---|---|
| `*VMQ` | 0 | Virtual Machine Queues. |
| `*SRIOV` | 0 | SR-IOV. |
| `*NetworkDirect` | 0 | RDMA/NetworkDirect. |
| `*FlowControl` | 0 | See **Unsafe** — 802.3x pause frames. |

---

## B. Vendor / undocumented keywords — recognise, do not import

No `*` prefix. Expose only if advertised with metadata; otherwise these are the
"high-risk, uncharacterised" bucket. Grouped, Zenit values shown as the tool's guess.

**Duplicate-of-standardized / driver internal moderation:**
`ITR=200`, `RxIntModeration=0`, `TxIntModeration=0`, `RssV2=1`,
`RecvCompletionMethod=4`, `SendCompletionMethod=2`, `AsyncReceiveIndicate=2`,
`ThreadedDpcEnable=0`, `TxThreadedDpcEnable=0`, `ThreadPoll=200000` *(see Unsafe)*.

**Power / EEE / PHY (vendor variants of the `*` power knobs):**
`AdvancedEEE=0`, `EEEPlus=0`, `EEELinkAdvertisement=0`, `GigaLite=0`,
`EnableGreenEthernet=0`, `PowerSavingMode=0`, `AutoPowerSaveModeEnabled=0`,
`EnableSavePowerNow=0`, `EnablePowerManagement=0`, `EnablePME=0`, `PowerDownPll=0`,
`ReduceSpeedOnPowerDown=0`, `EnablePHYFlexibleSpeed=0`, `EnableD0PHYFlexibleSpeed=0`,
`EnableD3ColdInS0=0`, `DisableDelayedPowerUp=1`, `EnablePHYWakeUp=0`,
`EnableModernStandby=0`, `EnableDisconnectedStandby=0`, `EnableWakeOnManagmentOnTCO=0`,
`WakeOn=0`, `WakeOnLink=0`, `WakeOnFastStartup=0`, `WakeFromS5=0`, `S5WakeOnLan=0`,
`S0MgcPkt=0`, `ForceWakeFromMagicPacketOnModernStandby=0`, `WolShutdownLinkSpeed=2`.

**ASPM / LTR / OBFF (PCIe link-power; also done via config space in §3/§4 of the 3.0 notes):**
`ASPM=0`, `EnableAspm=0`, `CLKREQ=0`, `DynamicLTR=0`, `ForceLtrValue=0`,
`LatencyToleranceReporting=0`, `LTROBFF=0`, `OBFFEnabled=0`.

**Realtek/Intel private internals — no public datasheet meaning:**
`HwOption=12582912` (DWord), `HwOptionV2=4` (DWord), `HwOptionV3=262144` (DWord),
`PnPCapabilities=24` (DWord) *(see Unsafe)*, `I218DisablePLLShut=1`,
`I218DisablePLLShutGiga=1`, `I219DisableK1Off=1`, `DisableIntelRST=1`,
`ForceHostExitUlp=1`, `ULPMode=0`, `SipsEnabled=0`, `FecMode=0`,
`LinkNegotiationProcess=0`, `WaitAutoNegComplete=0`, `WaitForValidPhyIDRead=0`,
`DisablePhyReset=1` *(see Unsafe)*.

**Misc behavioural:**
`DisableLLDP=1`, `StoreBadPackets=0`, `SleepWhileWaiting=0`, `EnableETW=0`,
`EnableCoalesce=0`, `DMACoalescing=0`, `CongestionMonitoringEnable=0`,
`VMQSupported=0`, `TeredoOffload=1`, `SSIdleTimeoutMS=1`,
`DropHighlyFragmentedPacket=1` *(see Unsafe)*.

---

## C. Latency tweaks flagged **UNSAFE** (recognise the symptom, reject the method)

These are the ones I'd *not* let into SockTuner as-is — either undocumented, high blast
radius, or actively harmful. Ordered roughly by severity.

1. **Driver-blocklist disable** — `CI\Config\VulnerableDriverBlocklistEnable=0`. BYOVD enabler,
   nothing to do with latency. Already the §0 hard-no. **Never.**
2. **Below-driver MMIO pokes** — Intel ITR `+0x00C4` / EEE `+0x0E00`, Realtek INT_MIT `+0x00EC`
   / EEE `+0x00D4`, HD-Audio INTCTL `+0x08`. Undocumented offsets written behind the live
   driver. The *documented* equivalent (`*InterruptModeration`, `*EEE`) already covers the goal.
3. **MSR pokes** — `IA32_ENERGY_PERF_BIAS` (0x1B0), AMD `0xC0010296` (undocumented DF C-state
   lock). Out of networking scope + BSoD risk. The Zenit RW.exe path itself refuses MSR writes.
4. **`HwOption`/`HwOptionV2`/`HwOptionV3` (Realtek bitmasks)** — magic DWords with no public
   meaning; wrong silicon revision = undefined behaviour. Guessed by the tool.
5. **`ThreadPoll=200000` + `*NdisPoll=1`** — busy/spin-poll the NIC. Can pin a CPU core at 100%,
   raising power/heat/DPC on *other* devices; "lower latency" here trades a whole core. If ever
   offered, must be explicit, measured, and default-off.
6. **`DisablePhyReset=1`** — suppresses PHY reset; can wedge link renegotiation after a
   cable/speed change. Recovery may need a driver reinstall.
7. **`PnPCapabilities=24`** — sets the Device-Manager "allow the computer to turn off this
   device" / WoL bits in one opaque DWord. Documented-ish but coarse; prefer the individual
   `*`-power keywords so the change is legible and reversible per-setting.
8. **`*FlowControl=0`** — disabling 802.3x pause frames can cause RX drops on a congested or
   slower link (a real regression, not just "no gain"). Situational, not a blanket win.
9. **`DropHighlyFragmentedPacket=1`** — silently drops legitimate fragmented traffic. Correctness
   risk, not a latency setting.
10. **`*Rsc*/*UdpRsc=1` while chasing latency** — coalescing *adds* receive latency; enabling it
    contradicts the stated goal. Not dangerous, but wrong-signed for a "low latency" preset —
    flag as opinionated/incoherent rather than safe.
11. **Persistent MTU / `DisabledComponents=0x20` (IPv6 de-prioritise) / interface-metric=1** —
    documented but *behavioural*, system-wide, and sticky (`store=persistent`, survives reboot).
    Not unsafe per se, but must be snapshot + reversible, never a silent side effect.

### Safe-ish latency levers worth keeping on the radar (documented, low blast radius)
`*InterruptModeration`, `*EEE`, `*FlowControl` *(situational)*, `*ReceiveBuffers`/`*TransmitBuffers`
*(clamp to advertised)*, `*Rss`+profile *(machine-specific tuning)*, MMCSS
`NetworkThrottlingIndex`/`SystemResponsiveness` *(already catalogued)*, and the TCP/IP knobs in
§7 of the 4.0 delta — all as **driver-advertised / documented, snapshot-and-rollback** entries,
which is what the existing catalog already does.
