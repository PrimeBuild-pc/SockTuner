# Reference notes: "Jackpot's Hardware Register Engine v7.0" (Zenit 5.0) — delta vs 4.0

> **Provenance and status.** Third in the series after
> [JACKPOTS_ZENIT_REFERENCE.md](JACKPOTS_ZENIT_REFERENCE.md) (3.0) and
> [JACKPOTS_ZENIT_4.0_DELTA.md](JACKPOTS_ZENIT_4.0_DELTA.md). Same source family
> (Noverse/"Jackpot" IMOD tool, bundled `RW.exe` + WinRing0/inpoutx64 kernel
> drivers). **Same rule: nothing here is a verified recommendation and none of the
> code should be copied.** The value is the *list of new knobs/techniques* to
> cross-check against SockTuner's own design
> ([PRODUCT_SCOPE.md](PRODUCT_SCOPE.md), [ROADMAP.md](ROADMAP.md),
> [`SettingCatalog`](../src/SockTuner/Services/SettingCatalog.cs)).
>
> Folders compared: `Jackpot Hardware Tuner Zenit 4.0/` vs `Zenit_5.0/Zenit/`.
> Byte-level diff: the delta is almost entirely in **`IMOD_Dashboard.py`**
> (61.0 KB → 83.9 KB, +437 lines). Every other shared file is byte-identical
> (`IMOD_Cleaner.py`, `dpc-isr.vbs`, `gpu/`, `RW/`, `chiptool/`, `xperf/`,
> `power/`, `spowers/`, the `.pow`/`.nip` bundles). `Start_Hardware_Engine.bat`
> was renamed to `Click_to_Start.bat` (identical content).

## TL;DR — what changed and what (if anything) is worth taking

| # | 5.0 change | New vs 4.0? | Useful to SockTuner? |
|---|---|---|---|
| 1 | **Native registry helpers replace shelled-out `.bat`** for GPU/system toggles (`winreg` writes, not `subprocess` to a generated batch) | New | **Yes, as validation only** — confirms SockTuner's own native-registry stance; no new *network* knob |
| 2 | **NVIDIA ECC / HDCP disable via display-class registry keys** (`RMNoECCFuseCheck`, `RMHdcpKeyglobZero`, + `nvidia-smi -e`) | New | No — GPU, out of scope. Note the class-key discovery pattern only |
| 3 | **HAGS toggle** (`GraphicsDrivers\HwSchMode` = 2/1) | New | No — GPU, out of scope; but it's the "one DWORD, documented, reboot-gated" shape SockTuner already uses |
| 4 | **"Deep Cleaner"** background `QThread` — purges Temp/Prefetch/browser+Discord caches/WER/minidumps, `ipconfig /flushdns`, `wevtutil cl` on every log, deletes RunMRU/TypedPaths | New | **No — reject.** Destructive junk-cleaner well outside a NIC tuner. Only the async-worker+log-signal *pattern* is unremarkable/already-known |
| 5 | **"Take Ownership" Explorer context-menu** installer (writes `HKCR\*\shell` + `Directory\shell`) | New | No — reject; unrelated, and a persistent shell modification |
| 6 | **"Software Hub"** page: 53 hard-coded vendor download URLs opened in a browser | New | **No — reject.** Stale/unpinned installer URLs are a supply-chain smell; nothing to take |
| 7 | **`--clean-system` flag** threaded into the generated startup `.bat` | New (partial) | No — and see §7, the flag is dead on the launcher side |
| 8 | Theme unification (purple `#7C4DFF` → blue `#00B0FF`), button-variant cleanup, Noverse credits/links stripped | Cosmetic | No |
| 9 | **`IMOD-Test.py` shipped truncated/corrupt** (ends mid-statement at line 916; `parse_args`/`main`/CLI entrypoint gone) | Regression | No — quality/provenance signal, see §8 |
| — | All 3.0/4.0 hardware-poke anti-patterns (RW.exe MMIO/MSR pokes, ~90 unchecked NDIS props, BDF baked into `.bat`, driver blocklist) | **Unchanged** | Still all the §0/§3/§8 anti-patterns. Do not adopt |

**Bottom line for SockTuner: nothing new worth adopting.** 5.0 is feature-creep
sideways into GPU tweaks, a system junk-cleaner, a context-menu hack, and a
software-installer launcher — none of it networking. The single positive signal
is architectural (§1): the new toggles are written with native `winreg` calls
instead of spawning a batch file, which is the direction SockTuner already took.

