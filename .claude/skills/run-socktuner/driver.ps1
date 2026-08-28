<#
    SockTuner agent driver.

    A WPF app has no DOM and no CDP, so this drives it through Windows UI Automation:
    tabs are selected by NAME, never by screen coordinate. That matters — the navigation
    list scrolls, so coordinates captured a moment ago silently select the wrong tab or
    land on whatever window is behind.

    Run under Windows PowerShell 5.1 (powershell.exe), not pwsh 7: UIAutomationClient is
    a .NET Framework assembly.

        powershell.exe -NoProfile -File .claude\skills\run-socktuner\driver.ps1 <command> [arg]
#>
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$Arg
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$Exe = Join-Path $Root 'src\SockTuner\bin\Release\net10.0-windows\SockTuner.exe'
$ShotDir = Join-Path $Root '.claude\skills\run-socktuner\shots'

function Load-Uia {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing | Out-Null
    if (-not ('Win32Rect' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32Rect {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
}
'@
    }
}

function Get-Window {
    param([int]$TimeoutSeconds = 30)
    Load-Uia
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'SockTuner')
        $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $cond)
        if ($win) { return $win }
        Start-Sleep -Milliseconds 400
    }
    throw 'SockTuner window not found. Run "launch" first.'
}

function Get-Tabs {
    param($Window)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    return $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

switch ($Command) {

    'build' {
        & dotnet build (Join-Path $Root 'src\SockTuner\SockTuner.csproj') -c Release --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
        "OK build -> $Exe"
    }

    # Start-Process, never "& $Exe" or a background job: a child of the agent's shell dies
    # when that shell call returns, and the app vanishes mid-session.
    'launch' {
        if (-not (Test-Path $Exe)) { throw "Not built. Run: driver.ps1 build" }
        if (Get-Process SockTuner -ErrorAction SilentlyContinue) { "already running"; break }
        Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe)
        $win = Get-Window
        $p = Get-Process SockTuner
        "OK launched pid=$($p.Id) window='$($win.Current.Name)'"
    }

    'status' {
        $p = Get-Process SockTuner -ErrorAction SilentlyContinue
        if ($p) { "running pid=$($p.Id) responding=$($p.Responding) ram=$([math]::Round($p.WorkingSet64/1MB,1))MB" }
        else { 'not running' }
    }

    'tabs' {
        foreach ($t in (Get-Tabs (Get-Window))) {
            $sel = $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $mark = if ($sel.Current.IsSelected) { '*' } else { ' ' }
            "$mark $($t.Current.Name)"
        }
    }

    # Selects by name through the SelectionItemPattern. Immune to the scrolling nav list.
    'select' {
        if (-not $Arg) { throw 'Usage: driver.ps1 select "<tab name>"' }
        $win = Get-Window
        $match = $null
        foreach ($t in (Get-Tabs $win)) {
            if ($t.Current.Name -like "*$Arg*") { $match = $t; break }
        }
        if (-not $match) { throw "No tab matching '$Arg'. Run: driver.ps1 tabs" }
        $match.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 700
        "OK selected '$($match.Current.Name)'"
    }

    'shot' {
        $win = Get-Window
        $h = [IntPtr]$win.Current.NativeWindowHandle
        [void][Win32Rect]::ShowWindow($h, 9)          # SW_RESTORE
        [void][Win32Rect]::SetForegroundWindow($h)
        Start-Sleep -Milliseconds 600
        $r = New-Object Win32Rect+RECT
        [void][Win32Rect]::GetWindowRect($h, [ref]$r)
        $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
        New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
        $path = if ($Arg) { $Arg } else { Join-Path $ShotDir ("socktuner-{0:yyyyMMdd-HHmmss}.png" -f (Get-Date)) }
        $bmp = New-Object System.Drawing.Bitmap $w, $ht
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        "OK shot ${w}x${ht} -> $path"
    }

    # Read-only inventory dump to JSON. Ends on a modal this driver cannot click, so the
    # process is stopped once the file lands. Nothing on the machine is changed.
    'probe' {
        if (-not (Test-Path $Exe)) { throw "Not built. Run: driver.ps1 build" }
        $before = Get-Date
        $desktop = [Environment]::GetFolderPath('Desktop')
        $proc = Start-Process -FilePath $Exe -ArgumentList '--probe' -WorkingDirectory (Split-Path $Exe) -PassThru
        # The JSON is written BEFORE the modal appears, so wait for the file and then close
        # that exact process. Do not try to click the dialog: it is a Win32 #32770 whose
        # buttons this build does not expose to UI Automation, so a UIA click never lands
        # and the process is left alive holding a modal on the user's desktop.
        $json = $null
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 750
            $json = Get-ChildItem $desktop -Filter 'socktuner-probe-*.json' -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -ge $before } |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($json) { break }
            if ($proc.HasExited) { break }
        }
        if (-not $json) { throw 'Probe produced no JSON on the Desktop within 120s.' }
        Start-Sleep -Milliseconds 500
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        "OK probe -> $($json.FullName) ($([math]::Round($json.Length/1KB,1)) KB)"
    }

    'stop' {
        Get-Process SockTuner -ErrorAction SilentlyContinue | Stop-Process -Force
        'OK stopped'
    }

    default {
@'
SockTuner driver — commands:
  build            dotnet build Release
  launch           start detached, wait for the window
  status           pid / responding / RAM
  tabs             list navigation tabs, * marks the selected one
  select "<name>"  select a tab by name (substring match)
  shot [path]      PNG of the window -> .claude/skills/run-socktuner/shots/
  probe            --probe read-only inventory dump to JSON on the Desktop
  stop             kill the process
'@
    }
}
