# Reference notes: "Jackpots Zenit 3.0" (Noverse IMOD tool)

> **Provenance and status.** This document condenses technical values observed in a
> third-party tool (`C:\Users\Lorenzo\Downloads\Jackpots Zenit 3.0`, files `IMOD-Test.py`,
> `IMOD_Dashboard.py`, using the bundled `RW.exe` / `chiptool.exe` + WinRing0/InpOut
> kernel drivers). It is kept here purely as a **research input**, per the project's own
> engineering position in [README.md](../README.md#engineering-position): nothing below is
> a verified recommendation. Every value must be checked against official documentation
> (Intel/Realtek/AMD datasheets, Windows NDIS/TCP docs, PCIe spec) and validated on real
> hardware before it becomes a SockTuner feature or catalog entry.
>
> None of the code below should be copied. What's useful is the **list of knobs** and the
> **register/property names**, so nothing gets missed while building
> [`SettingCatalog`](../src/SockTuner/Services/SettingCatalog.cs) and the NDIS/TCP
> inventories.
>
> **Later versions:** [JACKPOTS_ZENIT_4.0_DELTA.md](JACKPOTS_ZENIT_4.0_DELTA.md) (4.0 delta,
> incl. the DPC/ISR measurement idea) · [JACKPOTS_ZENIT_5.0_DELTA.md](JACKPOTS_ZENIT_5.0_DELTA.md)
> (5.0 delta — no new networking knobs) · [JACKPOTS_ZENIT_5.3_DELTA.md](JACKPOTS_ZENIT_5.3_DELTA.md)
> (5.3 delta — the `WinTweakVerifier` placebo-detection method, the one genuinely
> useful item in the corpus).

## 0. Do NOT replicate: security-relevant anti-pattern

`IMOD-Test.py` calls this, unconditionally, on every run, with no UI disclosure:

```python
def apply_driver_blocklist_tweak():
    key = winreg.OpenKey(HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\CI\Config", ...)
    winreg.SetValueEx(key, "VulnerableDriverBlocklistEnable", 0, REG_DWORD, 0)
```

This disables Microsoft's **Vulnerable Driver Blocklist** (part of HVCI/Smart App
Control). It's the standard first step of the BYOVD (Bring Your Own Vulnerable Driver)
pattern used by cheat loaders and malware to get an unsigned/vulnerable kernel driver
(here: WinRing0/InpOut) past Windows' own defenses. It has nothing to do with network or
PCIe latency.

**Action for SockTuner:** never touch `HKLM\SYSTEM\CurrentControlSet\Control\CI\Config`.
If a future low-level driver path is ever considered, it must degrade gracefully when the
blocklist is enabled, never disable it.

## 1. Coverage checklist vs. SockTuner's planned areas

Cross-reference against [PRODUCT_SCOPE.md](PRODUCT_SCOPE.md) /
[ROADMAP.md](ROADMAP.md) "NIC tuning" and "Windows networking" areas — used to make sure
nothing in this tool's surface is missing from the catalog design (not to copy its values):

| Area | Jackpots Zenit does it? | Mechanism | In SockTuner scope already? |
|---|---|---|---|
| NIC advanced NDIS properties (offloads, RSS, interrupt moderation, power) | Yes (~90 properties) | raw registry write under NIC's Class GUID key | Yes — `WindowsNdisInventory` / `SettingCatalog`, driver-advertised only |
| NIC hardware register poke (ITR/EEE) below the driver | Yes, Intel + Realtek specific | direct MMIO write via kernel driver | **Not planned** — see §3, high risk |
| PCIe MRRS / ASPM on GPU, NVMe, root bridge, NIC | Yes | direct PCI config space write via kernel driver | Not currently in scope; Windows exposes no supported API for this |
| USB xHCI interrupt moderation | Yes | direct MMIO write via kernel driver | Not currently in scope |
| CPU MSR (energy perf bias, AMD C-states) | Yes | `wrmsr` via kernel driver | Out of scope (CPU, not networking) |
| HD Audio interrupt throttling | Yes | direct MMIO write via kernel driver | Out of scope (audio, not networking) |
| TCP/IP registry globals (MinRto, TTL, TimeWait) | Yes | registry | Yes — `WindowsTcpSettingInventory` |
| MMCSS `NetworkThrottlingIndex` / `SystemResponsiveness` | Yes | registry | Matches Step 6 MMCSS work already gated in `SettingTransactionService` |
| `netsh` congestion provider / autotuning | Yes | shells out to `netsh` | README states "prefer documented APIs over scripts" — worth a native equivalent (`Set-NetTCPSetting` / IP Helper API) instead of shelling out |
| Disable Windows driver blocklist | Yes | registry | **Explicitly excluded**, see §0 |

