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
| `tabs` | Lists the navigation entries; `*` marks the selected one |
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

The 20 tabs, in five groups. `tabs` also prints the group headings — `OVERVIEW`,
`INVENTORY`, `MEASURE`, `ACT`, `RECORDS` — which are disabled `TabItem`s used as
navigation scenery. They are not selectable: `select "INVENTORY"` fails by design.

| Group | Tabs |
| --- | --- |
| OVERVIEW | `Dashboard` |
| INVENTORY | `Adapters`, `NDIS & drivers`, `Routes & DNS`, `Network profiles`, `Network bindings`, `Offloads`, `TCP settings`, `QoS policies`, `Winsock catalog` |
| MEASURE | `Gaming diagnostics`, `Throughput & bufferbloat`, `DNS resolvers` |
| ACT | `Interfaces`, `Recommendations`, `Interrupt affinity`, `Tuning plan` |
| RECORDS | `Tools & references`, `History & comparison`, `Preferences` |

Keyboard: **F5** refreshes the inventory, **Ctrl+F** focuses the global search, **Ctrl+K**
jumps to the section whose name matches what is typed there, and **Ctrl+1..9** select the
first nine selectable tabs. The window remembers its size and position between runs, unless
the saved position no longer lands on an attached monitor.

`Tuning plan` used to be last; it now sits with the other surfaces that act. The reference
links moved out of `Preferences` into `Tools & references`.

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

659 pass, 12 skipped. The 12 skipped are read-only live-inventory checks against the real
adapters and the two device-level settings; they are safe on a normal desktop and mutate
nothing:

```bash
SOCKTUNER_LIVE_INVENTORY=1 dotnet test SockTuner.sln
```

671 pass, 0 skipped.

## Never run these on a real machine

`--verify-tcp-writes` **writes to the live TCP stack** and `--verify-device-writes`
**enables and disables a real adapter, writes PnPCapabilities and creates a QoS policy**.
Both are gated behind `SOCKTUNER_VM_WRITE_TEST=1` so a mistyped `--probe` cannot trigger
them, and both belong in a disposable VM only. The driver deliberately exposes no command
for either.

### Running a write validation in the VM

`D:\VmLab\SockTuner-Win11-Base` is the guest. Checkpoint first, then:

```powershell
$vm   = Get-VM | ? Name -eq 'SockTuner-Win11-Base'      # -Name fails, see gotcha below
$cred = Get-Credential                                   # the guest account
$vm | Copy-VMFile -SourcePath <published exe> -DestinationPath 'C:\SockTunerValidate\SockTuner.exe' `
                  -CreateFullPath -FileSource Host -Force
Invoke-Command -VMId $vm.Id -Credential $cred -ScriptBlock {
    $env:SOCKTUNER_VM_WRITE_TEST = '1'
    Start-Process 'C:\SockTunerValidate\SockTuner.exe' -ArgumentList '--verify-device-writes'
    # then poll the guest Desktop for socktuner-device-write-verification-*.json
}
```

**Do not trust the report alone.** It only checks each setting's own value. Compare the
guest's registry and adapter state either side of the run: that is what caught an empty
`…\Windows\QoS` container being left behind by a rollback that the report called clean.

The guest needs a second network adapter (`Add-VMNetworkAdapter`) or the enable/disable
path is skipped — the run refuses to disable the adapter carrying the default route.

## Gotchas

- **`Start-Process`, never `& $exe` or `exe &`.** Launched as a child of the agent's shell,
  the app dies when that shell call returns — it disappears mid-session and looks like a
  crash. The driver's `launch` uses `Start-Process`.
- **Never click tabs by coordinate.** The navigation list scrolls, so coordinates read a
  moment earlier select the *wrong* tab, or miss the window entirely and land on whatever
  is behind it. The driver uses UIA `SelectionItemPattern.Select()` by name and is immune
  to this. Coordinate clicking failed three times in a row before the driver existed.
- **A background process cannot raise a window by asking.** `SetForegroundWindow` returns
  `true` and does nothing when the caller does not already own the foreground, so a shot
  silently captured whatever browser was on top instead. `driver.ps1 shot` now shares the
  input queue of the current foreground window for the length of the call
  (`AttachThreadInput`), retries once, and **throws** rather than saving a screenshot of the
  wrong window. If it throws, close or minimise what is covering the app.
- **Screenshot tooling downscales.** A desktop-capture MCP may return an image scaled from
  3640x2144 to ~1833x1080, so coordinates read off that image are wrong by a ~1.99 factor.
  `driver.ps1 shot` captures the window rect at native size and sidesteps this.
- **`--probe` used to end on a modal.** It now attaches to the calling console, prints the
  report path there and exits on its own; the message box is kept only for someone who
  double-clicked the exe and has no console to read. The driver still waits for the file
  before returning, which is what makes it reliable, but it no longer has to kill a process
  left holding a dialog nobody could dismiss.
- **Probe takes longer than you expect.** A full inventory capture exceeded 6 s here, so a
  fixed sleep is not enough; the driver polls for up to 120 s.
- **`pwsh` 7 cannot load `UIAutomationClient`.** Use `powershell.exe`.
- **A stale VM registration breaks every `-Name` Hyper-V cmdlet on this host.** `Get-VM`,
  `Checkpoint-VM -Name`, `Copy-VMFile -Name` and friends enumerate all VMs first, hit the
  broken registration and throw "the object was not found" — while still returning the good
  VMs. Get the object and pipe it instead: `Get-VM | ? Name -eq '…' | Copy-VMFile …`.
- **PowerShell Direct runs over VMBus, not the network.** That is what makes it safe to
  disable an adapter in the guest from the host: the session survives losing all guest
  networking. It does need guest credentials; there is no passwordless path.
- **A console-mode run has no console over PowerShell Direct.** `--probe` and the verify
  modes attach to the caller's console when there is one and fall back to a message box when
  there is not — and over PSDirect there is not, so the modal blocks forever. Start the
  process detached and poll for the JSON, which is written before the message appears.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `SockTuner window not found. Run "launch" first.` | The app is not running, or it was started as a shell child and already died. Re-run `launch`. |
| `Not built. Run: driver.ps1 build` | The Release exe is missing. Run `build`. |
| `No tab matching '<x>'` | Run `tabs` for the exact names; matching is substring, case-insensitive. |
| `Probe produced no JSON on the Desktop within 120s.` | Check for a stuck `SockTuner probe` process: `Get-Process SockTuner`. Kill it and retry. |
| `Could not raise the SockTuner window; another window is on top…` | Something is covering the app and Windows refused the foreground handover. Minimise it, or click the SockTuner window once, then re-run `shot`. |
| A leftover window titled `SockTuner probe` | An earlier probe attempt was interrupted. `powershell.exe -NoProfile -File .claude/skills/run-socktuner/driver.ps1 stop` clears all SockTuner processes. |
