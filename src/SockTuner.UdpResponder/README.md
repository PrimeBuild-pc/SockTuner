# SockTuner UDP quality responder

This companion runs on a second computer you choose. The WPF app's **UDP quality** page measures the path to it. A responder on your LAN tests that local path; an Internet measurement needs a responder outside your home network. No hosted service or Packet Loss Test source code is used.

Build with .NET 10:

```powershell
dotnet build src/SockTuner.UdpResponder -c Release
```

For a Windows computer without .NET installed, publish a separate self-contained executable:

```powershell
dotnet publish src/SockTuner.UdpResponder -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/udp-responder
```

Create a random shared key file using PowerShell 7. Keep it private and exchange it with the testing user through a trusted channel:

```powershell
$keyFile = Join-Path $env:LOCALAPPDATA 'SockTuner-udp.key'
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) |
    Set-Content -LiteralPath $keyFile -NoNewline
```

On the responder computer, choose its local IPv4 address and port explicitly:

```powershell
.\SockTuner.UdpResponder.exe --listen 192.0.2.20 --port 52345 --key-file "$keyFile" --seconds 600
```

`192.0.2.20` is an example: replace it with the responder's actual address. The default bind is loopback. Permit inbound UDP on the chosen port only from the testing computer, using your existing firewall/network management. The program does not create firewall rules, port forwards or an always-on service. It stops at the requested lifetime (at most one hour) or Ctrl+C.

When launching through Windows OpenSSH, keep the foreground server session open or use a task/service configured to outlive the SSH command. A background child launched with `Start-Process` can be terminated when that SSH command closes. For a temporary scheduled task, remove the task, its scoped firewall rule and its key file after testing.

In SockTuner, enable **Advanced mode**, open **UDP quality**, and enter the responder address, port and the file's 64-character key. The default run sends 600 packets over approximately 30 seconds at 20 packets/second. Save the completed run as a baseline before applying a preset; **Repeat and compare** restores the parameters and asks whether background traffic and network conditions match. Writes from the app are disabled while a UDP measurement is running.

The companion can also run the same client for automation; standard output is a JSON report without the key:

```powershell
.\SockTuner.UdpResponder.exe --test 192.0.2.20 --port 52345 --key-file "$keyFile" --seconds 30
```

## What is measured

- Upload loss: probes sent by the client but absent from the responder's authenticated receipt bitmap.
- Download loss: unique probes received by the responder whose echoes were absent from the client by the measurement cutoff. Its denominator is the number received by the responder, not all client sends. With no received probes, download loss is unmeasured.
- RTT median, P95 and jitter (mean absolute difference between successive unique echo RTTs in receive order), measured using a monotonic local clock. RTT is not one-way latency.
- Late echoes, duplicates, reordering and sends delayed by local scheduling. Late delivered packets are distinct from missing packets. Collection waits two seconds after the send phase; packets arriving after the cutoff may count as missing.
- Packet size is the UDP payload length, including the protocol header and authentication tag, excluding UDP/IP/link headers.

The `STQ1` protocol authenticates messages with HMAC-SHA256 and uses a fresh session ID per measurement. The responder binds each session to its initiating endpoint, limits sessions and replies, and never replies with more bytes than it received. Final receipt summaries are frozen and retried; a missing or inconsistent summary fails the measurement instead of inventing direction-specific loss.

The before/after verdict uses heuristic noise floors and matching responder/options/interface context, plus the user's confirmation of matching external conditions. It is an observed comparison, not statistical certification or proof of causation. Check CPU use and sustained throughput separately when testing offload or interrupt changes. No preset is applied, retained or rolled back automatically based on this result.

## Experimental presets and power controls

The balanced gaming preset uses supported power controls and preserves wake-on-LAN. The aggressive preset adds interrupt moderation, receive coalescing and LSO trials and selected Intel/Realtek templates. Unarchived templates are marked experimental and still require live enumerations and supported values. AutoDisableGigabit, ULPMode and SipsEnabled are proposed only when the driver's label agrees with the requested off value. EnablePME remains an experimental manual wake control, outside the gaming presets.

New power plans use advertised `*SelectiveSuspend` or `*IdleRestriction` where available. The legacy `PnPCapabilities` backend remains for old audit recovery; a matching stored DWORD is not evidence of its operational effect on a modern driver. Aggressive profile provenance is retained through preview, audit and the explicit confirmation step. Rollback restores recorded values rather than resetting unrelated settings.
