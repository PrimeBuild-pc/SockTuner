# Reference notes: Zenit 5.3 — delta vs 5.0, and the `WinTweakVerifier` methodology

> **Provenance and status.** Fourth in the series, after
> [JACKPOTS_ZENIT_REFERENCE.md](JACKPOTS_ZENIT_REFERENCE.md) (3.0),
> [JACKPOTS_ZENIT_4.0_DELTA.md](JACKPOTS_ZENIT_4.0_DELTA.md) and
> [JACKPOTS_ZENIT_5.0_DELTA.md](JACKPOTS_ZENIT_5.0_DELTA.md). Same source family
> ("Jackpot" IMOD tool, bundled `RW.exe` + WinRing0/inpoutx64 kernel drivers).
> **Same rule: nothing here is a verified recommendation and none of the code
> should be copied.**
>
> Folders compared: `Zenit_5.0/Zenit/` vs `Zenit - 5.3/Zenit - 5.2/` (the 5.3
> archive contains a folder still named 5.2). `IMOD_Dashboard.py` 83.9 KB →
> 180.9 KB, `IMOD-Test.py` 47.2 KB → 28.2 KB. New folder: `WinTweakVerifier/`.
>
> **Unlike 4.0 and 5.0, this release contains something genuinely useful.**

## TL;DR

| # | 5.3 change | Useful to SockTuner? |
|---|---|---|
| 1 | **`WinTweakVerifier` — a placebo detector for registry tweaks.** Scans System32/SysWOW64 binaries for a registry value name, then uses PE analysis (imports, string VAs, `.text` xrefs, `RTL_QUERY_REGISTRY_TABLE` tracing) to classify each tweak GREEN / CYAN / YELLOW / RED | **YES — the single most valuable thing in the whole Zenit corpus.** Adopt the *method* as an offline research gate for the catalog; do not adopt the code or trust its verdicts unverified |
| 2 | **A 42-tweak verdict table**, incl. 15 marked outright placebo, and hard evidence naming `tcpipreg.sys` as the consumer of `TcpAckFrequency` / `Tcp1323Opts` | **Yes — direct cross-check data** for `SettingCatalog`. See §2 |
| 3 | **"Apply ONLY Verified Tweaks" button** — a `verified_targets.json` sidecar gates what gets written | **Yes, as a product principle** (evidence-gated apply). SockTuner already does this per-adapter via the driver; this extends the idea to global values |
| 4 | PCIe capability-list **walking** (`cap_ptr` chain from offset `0x34`, looking for cap ID `0x10`) replaces hard-coded register offsets, with verify-read-back on MRRS | Minor — confirms the "discover, don't hard-code" and "read back" principles SockTuner already applies |
| 5 | NDIS list trimmed ~90 → **44 properties**, offloads flipped from *disabled* to *enabled* (`3`), `*JumboPacket = 1510` | Cross-check only — and the whole block is now **orphaned dead code** (§6) |
| 6 | Five new CLI flags (`--pcie-mps`, `--pcie-ltr-clkreq`, `--usb-u0`, `--nvme-apst`, `--msr-cstate`); **four are dead** — parsed, never implemented | No — quality signal (§7) |
| 7 | `MainWindow` class body **duplicated (partly triplicated)**; ~1,300 lines are unreachable dead code — this is the entire 84 KB → 181 KB growth | No — quality signal (§8) |
| — | RW.exe/WinRing0 MMIO+MSR pokes, driver blocklist, BDF baked into `.bat` | **Unchanged.** Still the §0/§3/§8 anti-patterns. Do not adopt |

---

## 1. `WinTweakVerifier` — the methodology worth stealing

`WinTweakVerifier/win_tweak_verifier.py` (19 KB, `pefile`-based) answers a question
SockTuner genuinely needs answered: **does this registry value actually exist in
Windows code, and is anything reading it?**

This matters because global registry knobs have no enumeration API. Unlike NDIS
keywords — where the driver advertises what it supports, so "the driver is the
allowlist" works — you can write *any* name under `Tcpip\Parameters` and Windows
will silently ignore it. That is exactly the niche where placebo tweaks breed,
and it is exactly the class of settings SockTuner carries outside its NDIS surface.

### The pipeline

1. **Parse a `.reg` file** → `(key_path, value_name, type, hex, decimal)` tuples.
   Value names become the search terms.