## 2. NIC advanced-property registry tweaks (the "Zenot" PowerShell block)

Applied to `HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E972-...}\<NIC subkey>`
(the standard NDIS adapter class), gated only by a driver-description regex match for
`Intel|Realtek|Killer|Aquantia|Broadcom` — i.e. **not** checked against what the driver
actually advertises as supported, which is precisely the gap SockTuner's
"driver-advertised only" design principle (README, Product goals) already avoids.

Grouped by theme (values as observed, for cross-checking against SockTuner's catalog —
not vetted):

**Interrupt moderation / RSS**
`*InterruptModeration=1`, `ITR=200`, `*RSS=1`, `*RSSProfile=4`, `*RssBaseProcNumber=2`,
`*NumRssQueues=2`, `*MaxRssProcessors=1`, `RssV2=1`, `RxIntModeration=0`,
`TxIntModeration=0`, `RecvCompletionMethod=4`, `SendCompletionMethod=2`,
`ThreadedDpcEnable=0`, `TxThreadedDpcEnable=0`, `AsyncReceiveIndicate=2`

**Offloads**
`*LsoV1IPv4=1`, `*LsoV2IPv4=1`, `*LsoV2IPv6=1`, `*UsoIPv4=1`, `*UsoIPv6=1`,
`*UdpRsc=1`, `*RscIPv4=1`, `*RscIPv6=1`, `ForceRscEnabled=1`,
`*TCPChecksumOffloadIPv4=3`, `*TCPChecksumOffloadIPv6=3`, `*UDPChecksumOffloadIPv4=3`,
`*UDPChecksumOffloadIPv6=3`, `*IPChecksumOffloadIPv4=3`, `*TCPUDPChecksumOffloadIPv4=3`,
`*TCPUDPChecksumOffloadIPv6=3`, `*TCPConnectionOffloadIPv4=1`,
`*TCPConnectionOffloadIPv6=1`, `*EncapsulatedPacketTaskOffload=1` (+Nvgre/Vxlan
variants), `*QoSOffload=1`, `TeredoOffload=1`, `HDSplitAlways=1`, `*HeaderDataSplit=1`

**Power management / EEE (all disabled)**
`*EEE=0`, `AdvancedEEE=0`, `EEELinkAdvertisement=0`, `EEEPlus=0`, `GigaLite=0`,
`EnableGreenEthernet=0`, `PowerSavingMode=0`, `*NicAutoPowerSaver=0`,
`AutoPowerSaveModeEnabled=0`, `EnableSavePowerNow=0`, `*DeviceSleepOnDisconnect=0`,
`*EnableDynamicPowerGating=0`, `*SelectiveSuspend=0`, `EnablePowerManagement=0`,
`ReduceSpeedOnPowerDown=0`, `PowerDownPll=0`, `EnablePME=0`, `WakeOn=0`,
`WakeOnFastStartup=0`, `WakeOnLink=0`, `S0MgcPkt=0`, `S5WakeOnLan=0`,
`WakeFromS5=0`, `*WakeOnMagicPacket=0`, `*WakeOnPattern=0`,
`ForceWakeFromMagicPacketOnModernStandby=0`

**ASPM / link power / latency tolerance**
`ASPM=0`, `EnableAspm=0`, `CLKREQ=0`, `LTROBFF=0`, `OBFFEnabled=0`, `DynamicLTR=0`,
`LatencyToleranceReporting=0`, `ForceLtrValue=0`, `EnableD0PHYFlexibleSpeed=0`,
`EnableD3ColdInS0=0`, `EnableModernStandby=0`, `EnableDisconnectedStandby=0`