---

## 1. Native registry helpers instead of generated `.bat` — the one architectural improvement

4.0 (and earlier) executed system changes by writing a `.bat` and shelling out
to it. 5.0 adds a block of module-level helpers in `IMOD_Dashboard.py` that call
`winreg` directly and return `(success: bool, message: str)`:

```python
def set_hags(enable: bool):
    k = winreg.OpenKey(HKLM, r"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", 0, KEY_SET_VALUE)
    winreg.SetValueEx(k, "HwSchMode", 0, winreg.REG_DWORD, 2 if enable else 1)
    winreg.CloseKey(k)
    return True, "... (Reboot required)"
```

wired to the UI through a small dispatcher:

```python
def handle_native_toggle(self, func, *args):
    success, msg = func(*args_without_title)
    self.log(f"{title}: {msg}")
    QMessageBox.information/critical(self, ...)
```

**Why it matters for SockTuner:** this is Zenit belatedly arriving at the pattern
SockTuner started with — a typed apply that touches the registry in-process,
returns a structured result, and surfaces success/failure to the UI, instead of
generating and running a script. It's confirmation of the design choice, not a
new capability. SockTuner's C# equivalents (`WindowsNdisInventory`, the live
write path — the driver is the allowlist) are already stricter: they read back
and they don't persist a frozen artifact. **Take: nothing to import; note that
even the reference tool is moving toward in-process registry writes.**

**Caveat.** The `(success, message)` helpers still don't verify-read-back after
the write (they trust `SetValueEx` returning), and several swallow every
exception into a string. SockTuner's read-after-write remains the stronger form.

## 2. NVIDIA ECC / HDCP disable (out of scope — pattern note only)

New helpers hunt the NVIDIA display adapter by walking
`...\Control\Class\{4d36e968-...}\0000..0009` and matching
`ProviderName` containing `NVIDIA`, then write:

- ECC off: `RMNoECCFuseCheck=1` plus a batch of `RMEnable*ECC=0` keys, and
  `nvidia-smi -e 0`.
- HDCP off: `RMHdcpKeyglobZero=1`, `RmDisableHdcp22=1`.

These are GPU-latency-theatre knobs (disabling error-checking / display copy
protection for "gaming performance"), entirely outside a NIC tuner. The **only**
transferable detail is the *class-key discovery loop* — iterate the `{class-GUID}`
subkeys and match a stable property — which is the same shape SockTuner already
uses to resolve the NIC subkey under `{4d36e972-...}` in
[`WindowsNdisInventory`](../src/SockTuner/Services/WindowsNdisInventory.cs)
(matching on `NetCfgInstanceId`). Nothing new there. **Reject the knobs.**

## 3. HAGS toggle (out of scope, but the "right shape" for a knob)

`GraphicsDrivers\HwSchMode` = `2` (enabled) / `1` (disabled), reboot-gated,
single `REG_DWORD`, documented by Microsoft. GPU-side, so out of SockTuner's
networking scope — noted only because it's the clean archetype SockTuner already
follows: one documented value, clear revert, reboot requirement stated to the
user. No action.

## 4. "Deep Cleaner" — reject

New `CleanerWorker(QThread)` that, on a button or on startup (if the new
checkbox is set), deletes the contents of ~18 directories (`%TEMP%`,
`Windows\Temp`, `Prefetch`, `SoftwareDistribution\Download`, `Recent` +
Jump-List `AutomaticDestinations`/`CustomDestinations`, INetCache, History,
Edge/Chrome `Cache`, Discord `Cache`/`Code Cache`, WER `ReportArchive`/
`ReportQueue`, `Minidump`), then `ipconfig /flushdns`, deletes the `RunMRU` and
`TypedPaths` registry keys, and loops `wevtutil cl` over **every** event log
from `wevtutil el`.

This is a privacy/junk cleaner bolted onto a hardware tuner. It's destructive
(wiping Prefetch hurts cold-start times it claims to help; clearing all event
logs destroys diagnostic history; nuking `SoftwareDistribution\Download` can
disrupt pending Windows Update state). **Nothing to take.** The one
network-adjacent line, `ipconfig /flushdns`, is a trivial documented command
SockTuner can offer on its own terms if ever wanted — not a reason to look here.

The `QThread` + `log_signal`/`finished_signal` structure is ordinary Qt worker
plumbing; SockTuner's WPF stack has the equivalent already. No pattern debt paid.

## 5. "Take Ownership" context menu — reject