2. **Fast parallel pre-scan** (`ProcessPoolExecutor`, one task per file) over every
   `.dll`/`.exe`/`.sys` under `C:\Windows\System32` and `SysWOW64` — recursive, so
   `System32\drivers\` and `DriverStore\FileRepository\` are included. Each term is
   searched as **both ASCII and UTF-16LE bytes**.
3. **Deep PE analysis** on the first 5 hit binaries per term:
   - which **registry APIs** the binary imports (`RegQueryValueExW`,
     `RtlQueryRegistryValuesEx`, `ZwQueryValueKey`, …) — a binary that imports none
     cannot be reading the value;
   - the string's **virtual address and section**;
   - **direct `.text` xrefs** — RIP-relative displacement matching back to the
     string's RVA;
   - if no direct xref, **`RTL_QUERY_REGISTRY_TABLE` tracing**: find the 8-byte
     pointer to the string in `.rdata`/`.data`, read adjacent qwords for the
     *destination global variable*, then look for code that reads that variable.
     This distinguishes "the kernel populates a variable from this value **and code
     uses it**" from "…and **nothing** reads it" (a legacy relic).
4. **Verdict**:

   | Verdict | Condition |
   |---|---|
   | 🔴 PLACEBO / FAKE TWEAK | 0 hits in any system binary |
   | 🟢 ACTIVE DRIVER TWEAK | xrefs found in a `.sys` (or dxgi/dxgkrnl) |
   | 🩵 SYSTEM MISMATCH | xrefs only in non-driver OS binaries |
   | 🟡 DEAD STRING | string present, 0 code xrefs |

5. **Sidecar output** — `verified_targets.json` contains only the GREEN entries;
   the dashboard's *"Apply ONLY Verified Tweaks to Regedit"* button writes just those.

**Why this is the right idea.** It replaces "a forum said this helps" with a
falsifiable, reproducible, machine-checkable claim. The **RED verdict is the
strongest signal**: if a value name appears nowhere in any Windows binary, no
amount of benchmarking noise can make it real. That is a cheap, decisive filter —
and one SockTuner can run offline, once, per candidate.

## 2. Cross-check against SockTuner's own catalog

[`SettingCatalog`](../src/SockTuner/Services/SettingCatalog.cs) holds 5 settings.
Four appear in Zenit's report:

| SockTuner setting | Catalog path / evidence level | Zenit verdict | Reading |
|---|---|---|---|
| `TcpAckFrequency` | per-interface `…\Tcpip\Parameters\Interfaces`, *Experimental* | 🟢 **GREEN**, 3 hits | **Supports the entry.** Real value, and the evidence names the consumer |
| `TCPNoDelay` | per-interface, *Experimental* | 🔴 RED — **but see below** | **Verdict does not apply.** Zenit searched `TcpNoDelay`; the byte scan is case-sensitive and it never tested `TCPNoDelay` |
| `NetworkThrottlingIndex` | MMCSS system profile, *Documented* | 🟡 DEAD STRING | **Verdict unreliable.** Found in `mmcss.sys` but in section `INITCONS`; the scanner only walks `.text`, so init-time references are invisible to it |
| `SystemResponsiveness` | MMCSS system profile, *Documented* | 🩵 CYAN, 8 hits | **Supports the entry.** Real direct xref in `avrt.dll` (the MMCSS user-mode API); "no driver xref" is expected for a user-mode scheduler knob |
| `TcpDelAckTicks` | per-interface, *Experimental* | *not tested* | Open — a good first candidate to run the method against |

### The concrete evidence worth recording

- **`TcpAckFrequency`** → `C:\Windows\System32\drivers\tcpipreg.sys`, string at
  `0x14000bb40` (`.rdata`, UTF-16LE), reached through a kernel registry table whose
  destination variable `0x1400021d0` **is read by code** at `0x140001c23`. In
  `tcpip.sys` the same value populates a variable that **nothing reads** ("legacy
  relic"). So the live consumer is `tcpipreg.sys`, not `tcpip.sys`.
- **`Tcp1323Opts`** → same picture: a direct `.text` xref in `tcpipreg.sys`; in
  `tcpip.sys` the string only lives in `.rsrc` (resources, not code).
- **`EnableRSS`** → hits **only in vendor NIC drivers** (`mlx4eth63.sys`, `mlx5.sys`,
  `ipoib6x.sys`, Realtek `rtwlan*.sys`) — **not** in `tcpip.sys`. Zenit still marked
  it GREEN and put it under `Tcpip\Parameters`. **This is the tool's central flaw
  demonstrated live** (§3) — and it independently validates SockTuner's position:
  RSS is a per-adapter NDIS keyword (`*RSS`), owned by the driver, not a global TCP knob.

### The 15 values Zenit found nowhere in Windows

`WaitToKillAppTimeout`, `TcpNoDelay`, `EnableTCPA`, `EnableDca`, `ArpCacheSize`,
`TcpCreateAndConnectTcbRateLimitDepth`, `NegativeSOACacheTime`, `GPU Priority`,
`MaxFreeTcbs`, `SFIO Priority`, `MaxSOACacheEntryTtlMax`, `SackOpts`, `EnableWsd`,
`GlobalMaxTcpWindowSize`, `NetFailureCacheTime`.

Several are known XP/2003-era values genuinely removed from modern Windows
(`SackOpts`, `GlobalMaxTcpWindowSize`, `MaxFreeTcbs`), which is a decent sanity
check that the method's RED verdicts track reality. Treat the list as **"do not add
to the catalog without independent proof"**, not as settled fact — the MMCSS ones
(`GPU Priority`, `SFIO Priority`) are documented task-profile values and their RED
verdict is suspicious for the same `INITCONS`/section reason as above.

## 3. Where the method breaks — read before trusting any verdict

The idea is sound; this implementation is not rigorous enough to gate anything
automatically. Concretely:

1. **It never checks the key path.** It matches value *names* only. A value can be
   real, referenced by a driver, and still be read from a completely different key
   than the `.reg` claims. Zenit's own `verified_targets.json` ships
   `DisableTaskOffload` under `HKEY_CURRENT_USER\Control Panel\Desktop` — nonsense —
   and marks it GREEN. `TcpAckFrequency` is likewise filed under the global
   `Tcpip\Parameters` when the documented location is per-interface.
2. **Case-sensitive byte matching.** `TcpNoDelay` ≠ `TCPNoDelay`. A casing slip
   produces a confident false RED (§2).
3. **The xref scanner is not a disassembler.** It walks `.text` byte by byte, accepts
   any position whose first bytes look like a REX+LEA/MOV prefix, then unpacks a
   displacement at a **fixed** `+3..+7` offset assuming a 7-byte instruction. It is
   not instruction-aligned and not length-aware, so it both misses real references
   and can fabricate spurious ones.
4. **It only scans `.text`.** Strings referenced from init-only/discardable sections
   (`INITCONS` in `mmcss.sys`) are invisible → false "DEAD STRING".
5. **`candidates = hits[:5]`.** Only the first five hit binaries are analysed, in
   arbitrary filesystem order. The actual consumer can be the sixth.
6. **`RTL_QUERY_REGISTRY_TABLE` tracing guesses the struct layout** by probing ±8 and
   ±16 bytes around the string pointer. Loose heuristic, not the real layout.
7. **Presence ≠ effect.** Even a correct GREEN only proves *something reads the
   value*. It says nothing about whether the value helps, what range is valid, or
   whether the effect is measurable.

**Net: RED with 0 hits is a strong negative signal. GREEN is a weak positive
signal.** Use it to *reject* candidates cheaply and to *locate the consuming
binary* for manual follow-up — never to auto-approve a write.

## 4. What SockTuner should actually take from this

**A research-side verification gate, not a shipped feature.**

- **Do:** for each *global* (non-NDIS) registry knob in `SettingCatalog`, record in
  the docs which Windows binary contains the value name and, where determinable,
  what reads it. `TcpAckFrequency` → `tcpipreg.sys` is the template. This upgrades
  an `EvidenceLevel` from an assertion to a citation.
- **Do:** use the cheap half of the method first — a case-insensitive ASCII+UTF-16LE
  string search across `System32` (`findstr`/`rg -a` needs no `pefile`, no PE parsing,
  no disassembly). That alone yields the decisive RED verdicts, which is most of the
  value for a fraction of the complexity.
- **Do:** keep the "only apply what's verified" *principle*. SockTuner already
  enforces it per-adapter (the driver is the allowlist); the gap is global values,
  and this is a way to close it with documented evidence instead of a runtime scan.
- **Don't:** ship a System32 PE scanner in the product. It is slow, needs a PE
  parser, its verdicts are not trustworthy enough to gate writes, and scanning every
  system binary at runtime is exactly the kind of behaviour SockTuner shouldn't have.
- **Don't:** import `verified_targets.json` or the report's verdicts as fact. Two of
  the five entries in that file have wrong key paths.

**Suggested first pass** (offline, one-off, results into the docs): run the string
search for `TCPNoDelay` (correct casing), `TcpDelAckTicks`, `NetworkThrottlingIndex`
and `SystemResponsiveness`, and record which binaries and sections contain them.
That either substantiates or retires the two *Experimental* entries.

## 5. PCIe capability-list walking (small, genuine improvement)

`apply_pcie_tuning` now discovers the PCI Express Capability properly instead of
assuming a fixed offset: read the capabilities pointer at `0x34`, walk the linked
list (`cap_id` at `ptr`, next at `ptr+1`, masked `& 0xFC`) with a `visited` set to
break malformed loops, and stop at cap ID `0x10`. MRRS writes are followed by a
read-back that distinguishes "applied" from "hardware locked at its max".

Out of scope for SockTuner (this is raw PCI config space via a kernel driver), but
it's the same two principles SockTuner already holds: **discover, don't hard-code**,
and **read back after writing**. Noted as convergence, nothing to import.

## 6. NDIS block: trimmed, values flipped — and now orphaned

`ZENOT_POWERSHELL_SCRIPT` is down to **44 properties** (from ~90). Notable changes
vs the values recorded in
[JACKPOTS_ZENIT_NDIS_CANDIDATES.md](JACKPOTS_ZENIT_NDIS_CANDIDATES.md):

- **Offloads are now enabled, not disabled** — `*TCPUDPChecksumOffloadIPv4/6 = 3`,
  `*LsoV2IPv4/6 = 1`, `*RscIPv4/6 = 1`, `*IPsecOffloadV2 = 3`. A straight reversal
  of earlier versions' "disable everything" stance, with no stated rationale. Worth
  noting as evidence that these value choices are fashion, not measurement.
- **Power/latency knobs still forced off** — `*EEE = 0`, `*FlowControl = 0`,
  `*SelectiveSuspend = 0`, `*NicAutoPowerSaver = 0`, `*PacketCoalescing = 0`.
- `*ReceiveBuffers`/`*TransmitBuffers = 4096` — written blind, with no check against
  the driver's advertised maximum.
- **`*JumboPacket = 1510`** — a nonsense value; standard settings are 1514 or
  9014/9614, and 1510 is below normal Ethernet framing. Good example of why
  SockTuner validates against driver-advertised enums rather than copying constants.
- Writes go **directly to the class key** via `Set-ItemProperty`, bypassing
  `Set-NetAdapterAdvancedProperty` and therefore all driver-side validation — still
  the §0 anti-pattern from the 3.0 notes.

**And it is now dead code.** `ZENOT_POWERSHELL_SCRIPT` is defined but referenced
nowhere: the function that executed it is gone from `IMOD-Test.py`, and `main()`
never dispatches `--network-stack` even though the flag is still parsed *and the
generated startup `.bat` still passes it*. The tool's headline networking feature
does nothing in 5.3.

## 7. Four of five new flags are dead

| Flag | Status |
|---|---|
| `--msr-cstate` | **Implemented** (`apply_cstate_lock` reaches `apply_msr_tweaks`) |
| `--pcie-mps` | Dead — `harmonize_mps` param exists, never passed by `main()`, never used in the body |
| `--pcie-ltr-clkreq` | Dead — same, `purge_ltr_clkreq` unused |
| `--usb-u0` | Dead — `force_u0` *is* passed into `process_usb_controller`, whose body never references it |
| `--nvme-apst` | Dead — parsed, referenced nowhere |
| `--network-stack` | Dead — parsed, not dispatched (§6) |

The generated `IMOD_Profile.bat` passes all of them on every boot. Five of six
arguments are no-ops.

## 8. Code duplication — the whole file-size increase

`MainWindow` (lines 610–3602) has its **entire page-building body defined twice**:
`setup_hardware_page` at 1039 *and* 2321, `setup_gpu_page` at 1259 *and* 2541, and
the same for the power, maintenance and software-hub pages;
`apply_verified_tweaks_to_regedit` appears **three** times. Python silently keeps the
last definition, so roughly 1,300 lines are unreachable. This — not new features —
is what took the dashboard from 84 KB to 181 KB.

Together with 5.0's truncated `IMOD-Test.py`, the dead flags (§7) and the orphaned
NDIS block (§6), it reinforces the standing caveat: **this corpus is hand-assembled
and frequently broken.** Every extracted value is a lead to verify, never a working
reference.

---

## Net takeaways for SockTuner

1. **Adopt the verification *method*, not the tool** (§1, §4). A one-off offline
   string search over `System32` for each global registry knob in the catalog turns
   `EvidenceLevel` into a citation. Start with the two *Experimental* per-interface
   TCP entries.
2. **Record the concrete evidence already available** (§2): `TcpAckFrequency` and
   `Tcp1323Opts` are consumed by `tcpipreg.sys`, not `tcpip.sys`.
3. **Don't act on Zenit's verdicts directly** (§3). The RED for `TcpNoDelay` is a
   casing artefact and does not apply to SockTuner's `TCPNoDelay`; the DEAD STRING
   for `NetworkThrottlingIndex` is a scanner limitation (`INITCONS` section), not
   evidence against the setting. Neither catalog entry is invalidated by this report.
4. **`EnableRSS` independently validates "the driver is the allowlist"** (§2): the
   name lives in vendor NIC drivers as an NDIS keyword, not in the TCP/IP stack as a
   global value.
5. **Everything below the driver stays rejected** — RW.exe/WinRing0 MMIO and MSR
   pokes, PCI config-space writes, the driver blocklist. Unchanged since 3.0.