**Vendor/model-specific (Intel I218/I219 only — will no-op or error elsewhere)**
`I218DisablePLLShut=1`, `I218DisablePLLShutGiga=1`, `I219DisableK1Off=1`

**Misc / buffers / driver internals**
`*ReceiveBuffers=4096`, `*TransmitBuffers=4096`, `*JumboPacket=1510`,
`*FlowControl=0`, `*VMQ=0`, `VMQSupported=0`, `*SRIOV=0`, `*NetworkDirect=0`,
`DropHighlyFragmentedPacket=1`, `CongestionMonitoringEnable=0`, `DMACoalescing=0`,
`EnableCoalesce=0`, `PacketCoalescing=0`, `*PacketDirect=1`, `*NdisPoll=1`,
`ThreadPoll=200000`, `EnableETW=0`, `DisableLLDP=1`, `SipsEnabled=0`, `FecMode=0`,
`StoreBadPackets=0`, `SleepWhileWaiting=0`, `ForceHostExitUlp=1`, `ULPMode=0`,
`WaitAutoNegComplete=0`, `WaitForValidPhyIDRead=0`, `LinkNegotiationProcess=0`,
`DisablePhyReset=0`, `DisableDelayedPowerUp=1`, `DisableIntelRST=1`,
`SSIdleTimeout=1`, `SSIdleTimeoutScreenOff=1`, `SSIdleTimeoutMS=1`,
`WolShutdownLinkSpeed=2`, `HwOption=12582912 (DWord)`, `HwOptionV2=4 (DWord)`,
`HwOptionV3=262144 (DWord)`, `PnPCapabilities=24 (DWord)`

None of these are checked against what `WindowsNdisInventory`-style driver-advertised
enumeration would return — several (RSS profile/queue counts, `HwOption*`) are Realtek
RTL8125-specific internal bitmasks with no public datasheet meaning. Good candidates for
the hardware-capability probe effort already underway (README "Help wanted" section):
if a real report shows a property/range, add it to the catalog; if not, leave it out
rather than guessing the value like this tool does.

## 3. Below-the-driver hardware register pokes (do not do this without a very good reason)

`IMOD-Test.py` also writes directly into device MMIO space via the WinRing0/InpOut
kernel driver, bypassing the NIC driver entirely:

| Vendor (PCI Vendor ID) | Register | Offset from MMIO BAR | Action |
|---|---|---|---|
| Intel (`0x8086`) | ITR (Interrupt Throttle Register, global) | `+0x00C4` | write `0x00000000` |
| Intel (`0x8086`) | EEE Control | `+0x0E00` | write `0x00000000` |
| Realtek (`0x10EC`) | INT_MIT (interrupt mitigation) | `+0x00EC` | write `0x00000000` |
| Realtek (`0x10EC`) | EEE Power Lock | `+0x00D4` | write `0x00000000` |
| HD Audio controller (any) | INTCTL (interrupt control) | `+0x08` | OR in bit 31 |

These offsets are undocumented/reverse-engineered (no datasheet citation in the source),
apply to whatever silicon revision the vendor ID matches without further validation, and
write behind the back of the driver that's supposed to own that memory — a live driver
can race the write, revert it, or crash. This is fundamentally incompatible with
SockTuner's stated goal ("prefer documented Windows APIs and driver capabilities over
scripts") and its "no third-party command-line tools for normal operation" principle.
Flagging only so the *symptom* it's chasing (interrupt moderation / EEE) is recognized as
already covered, more safely, by the NDIS properties in §2
(`*InterruptModeration`, `*EEE`, `ITR`) — the MMIO route adds risk without adding a
capability SockTuner doesn't already plan to expose.

## 4. PCIe MRRS / ASPM (generic, spec-documented — unlike §3)

Unlike the vendor pokes above, this part walks the **standard PCI Express Capability
structure** (PCIe spec, not reverse-engineered):

