# Reference notes: "Jackpot Hardware Tuner Zenit 4.0" — delta vs 3.0

> **Provenance and status.** Companion to [JACKPOTS_ZENIT_REFERENCE.md](JACKPOTS_ZENIT_REFERENCE.md)
> (the 3.0 notes). Same source family (Noverse/"Jackpot" IMOD tool, bundled `RW.exe` +
> WinRing0/inpoutx64 kernel drivers). Same rule applies: **nothing here is a verified
> recommendation and none of the code should be copied.** What's useful is the *list of
> new knobs/techniques* to cross-check against SockTuner's own design
> ([PRODUCT_SCOPE.md](PRODUCT_SCOPE.md), [ROADMAP.md](ROADMAP.md),
> [`SettingCatalog`](../src/SockTuner/Services/SettingCatalog.cs)), and the *measurement*
> idea, which is the one genuinely new capability.
>
> Files compared: `Jackpots Zenit 3.0/` vs `Jackpot Hardware Tuner Zenit 4.0/`
> (`IMOD-Test.py` 33.7 KB → 50.7 KB, `IMOD_Dashboard.py` 28.6 KB → 61.0 KB).
>
> **Next version:** [JACKPOTS_ZENIT_5.0_DELTA.md](JACKPOTS_ZENIT_5.0_DELTA.md)
> (5.0 adds no networking knobs — GPU tweaks, a junk cleaner, and a download launcher)
> · [JACKPOTS_ZENIT_5.3_DELTA.md](JACKPOTS_ZENIT_5.3_DELTA.md) (5.3 — registry-tweak
> verification method).

## TL;DR — what changed and what (if anything) is worth taking

| # | 4.0 change | New vs 3.0? | Useful to SockTuner? |
|---|---|---|---|
| 1 | **Dynamic PnP hardware scan** (`Get-PnpDevice` + `DEVPKEY_Device_LocationInfo` → BDF at scan time) | New | **Yes, as a pattern** — proper device discovery, not hard-coded addressing |
| 2 | **Dynamic MTU auto-detection** (ping DF-bit binary probe, +28) | New | **Yes** — documented, safe, a real "detect optimal" feature idea |
| 3 | **DPC/ISR latency measurement** (deferred xperf pipeline, weighted latency per module) | New | **Yes — the one interesting new idea**: before/after *quantification* |
| 4 | **State reconciliation** from last-run log (per-device ACTIVE/LOCKED checklist) | New | Concept yes, implementation no (parses stdout, not real state) |
| 5 | **MSI-mode forcing** (`MSISupported=1`, drop `MessageNumberLimit`) | New | Maybe — documented registry mechanism, legit for NIC |
| 6 | **Physical-NIC detection via `Characteristics` bit 0x4** (`NCF_PHYSICAL_ADAPTER`) | New | Minor — better than pure name regex |
| 7 | **~15 extra documented TCP/IP knobs** (PMTU, ServiceProvider priorities, AFD, DNS negcache…) | New values | Cross-check catalog |
| 8 | **Native WinRing0 DLL C-bindings + hybrid runner + verify-read-back** | Rewrite | Architecture note only; verify-read-back worth mirroring |
| 9 | Driver-blocklist disable, MMIO ITR/EEE pokes, MSR pokes, ~90 unchecked NDIS props, BDF cached into generated .bat | **Unchanged** | Still all the §0/§3/§8 anti-patterns. Do not adopt. |

---

## 1. Dynamic PnP hardware scan (`IMOD_Dashboard.py::safe_scan`) — the "hardware scanning" you asked about

3.0 hard-coded BDF addresses. 4.0 discovers devices at scan time with one PowerShell pass:

```powershell
Get-PnpDevice -PresentOnly | Where-Object { $_.Class -eq 'USB'  -and $_.FriendlyName -match 'xHCI|eXtensible' } ...
Get-PnpDevice -Class 'Display' -PresentOnly ...
Get-PnpDevice -Class 'System'  -PresentOnly | Where-Object { $_.FriendlyName -match 'PCI Express Root|Host Bridge' } ...
Get-PnpDevice -Class 'Net'     -PresentOnly | Where-Object { $_.FriendlyName -match 'Ethernet|Network|Gigabit|2.5G|Gaming' -and $_.FriendlyName -notmatch 'Virtual|TAP|VPN|Bluetooth' } ...
Get-PnpDevice -Class 'SCSIAdapter' -PresentOnly | Where-Object { $_.FriendlyName -match 'NVMe|Express' } ...
```

