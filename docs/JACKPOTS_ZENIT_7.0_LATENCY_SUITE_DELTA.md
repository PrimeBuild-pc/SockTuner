# Reference notes: Jackpot Latency Suite / Engine 7.0 — delta vs Zenit 5.3

> **Provenance and status.** Fifth note in the Zenit series, after
> [JACKPOTS_ZENIT_5.3_DELTA.md](JACKPOTS_ZENIT_5.3_DELTA.md). The new folder is
> `research/tools/Zenit/Zenit - Jackpot Latency Suite/`; its English manual identifies the
> product as "Jackpot's Hardware Register Engine v7.0". It was compared statically with
> `Zenit - 5.3/Zenit - 5.2/`. Nothing under `research/` was executed.
> The later local Engine v8.0 delta and final disposition are preserved in
> [`RESEARCH_ARCHIVE.md` §5](RESEARCH_ARCHIVE.md#5-delta-finale-zenit-latency-suite--engine-v80).
>
> **Nothing here is a verified recommendation and none of the code should be copied.** The
> useful result is a product lead to implement through SockTuner's existing native, typed,
> transactional path.

## TL;DR

| 7.0 change | Useful to SockTuner? |
|---|---|
| New `networktweaker/`: editable RSS profile, CPU range, processor count and receive queues | **Yes, one real gap.** SockTuner already reads this state from `MSFT_NetAdapterRssSettingData`; consider a native typed RSS setting after a read-only inventory increment and VM validation |
| Generic RSS presets for 4/6/8-core, HEDT and NUMA machines | **No.** They guess processor placement from core count and ignore processor groups, hybrid topology, actual indirection table, NIC locality and measured load |
| Data-driven registry tweak table, three-state “set/remove/unchanged” logic, JSONL audit and portable profiles | No new architecture. SockTuner already has catalogs, capability validation, exact snapshots, verification, audit and rollback; Zenit lacks the last four guarantees |
| `Nic.ps1` with PNP-to-driver resolution and per-write registry read-back | Principles already covered more safely. The same script also invents unsupported driver properties, disables bindings, resets Winsock/IP and cannot restore exact prior state |
| WinTweakVerifier v2 with Ghidra confidence tiers | **Research-only, still not evidence to import.** The shipped run has 0 dataflow-confirmed and 0 table-confirmed targets |
| AFD, global offload, interface and MSI controls | Already covered, intentionally deferred, or unsupported by evidence. No catalog addition justified |

## 1. The one adoption candidate: native RSS topology control

The new panel exposes six per-adapter RSS fields through `Set-NetAdapterRss`:

- enabled state;
- profile (`Closest`, `ClosestStatic`, `NUMA`, `NUMAStatic`, `Conservative`);
- base and maximum processor;
- maximum processors;
- number of receive queues.

SockTuner already reads those same fields in `WindowsOffloadInventory` from
`MSFT_NetAdapterRssSettingData`, but it does not expose that CIM instance as a writable typed
setting. NIC advanced properties such as `*RSS` and `*RSSProfile` are a related driver surface,
not a complete substitute for the operating-system RSS processor policy.

This is a legitimate Windows surface. Microsoft documents `Profile`, processor group/number,
`MaxProcessors`, `NumaNode` and `NumberOfReceiveQueues` as read/write properties of
[`MSFT_NetAdapterRssSettingData`](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/netadaptercimprov/msft-netadapterrsssettingdata),
and documents the meaning and trade-offs of the profiles in
[`Set-NetAdapterRss` guidance](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-choose-nic).

### Smallest safe SockTuner version

1. **Read-only first:** add the actual `IndirectionTable`, `RssProcessorArray`,
   `NumberOfInterruptMessages`, `MsiSupported`, `MsiXSupported` and `MsiXEnabled` fields already
   exposed by the CIM class. This shows what CPUs the NIC really uses instead of inferring it from
   the requested range.
2. **Then one typed composite setting:** snapshot and restore the complete RSS policy tuple for one
   exact adapter. Start with the documented profile enum; add manual processor placement only when
   the machine topology and processor groups can be validated.
3. Treat the operation as link-disruptive unless VM testing proves a narrower restart contract.
   Re-read both the requested fields and the resulting indirection table after apply and rollback.
4. Do not ship Zenit's queue-count fallback. If the provider rejects a queue count, report the
   unsupported value; never rewrite `Ndi\Params\*NumRssQueues\default` or manufacture enum entries.

This belongs behind the existing Step 7b hardware-validation gate. It is not needed for the
current Step 10 release work and should not expand the immediate queue without real demand.

## 2. Why the supplied RSS presets should not be copied

The presets are fixed JSON guesses such as “RSS on CPUs 2-7 with four queues”. That is not a
hardware capability model:

- the numbers are logical processors, not physical-core identities;
- processor groups are omitted, despite the backend and Windows API supporting them;
- hybrid P/E-core topology and SMT sibling placement are ignored;
- NIC NUMA locality and the live RSS processor array are ignored;
- no workload measurement selects between dynamic, static and conservative profiles;
- no driver-advertised queue maximum is checked.

The panel also omits profile value 6, `Balanced`, which SockTuner's existing formatter already
recognises. Microsoft documents it as a NetAdapterCx option on heterogeneous-CPU systems. Static
“4-core/8-core” presets would therefore make SockTuner less correct than its current inventory.

If RSS recommendations are added later, they should be derived from live topology plus measured
receive load, and remain editable proposals. A canned processor map is not a safe shortcut.

## 3. The Network Tweaker architecture is below SockTuner's current baseline

There are some sensible implementation ideas in isolation:

- one data table drives the form and write logic;
- removing a registry value represents “driver default”;
- each log row records previous, requested and result states;
- adapter and command output are parsed as structured JSON rather than display text.

SockTuner already has stronger versions of all four: setting catalogs/specifications, a fake-backed
transaction engine, capability-derived values, snapshot/read-back/exact rollback and an audit
store. The Zenit engine performs independent writes in sequence and reports “Apply Complete” even
after individual failures. It has no transaction boundary, stale-plan rejection, rollback or
post-write verification for the PowerShell/CIM paths.

The “portable profile” loader validates only a `kind` string and the presence of a dictionary. A
profile can carry arbitrary setting IDs and free-text values, after which “Apply all” processes
every category. If SockTuner ever adds user profile import, the file must remain only an untrusted
proposal that is re-resolved through the live typed capability set. There is no current need to add
that attack surface.

## 4. `Nic.ps1`: a better lookup wrapped around unsafe operations

The useful detail is the adapter lookup: it follows a PNP instance's `Driver` value to the real
network class key instead of assuming that a WMI `DeviceID` is the four-digit class subkey. The
new `networktweaker` backend still makes that unsafe assumption (`DeviceID -> NNNN`), while
SockTuner avoids both approaches for advanced properties by addressing the CIM instance with the
adapter GUID.

The script's central `Set-TypedRegistryValue` also reads each registry value back after writing.
That is good practice, but SockTuner already performs read-back inside a transaction and also owns
the snapshot and rollback that Zenit lacks.

The rest should stay rejected:

- it creates `Ndi\Params` definitions for `*UsoIPv4`, `*UsoIPv6`, `*UdpRsc`, `*RSS` and RSS queue
  values even when the driver did not advertise them;
- it writes fixed Intel/Realtek maps, including previously rejected `HwOption*`, `ThreadPoll`,
  `DisablePhyReset` and `PnPCapabilities` values;
- it forces RSS queue counts without checking hardware limits (16 in `Nic.ps1`, but the GUI's
  “unlock” invents only 1-12);
- it disables every adapter binding except IPv4/IPv6, including security, capture,
  virtualisation, discovery and sharing filters;
- it globally selects BBR2 from the OS build number instead of asking the live TCP provider what
  the build supports;
- it runs broad IP, TCP and Winsock resets after applying settings, then calls those resets a
  revert even though they cannot reproduce the captured pre-change state;
- its revert removes all present network devices with `pnputil` and rescans them;
- many `netsh`, cmdlet and direct `New-ItemProperty` writes bypass the checked helper, so the final
  success counter does not cover the whole operation.

There are also direct inconsistencies: the UI says it is enabling global RSC while the code passes
`Disabled`; the manual says the NIC operation uses `Disable-PnpDevice`/`Enable-PnpDevice`, while
the current paths use `Restart-NetAdapter`; and the manual claims `TCPNoDelay` is applied under a
global TCP key, while `Nic.ps1` does not set it in the apply path at all.

These are useful negative tests for SockTuner's existing rules, not feature leads.

## 5. WinTweakVerifier v2: improved labels, no new trustworthy result

Version 7.0 adds Ghidra result tiers for:

- a code reference in a function that calls a known registry API;
- claimed argument dataflow from the string to the call;
- a claimed `RTL_QUERY_REGISTRY_TABLE` structure match.

The distinction is directionally right. The shipped 42-target report, however, contains
**0 dataflow-confirmed and 0 table-confirmed targets**. Forty targets sit in the weaker
“same function calls a registry API” tier and two strings are absent. The current report is about
graphics-driver power values, not SockTuner's TCP/NDIS candidates, so it supplies no new catalog
evidence.

The new high-confidence implementations are also still heuristics:

- “dataflow” records that the string address was loaded and that a later call within a 140-
  instruction window targets a registry API; it does not track the destination register, argument
  register, clobbers or control-flow path;
- “confirmed table” checks only that the four bytes eight bytes before a string pointer decode to
  a small flags value; it does not validate the whole table entry, surrounding entries, terminator
  or the call that consumes the table;
- as before, a value-name match does not prove the registry key path, expected type, accepted
  range or performance effect.

Therefore the 5.3 conclusion is unchanged: use offline binary work as a cheap negative filter and
as a way to locate candidate consumers, never as an automatic approval gate. No SockTuner evidence
level should change from the supplied 7.0 results.

## 6. Remaining surfaces: no action

- **AFD values:** the panel republishes old buffer/window “magic values” without current Windows
  documentation, live capability metadata or benchmark evidence. Step 8 intentionally left these
  out; nothing here closes that evidence gap.
- **IP interface fields:** most are routing, discovery or lifetime semantics rather than latency
  controls. The generic UI assumes a returned property is safely writable and accepts numeric
  free text without local ranges. SockTuner's selected MTU and metric controls are the useful,
  reversible subset.
- **Global offloads:** already inventoried, and the provider-advertised writable subset already
  goes through `CimGlobalPropertyCatalog`. Network Direct remains irrelevant to a normal gaming
  desktop and has no new justification here.
- **MSI mode:** forcing `MSISupported=1` is not new and still lacks a capability-safe write model.
  The read-only MSI/MSI-X fields on the RSS CIM class are useful inventory; interrupt placement is
  already handled by SockTuner's typed affinity setting.
- **Custom profile sharing:** potentially pleasant UX, but not needed for the current release and
  not worth adding untrusted-plan import until users ask for it.

## Net recommendation

Keep exactly one item from this release on the future list:

> **Complete RSS inventory, then consider a typed native RSS profile/topology transaction using
> `MSFT_NetAdapterRssSettingData`.**

Everything else is already present in a safer form, outside SockTuner's scope, or unsupported by
the evidence shipped with the suite. Do not copy the generic presets, registry tables, queue
“unlock”, WinTweakVerifier verdicts or `Nic.ps1` apply/revert workflow.
