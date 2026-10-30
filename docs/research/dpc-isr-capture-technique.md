# Research: ETW DPC/ISR capture technique and per-core CPU usage reader

Resolves [#49](https://github.com/PrimeBuild-pc/SockTuner/issues/49), a research child of the
[DPC/ISR latency diagnosis map (#32)](https://github.com/PrimeBuild-pc/SockTuner/issues/32).

> [!NOTE]
> Historical research for #49, written before the alpha.7 implementation. This is not a
> description of the current implementation. Alpha.7 wrote a temporary `.etl` and compared
> activity with earlier jitter measurements. Alpha.8 keeps the temporary trace but adds
> simultaneous, repeated latency windows and separates driver cleanup from latency evidence.
> See the revised [host-diagnostics specification](../HOST-DIAGNOSTICS.md) for accepted
> behavior and safeguards; #49 remains a record of the initial research.

## 1. DPC/ISR capture

**Recommendation: `Microsoft.Diagnostics.Tracing.TraceEvent` (in-process ETW session), not
`wpr.exe`/`xperf` as an external process.**

TraceEvent lets SockTuner start a kernel trace session, read events, and stop it all inside the
elevated worker's own process lifetime — matching the bounded-window, on-demand model (map
decision Q6). `wpr.exe`/`xperf` would mean spawning a process, waiting for it to finish writing a
`.etl` file, then parsing that file with the same TraceEvent library anyway (or another ETW
reader) — an extra process hop and a temp file for no benefit, since TraceEvent can read the
kernel session live.

- **Session API**: `TraceEventSession.EnableKernelProvider(KernelTraceEventParser.Keywords flags)`.
  This talks to the classic **NT Kernel Logger** session — confirmed a Windows-wide singleton by
  Microsoft Learn: "There is only one NT Kernel Logger session... If a kernel logger session is
  already running, `StartTrace` returns `ERROR_ALREADY_EXISTS`."
  ([learn.microsoft.com/windows/win32/etw/configuring-and-starting-the-nt-kernel-logger-session](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-the-nt-kernel-logger-session))
- **Providers/keywords**: DPC/ISR events are part of the kernel `PerfInfo` event class — event
  types 66 (threaded DPC), 67 (ISR), 68 (DPC), 69 (DPC timer), enabled via the
  `EVENT_TRACE_FLAG_DPC` / `EVENT_TRACE_FLAG_INTERRUPT` kernel flags
  ([learn.microsoft.com/windows/win32/etw/perfinfo](https://learn.microsoft.com/en-ie/Windows/Win32/etw/perfinfo)).
  In TraceEvent, the matching flags are exposed as `KernelTraceEventParser.Keywords` members
  (folded into the composite `OS` keyword group) — read the exact enum names/values from the
  installed NuGet package rather than hardcoding them here, since the public docs don't pin the
  numeric values.
- **Privilege**: TraceEvent's own source
  (`TraceEventSession.cs`, [github.com/microsoft/perfview](https://github.com/microsoft/perfview))
  throws "Access Denied (Administrator rights required to start ETW)" on failure to start a
  kernel provider — i.e. **Administrator group membership is sufficient**. This fits inside
  SockTuner's existing elevated-worker boundary (`ElevatedWorkerClient.Launch`, `Verb = "runas"`
  over the named pipe in `src/SockTuner/Services/ElevatedWorkerClient.cs`) with no extra
  elevation path needed. The separate `Profile` keyword (CPU sampling) additionally needs
  `SeSystemProfilePrivilege` enabled in code — not required for DPC/Interrupt, so it's out of
  scope unless a later ticket adds CPU-sampling profiling.
- **Gotcha — kernel session exclusivity**: because the NT Kernel Logger is a system-wide
  singleton, if the user is already running their own WPR/xperf/other ETW kernel session when
  SockTuner's worker tries to start one, `EnableKernelProvider` will fail
  (`ERROR_ALREADY_EXISTS`). There is no sharing mechanism — SockTuner must catch this and surface
  a clear "another trace tool is already running" message, not retry silently or crash.

## 2. Per-core CPU usage reader

**Recommendation: CIM/WMI `Win32_PerfFormattedData_PerfOS_Processor` via
`System.Management.ManagementObjectSearcher`** — consistent with the existing CIM-reading pattern
already in this codebase (`CimAdapterSettingStore.cs`'s `ManagementScope`-based queries), not
`System.Diagnostics.PerformanceCounter`.

- `Win32_PerfFormattedData_PerfOS_Processor` returns one instance per logical core plus a
  `_Total` row; `PercentProcessorTime` is already rate-calculated server-side by WMI's formatted
  provider, so **a single query returns a valid, fresh value with no priming read** — important
  because the diagnostic window is short and on-demand (map decision Q6), not a continuous
  poller. Source:
  [learn.microsoft.com/windows/win32/wmisdk/monitoring-performance-data](https://learn.microsoft.com/en-us/windows/win32/wmisdk/monitoring-performance-data)
  — "formatted" (`PerfFormattedData_*`) classes come precalculated, vs. the raw `PerfRawData_*`
  classes which need manual rate math between two samples.
- `System.Diagnostics.PerformanceCounter` ("Processor" category, "% Processor Time" counter, one
  instance per core number) is viable but worse for this use case: its first `NextValue()` call
  always returns 0 and needs a second read roughly a second later for a real value (confirmed by
  Microsoft's own performance-counter guidance) — an avoidable delay inside a bounded capture
  window that the WMI formatted class doesn't have.
- **Elevation**: neither approach needs Administrator rights for a local, interactive read of
  standard `root\cimv2` classes; it can run from the same process as the rest of the UI if wanted,
  though for this feature it will run from inside the already-elevated worker anyway.

## Do the two capture windows share one session or run independently?

They are independent reads and should stay that way: the ETW kernel session captures DPC/ISR
events as a time series over the whole window, while the per-core CPU reader is a single
point-in-time WMI query used to pick/confirm a target core for the affinity recommendation
(map decision Q11). There's no API-level reason to interleave them — the diagnosis service can
start the ETW session, take one or more CIM CPU snapshots at convenient points during the same
wall-clock window, and stop the ETW session; no shared handle or session object is needed between
the two.

## Sources

- [Configuring and Starting the NT Kernel Logger Session](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-the-nt-kernel-logger-session) — Microsoft Learn (kernel session singleton)
- [PERFINFO (PerfInfo event class, EVENT_TRACE_FLAG_DPC / EVENT_TRACE_FLAG_INTERRUPT)](https://learn.microsoft.com/en-ie/Windows/Win32/etw/perfinfo) — Microsoft Learn
- [Microsoft.Diagnostics.Tracing.TraceEvent / PerfView source](https://github.com/microsoft/perfview) — `TraceEventSession.cs` (admin-rights error message, `EnableKernelProvider`)
- [Monitoring Performance Data (WMI, PerfFormattedData vs PerfRawData)](https://learn.microsoft.com/en-us/windows/win32/wmisdk/monitoring-performance-data) — Microsoft Learn
- Microsoft performance-counter guidance on `PerformanceCounter.NextValue()` returning 0 on the first call (Ryan Byington, archived MS guidance referenced via Microsoft Learn's PerformanceCounter docs)
- krabsetw `EtwPrimer.md` — corroborating note (C++ library, not authoritative for .NET privilege behavior) that kernel ETW sessions require Administrator rights and are capped at 8 concurrent sessions system-wide

## Codebase references used

- `src/SockTuner/Services/ElevatedWorkerClient.cs` — existing `runas` + named-pipe elevation boundary
- `src/SockTuner/Services/CimAdapterSettingStore.cs` — existing `System.Management`/`ManagementScope` CIM pattern
- `src/SockTuner/Views/InterruptAffinityView.xaml.cs` — confirms only manual core *selection* exists today, no usage reader