- Confirm PCI Status register (offset `0x06`) bit 4 (Capabilities List) is set.
- Walk the capability linked list from offset `0x34` looking for Capability ID `0x10`
  (PCI Express Capability).
- **Device Control Register** = `cap_offset + 0x08`; bits 12-14 = Max Read Request Size
  (MRRS): `0`=128B … `5`=4096B.
- **Link Control Register** = `cap_offset + 0x10`; bits 0-1 = ASPM control (`00`=disabled).

Windows doesn't expose a supported API for PCIe config-space MRRS/ASPM control, so there's
no safe path to this without a kernel driver of some kind — noting it here as a boundary
of what's reachable "properly" from user mode, not as something to implement the same way.

## 5. USB xHCI interrupt moderation (generic, spec-documented)

Also standard (xHCI spec, not vendor-specific): `RTSOFF` (Runtime Register Space Offset)
lives at MMIO BAR `+0x18`; each interrupter's IMOD register is at
`RuntimeBase + 0x20 + (interrupter_index * 0x20) + 0x04`, and setting it to `0` disables
interrupt coalescing for that interrupter. Same caveat as §4 — no supported Windows API
surface for this; noted for completeness only.

## 6. CPU MSRs (out of SockTuner's networking scope, noted for completeness)

- `IA32_ENERGY_PERF_BIAS` (MSR `0x1B0`) — documented in the Intel SDM, forced to `0`
  (max performance). Also present on AMD.
- AMD-specific MSR `0xC0010296` ("Data Fabric C-State Lock") — undocumented in public AMD
  docs, reverse-engineered. Same risk profile as §3.

CPU power/C-state tuning is outside SockTuner's networking-stack scope per
[PRODUCT_SCOPE.md](PRODUCT_SCOPE.md); listed only in case a future "system" adjacent tool
in the same family wants it.

## 7. Windows/TCP registry values used (legitimate, documented territory)

```
HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters
  MinRto=300 (DWord), DefaultTTL=64 (DWord), TcpTimedWaitDelay=30 (DWord)

HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile
  NetworkThrottlingIndex=10 (DWord), SystemResponsiveness=10 (DWord)
```

Plus, shelled out via `netsh` rather than a native API call:

```
netsh int tcp set supplemental Template=Internet CongestionProvider=CTCP
netsh int tcp set global autotuninglevel=normal
```

These are all documented, low-risk, standard Windows tuning knobs — the
`NetworkThrottlingIndex`/`SystemResponsiveness` pair overlaps with the MMCSS work already
gated through `SettingTransactionService` (README "Step 6"). The `netsh` TCP settings are
reachable natively via `Set-NetTCPSetting`/IP Helper API equivalents, consistent with the
project's "no third-party command-line tools" goal — `netsh` itself already isn't
third-party, but a native P/Invoke path avoids a process-shell-out for something with a
documented API.

## 8. Design anti-patterns observed (already avoided by SockTuner's plan — kept as a checklist)

These map directly to principles already stated in [README.md](../README.md), listed here
just as concrete "this is the failure mode we're avoiding" examples:

- **Fixed device addressing.** BDF (bus:device.function) values are captured once at
  "Save Startup" time and hard-coded into a generated batch file; a BIOS update, slot
  change, or bus renumbering silently targets the wrong device or a device that no longer
  exists. → SockTuner should always resolve the target adapter/device dynamically at
  apply time, not cache a topological address.
- **No rollback / no dry-run / no diff preview.** Values are written directly with no
  snapshot of the prior state. → matches the already-planned Step 5 dry-run cart +
  rollback + audit history.
- **Driver-description regex instead of capability query.** NIC family is matched by
  string (`Intel|Realtek|Killer|...`) instead of asking the driver what it actually
  supports. → matches the already-stated "expose only settings the NIC driver actually
  advertises" principle.
- **Silent, undocumented, high-blast-radius side effect.** The driver-blocklist disable
  (§0) is the clearest example: an action far outside the stated purpose of the tool,
  applied with no user-facing disclosure.