For each device it reads `DEVPKEY_Device_LocationInfo` (a string like `"PCI bus 8, device 0, function 0"`),
regexes the three integers out, and formats them into a `bus:dev.func` BDF. It also drops integrated
GPUs (`intel(r) uhd/iris`, `radeon graphics`) to `SKIP` automatically.

**Why this matters for SockTuner:** it's the correct answer to the 3.0 anti-pattern §8
("fixed device addressing"). Resolving the NIC *at run time* from a stable OS device
property is exactly what SockTuner already wants for adapter targeting. The specific
mechanism — `Get-PnpDevice -Class 'Net' -PresentOnly` filtered by
`Virtual|TAP|VPN|Bluetooth`, plus `DEVPKEY_Device_LocationInfo` — is a clean, documented
CIM/PnP path and maps to `MSFT_PnpDevice` / `Get-PnpDeviceProperty` in WMI, reachable from
.NET via `System.Management` (already a SockTuner dependency).

**Caveat — still only half-fixed.** `save_startup()` then bakes the resolved BDF strings
into a generated `.bat` and registers a `schtasks ONLOGON` task. So the *scan* is dynamic
but the *saved startup profile* re-freezes the topological address — a slot/BIOS change
still mis-targets on next boot. SockTuner's design (resolve at apply time, every time)
remains the stricter, correct position; 4.0 only moved the freeze one step later.

## 2. Dynamic MTU auto-detection (inside the "Zenot" PowerShell block)

Genuinely new and genuinely safe — worth noting as a feature idea:

```powershell
$bestP = 0
for ($p = 1472; $p -ge 1400; $p--) {
    ping.exe 1.1.1.1 -f -n 1 -l $p -w 1000 | Out-Null   # -f = don't-fragment
    if ($LASTEXITCODE -eq 0) { $bestP = $p; break }
}
if ($bestP -gt 0) {
    $mtu = $bestP + 28                                    # + 20 IP + 8 ICMP headers
    netsh interface ipv4 set subinterface "<nic>" mtu=$mtu store=persistent
}
```

Probe the largest ICMP payload that traverses the path without fragmenting, add the 28-byte
header, and that's the path MTU. It's a linear scan (1472→1400) rather than a binary search,
and it hard-codes `1.1.1.1` as the probe target, but the *technique* is sound and 100% user-mode.
A "Detect optimal MTU" helper is a plausible SockTuner feature — do it with `SendARP`/IP
Helper or a raw ICMP `Ping` with `DontFragment` in .NET instead of shelling `ping.exe`, and
binary-search the range. Pair with the existing interface-metric knob (4.0 also forces
`InterfaceMetric 1` via `Set-NetIPInterface`).

## 3. DPC/ISR latency measurement — the deferred xperf pipeline (`IMOD-Test.py::run_xperf_deferred_pipeline`)

This is the **one new capability class** 3.0 didn't have: it *measures* instead of only
writing. The tool ships the Windows Performance Toolkit (`xperf/xperf.exe`) and runs:

1. GUI closes itself first ("0% GUI DPC overhead"), `time.sleep(60)` for idle stabilization.
2. `xperf -on PROC_THREAD+LOADER+DPC+INTERRUPT`, trace 20 s, `xperf -d out.etl`.
3. `xperf -i out.etl -o out.txt -a dpcisr` → parse the DPC/ISR-by-module report.
4. Aggregates per driver module, then computes **weighted latency (µs)** and an **effective
   impact (%)** for DPC, ISR, and total; writes a summary, drops a `xperf_ready.flag`, and
   relaunches the GUI, which detects the flag and pops the report.

**Why it's interesting for SockTuner:** every tweak in this whole family is asserted, never
demonstrated. A measurement path — "here's DPC/ISR latency attributable to the NIC driver
before vs after" — is what would let SockTuner make *evidence-based* claims instead of
cargo-culting registry values. This fits the project's stated engineering position
(README "prefer verifiable"). The xperf `-a dpcisr` action and the `PROC_THREAD+LOADER+DPC+INTERRUPT`
kernel group are the documented WPT surface; a future SockTuner "benchmark / verify" step
could wrap WPR/WPA or `xperf` the same way (redistribution licensing to be checked). The
weighting math in the script is home-grown and unvalidated — take the *idea* (per-module
DPC/ISR before/after), not their formula.