Writes a `HKCR\*\shell\TakeOwnership` (+ `Directory\shell\...`) entry whose
command runs `takeown /f … && icacls … /grant *S-1-3-4:F` elevated. Persistent
shell modification, unrelated to networking, and a standing privilege-granting
menu item. Recognise it, reject it.

## 6. "Software Hub" — reject (supply-chain smell)

A new page renders 53 buttons across five sections (Gaming launchers,
Multimedia, Tools, Browsers, Dev SDKs); each opens a **hard-coded vendor URL** in
the default browser (`webbrowser.open`). Many are direct, unpinned installer
links (specific `.exe`/`.msi` versions, some CDN/redirector URLs, some third-party
"UNLOCKED"/modded builds hosted on random GitHub forks).

For SockTuner this is a clear **anti-pattern**: shipping a curated list of
version-pinned download URLs is a maintenance and supply-chain liability (links
rot; a hijacked host serves malware behind a trusted button). It reinforces
SockTuner's existing "no bundled third-party binaries, no download launcher"
position. **Take nothing.**

## 7. `--clean-system` flag is dead on arrival

The dashboard now appends `--clean-system` to the generated startup `.bat` when
the new "Execute Deep Cleaner on Startup" checkbox is set, and reads it back to
restore checkbox state. But the **worker that actually parses that flag lives in
`IMOD-Test.py`, which 5.0 ships truncated** (§8) — its `parse_args`/`main` are
gone. So the startup batch would pass an argument the target script can no longer
handle. Even setting aside the corruption, wiring an on-boot destructive cleaner
into a `schtasks ONLOGON` task is exactly the kind of persistence + side-effect
SockTuner deliberately avoids. Ignore.

## 8. `IMOD-Test.py` shipped broken — provenance/quality signal

The 5.0 `IMOD-Test.py` is a **truncated copy**: it ends mid-statement at line 916
(`print(f"[!]` with no closing quote — `SyntaxError: unterminated string
literal`), and the entire tail from 4.0 is missing: `parse_args`, `main`, and the
`if __name__ == "__main__"` entrypoint. The header was rebranded
(Noverse → "Jackpot's Hardware Register Engine") and an unused `glob` import
added, but the file cannot execute. The bundled `startup_log.txt` (paths under
`C:\Users\harald\Desktop\Jackpot Hardware Tuner PAID TOOL\`) and `crash_log.txt`
are leftovers from the author's own machine.

**Relevance to SockTuner:** none technically, but it's a useful reminder about
the corpus — these are hand-assembled, sometimes-broken release folders sold as a
"PAID TOOL", not a maintained codebase. Treat every extracted value/mechanism as
*a lead to verify against Microsoft/vendor docs*, never as a working reference.

## 9. Not new in 5.0 (already present in 4.0)

To avoid re-mining: `power/` (USB selective-suspend + `PnPCapabilities=24` +
`MSPower_DeviceEnable` power-management-off + `powercfg -DeviceDisableWake`),
`spowers/` (Sordum Switch-Power-Scheme), and the whole `xperf/` DPC/ISR tracing
tree all already shipped in 4.0 and are byte-identical here. The
`PnPCapabilities=24` NIC power lever is already documented as **UNSAFE** in
[JACKPOTS_ZENIT_NDIS_CANDIDATES.md](JACKPOTS_ZENIT_NDIS_CANDIDATES.md#c-latency-tweaks-flagged-unsafe);
the DPC/ISR measurement idea is covered in the
[4.0 delta §3](JACKPOTS_ZENIT_4.0_DELTA.md). No change in 5.0.

---

## Net takeaways for SockTuner (future, not now)

1. **No new networking capability.** 5.0 adds zero NIC/TCP/NDIS knobs. Everything
   new is GPU tweaks, a junk cleaner, a context-menu hack, and a software-download
   launcher — all out of scope.
2. **Architectural confirmation only.** The move to native `winreg` toggles (§1)
   validates SockTuner's in-process registry approach; SockTuner's read-back and
   no-frozen-artifact stance stays stricter. Nothing to import.
3. **Explicit rejects** to keep on record: the Deep Cleaner (§4), Take Ownership
   (§5), and the 53-URL Software Hub (§6) are anti-patterns that reinforce
   SockTuner's "no bundled binaries, no destructive system-wide cleaners, no
   download launcher" boundaries.
4. **Corpus quality caveat** (§8): this release ships a syntactically broken core
   script. Continue treating extracted mechanisms as leads to verify, not as
   working references.
