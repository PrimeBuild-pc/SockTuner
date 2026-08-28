---
name: run-socktuner
description: Build, launch, drive, screenshot and probe the SockTuner WPF desktop app on Windows. Use when asked to run, start, open, screenshot, inspect the UI of, or capture an inventory from SockTuner, or to verify a change in the real app rather than in tests.
---

# Run SockTuner

SockTuner is a **WPF desktop app on Windows** (.NET 10, `net10.0-windows`). There is no
DOM and no CDP, so it is driven through **Windows UI Automation** by
[`driver.ps1`](driver.ps1) — tabs are selected **by name**, never by screen coordinate.

All paths below are relative to the repo root. The driver must run under **Windows
PowerShell 5.1 (`powershell.exe`)**, not `pwsh` 7: `UIAutomationClient` is a .NET
Framework assembly.

## Prerequisites

The .NET 10 SDK, already present if `dotnet build` works. Nothing to `apt-get` — this is
Windows, and the driver uses only in-box assemblies (`UIAutomationClient`,
`UIAutomationTypes`, `System.Drawing`, `user32.dll`).

## Agent path — the driver

```bash
powershell.exe -NoProfile -File .claude/skills/run-socktuner/driver.ps1 <command> [arg]
```

| Command | What it does |
| --- | --- |
| `build` | `dotnet build src/SockTuner/SockTuner.csproj -c Release` |
| `launch` | Starts the app **detached** and waits for its window |
| `status` | pid / responding / RAM |
| `tabs` | Lists the 14 navigation tabs; `*` marks the selected one |
| `select "<name>"` | Selects a tab by name (substring match) |
| `shot [path]` | PNG of the window → `.claude/skills/run-socktuner/shots/` |
| `probe` | Read-only inventory dump to JSON on the Desktop |
| `stop` | Kills the process |

A full verified cycle:

```bash
D=.claude/skills/run-socktuner/driver.ps1
powershell.exe -NoProfile -File $D build
powershell.exe -NoProfile -File $D launch
powershell.exe -NoProfile -File $D select "NDIS"
powershell.exe -NoProfile -File $D shot
powershell.exe -NoProfile -File $D stop
```

Observed output of that sequence:

```text
OK build -> C:\...\src\SockTuner\bin\Release\net10.0-windows\SockTuner.exe
OK launched pid=8456 window='SockTuner'
OK selected 'NDIS & drivers'
OK shot 1320x820 -> C:\...\.claude\skills\run-socktuner\shots\socktuner-20260828-022129.png
OK stopped
```

**Read the screenshot back** with the Read tool. A blank frame means the window never
painted. A correct `NDIS & drivers` capture shows *"Driver-advertised NDIS properties"*
and a grid of real keywords (`*InterruptModeration`, `*EEE`, `ITR`, `*FlowControl`,
`*JumboPacket`) with current/default values.

The 14 tabs: `Dashboard`, `Adapters`, `NDIS & drivers`, `Routes & DNS`,
`Network profiles`, `Network bindings`, `Offloads`, `TCP settings`, `QoS policies`,
`Winsock catalog`, `Gaming diagnostics`, `History & comparison`, `Preferences`,
`Tuning plan`.

## Inventory without the GUI

`probe` runs the app's own `--probe` mode: a read-only capture of the whole inventory,
redacted (`machineName` masked, no addresses), written as JSON to the Desktop. It changes
nothing on the machine. This is the fastest way to inspect what the app *sees* without
touching the UI:

```bash
powershell.exe -NoProfile -File .claude/skills/run-socktuner/driver.ps1 probe
# OK probe -> C:\Users\<user>\Desktop\socktuner-probe-20260828-022443.json (410.4 KB)
```

Committed probe corpora live in `alpha-tester-output/`.

## Tests

```bash
dotnet test SockTuner.sln
```

480 pass, 7 skipped. The 7 skipped are read-only live-inventory checks against the real
adapters; they are safe on a normal desktop and mutate nothing:

```bash
SOCKTUNER_LIVE_INVENTORY=1 dotnet test SockTuner.sln
```

480 pass, 0 skipped.

## Never run this on a real machine

`SockTuner.exe --verify-tcp-writes` **writes to the live TCP stack**. It is gated behind
`SOCKTUNER_VM_WRITE_TEST=1` precisely so a mistyped `--probe` cannot trigger it. It
belongs in a disposable VM only (Hyper-V images live under `D:\VmLab`). The driver
deliberately exposes no command for it.

## Gotchas

- **`Start-Process`, never `& $exe` or `exe &`.** Launched as a child of the agent's shell,
  the app dies when that shell call returns — it disappears mid-session and looks like a
  crash. The driver's `launch` uses `Start-Process`.
- **Never click tabs by coordinate.** The navigation list scrolls, so coordinates read a
  moment earlier select the *wrong* tab, or miss the window entirely and land on whatever
  is behind it. The driver uses UIA `SelectionItemPattern.Select()` by name and is immune
  to this. Coordinate clicking failed three times in a row before the driver existed.
- **Screenshot tooling downscales.** A desktop-capture MCP may return an image scaled from
  3640x2144 to ~1833x1080, so coordinates read off that image are wrong by a ~1.99 factor.
  `driver.ps1 shot` captures the window rect at native size and sidesteps this.
- **`--probe` ends on a modal.** The JSON is written *before* the message box appears, and
  the box is a Win32 `#32770` whose buttons this build does not expose to UI Automation —
  a UIA click never lands and the process is left holding a modal on the user's desktop.
  The driver waits for the file, then stops that specific process.
- **Probe takes longer than you expect.** A full inventory capture exceeded 6 s here, so a
  fixed sleep is not enough; the driver polls for up to 120 s.
- **`pwsh` 7 cannot load `UIAutomationClient`.** Use `powershell.exe`.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `SockTuner window not found. Run "launch" first.` | The app is not running, or it was started as a shell child and already died. Re-run `launch`. |
| `Not built. Run: driver.ps1 build` | The Release exe is missing. Run `build`. |
| `No tab matching '<x>'` | Run `tabs` for the exact names; matching is substring, case-insensitive. |
| `Probe produced no JSON on the Desktop within 120s.` | Check for a stuck `SockTuner probe` process: `Get-Process SockTuner`. Kill it and retry. |
| A leftover window titled `SockTuner probe` | An earlier probe attempt was interrupted. `powershell.exe -NoProfile -File .claude/skills/run-socktuner/driver.ps1 stop` clears all SockTuner processes. |