## 4. State reconciliation from log (`parse_log_checklist`)

On each scan the dashboard re-reads `startup_log.txt` from the last apply run and rebuilds a
per-device status: it string-matches `"SUCCESS"`, `"already set"`, `"hardware locked"`,
`"ASPM ... Purged"`, `"USB Interrupter ... set to"` etc., and tags each BDF `ACTIVE`,
`ACTIVE (512B)`, or `LOCKED (<max>)`. The table then shows which devices are currently
optimized and which hit a hardware ceiling.

Concept is right (show applied vs. not, surface hardware-locked limits) and aligns with
SockTuner's planned Step 5 dry-run/rollback/audit. Implementation is the wrong way to do it —
it parses its own console stdout rather than reading actual current registry/register state.
SockTuner should snapshot the real prior value and re-read the real current value (it already
plans to). Keep the **UX idea**: a per-setting badge of `applied / default / locked-at-max`.

## 5. MSI-mode forcing (new, documented registry mechanism)

New in the Zenot block — for each physical NIC (and GPU, and `USBXHCI` controllers) it does:

```
HKLM\...\Enum\PCI\<dev>\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties
    MSISupported (DWord) = 1
    (delete MessageNumberLimit)
```

This is the standard, documented "force MSI/MSI-X interrupts" tweak (same thing MSI-mode
utilities do). Unlike the MMIO ITR pokes in §3 of the 3.0 notes, it's a supported registry
location. If SockTuner ever expands past NDIS advanced properties, `MSISupported` for the NIC
is a legitimately documentable knob (with the usual "driver/device must support it, and it's
per-instance under Enum\PCI, resolved dynamically" caveats). Note it targets `Enum\PCI\...`
device-instance keys, so it needs the same dynamic-resolution discipline as §1.

## 6. Physical-NIC detection via Characteristics bit (improvement over 3.0)

3.0 gated purely on a driver-description regex. 4.0 first checks the NDIS `Characteristics`
value for bit `0x4` (`NCF_PHYSICAL_ADAPTER`) and only falls back to the vendor regex:

```powershell
$isPhysical = ([int]$characteristics -band 0x4) -eq 0x4
$isEthernetVendor = $driverDesc -match 'Intel|Realtek|Killer|Aquantia|Broadcom'
if ($isPhysical -or $isEthernetVendor) { ... }
```

Closer to SockTuner's "ask what it is, don't string-match" principle. Still an `-or` with the
regex, so a mislabeled vendor string can still pull in a non-physical adapter — but the
`NCF_PHYSICAL_ADAPTER` bit is the right primary signal and worth knowing for
`WindowsNdisInventory` adapter classification.

## 7. New documented TCP/IP knobs to cross-check against the catalog

4.0 adds these to the registry block (all documented Windows parameters; **values unvetted**,
listed only so nothing is missed in `WindowsTcpSettingInventory` / `SettingCatalog`):

```
Tcpip\Parameters:
  EnablePMTUDiscovery=1, EnablePMTUBHDetect=1, TcpMaxDataRetransmissions=2,
  MaxUserPort=65534, LoopbackLargeMtu=0
Tcpip6\Parameters:
  LoopbackLargeMtu=0, DisabledComponents=0x20
AFD\Parameters:
  FastSendDatagramThreshold=1500
Tcpip\ServiceProvider:
  LocalPriority=4, HostsPriority=5, DnsPriority=6, NetbtPriority=7
Dnscache\Parameters:
  NegativeCacheTime=0, NegativeSOACacheTime=0, NetFailureCacheTime=0
```

Plus native cmdlets instead of registry for offload globals — worth preferring over `netsh`:

```
Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing Enabled
Set-NetOffloadGlobalSetting -PacketCoalescingFilter Disabled
Set-NetOffloadGlobalSetting -UdpReceiveOffload Disabled
Set-NetIPInterface -InterfaceAlias <nic> -AutomaticMetric Disabled -InterfaceMetric 1
```

(These map to `MSFT_NetOffloadGlobalSetting` / `MSFT_NetIPInterface` WMI classes — a native
P/Invoke/`System.Management` path, consistent with the project's "no shell-outs" goal.)
`DisabledComponents=0x20` is the documented "prefer IPv4 over IPv6" bit — behavioural, flag it
as opinionated. The `ServiceProvider` priority quartet is documented but obscure; verify before
cataloguing.

## 8. Architecture rewrite (note only — do not adopt)

3.0 shelled out to `chiptool.exe`/`RW.exe`. 4.0 rewrote the hardware layer:

- **`NativeKernelRunner`** — loads `WinRing0x64.dll` directly (`InitializeOls`,
  `ReadPciConfig{Byte,Word,Dword}`, `WritePciConfigWord`, `Wrmsr`) and `inpoutx64.dll`
  (`MapPhysToLin`/`UnmapPhysicalMemory`) for MMIO — no more process shell-out.
- **`RwRunner`** — `Rw.exe` fallback (skips MSR writes: `"BSoD-Schutz: MSR-Write … übersprungen"`).
- **`SmartHardwareRunner`** — hybrid: tries RW first, falls back to native per-op, each wrapped
  in try/except so one failing read doesn't abort the batch.
- **`resolve_mmio_base`** — walks BARs `0x10..0x24`, skips I/O-space BARs, handles 64-bit BARs
  (`bar & 0x4` → read the high dword) before using the base. Cleaner than 3.0.
- **verify-read-back** — every write is followed by a read to confirm, and MRRS detects
  "hardware locked at max supported limit". **This pattern is worth mirroring** in SockTuner's
  apply step (write → re-read → report actual, surface silently-clamped values), independent of
  the risky transport it rides on here.

Still built on WinRing0/inpoutx64 (BYOVD-class drivers). Architecture note only.

## 9. Unchanged anti-patterns — same warnings as the 3.0 notes still apply

All of these carried over verbatim; nothing was fixed:

- **§0 driver-blocklist disable is still there** — `apply_driver_blocklist_tweak()` sets
  `HKLM\SYSTEM\CurrentControlSet\Control\CI\Config\VulnerableDriverBlocklistEnable = 0`, and
  4.0 now calls it **unconditionally at the top of `get_hardware_runner()`**, i.e. on every
  hardware run, still with no UI disclosure. Same hard "never do this" from the 3.0 notes §0.
- **MMIO ITR/EEE pokes unchanged** (Intel `+0x00C4`/`+0x0E00`, Realtek `+0x00EC`/`+0x00D4`,
  HD-Audio INTCTL `+0x08` bit31) — still undocumented offsets written behind the driver's back.
- **MSR pokes unchanged/expanded** — `IA32_ENERGY_PERF_BIAS` (0x1B0) and AMD
  `0xC0010296` (undocumented "Data Fabric C-State Lock"), both forced to 0. Out of SockTuner's
  networking scope regardless.
- **~90 NDIS properties still written unconditionally** by name, gated only by the
  vendor/`Characteristics` `-or`, never checked against what the driver advertises; still
  includes the guessed Realtek `HwOption*` bitmasks. SockTuner's "driver-advertised only"
  design is still the right counter-position.
- **BDF still cached into a generated `.bat` + `schtasks ONLOGON`** (see §1 caveat).
- **Aggressive defaults** — the shipped `IMOD_Profile.bat` runs `--pcie-mrrs 5` (4096 B MRRS)
  across GPU/NVMe/NIC and `--nic-imod --msr-epb`. Values to treat as "observed, not endorsed".

---

### Net takeaways for SockTuner (future, not now)

1. **Adopt the discovery pattern, not the code:** dynamic NIC resolution via
   `Get-PnpDevice`/`DEVPKEY_Device_LocationInfo` (WMI `MSFT_PnpDevice`) instead of any cached
   address — validates the design already in scope.
2. **Consider a "Detect optimal MTU" helper** (native ICMP DontFragment binary-search, not
   `ping.exe`), paired with the interface-metric knob.
3. **The measurement idea is the real prize:** a before/after DPC/ISR (or simpler latency)
   benchmark would let SockTuner *prove* effect instead of asserting it. Wrap WPT/`xperf -a
   dpcisr` (check redistribution) — take the concept, not their weighting math.
4. **Mirror verify-read-back** in the apply path and surface hardware-clamped/locked values.
5. **Catalog cross-check** the §7 knobs and `MSISupported`; keep prefering native
   `Set-Net*`/`System.Management` over `netsh`.
6. Everything in §9 stays on the "recognize the symptom, reject the method" list.
