# Presets

A preset is a list of changes SockTuner **proposes**. Nothing is written until you preview the plan, confirm it
and accept the one-time consent; every change is snapshotted, read back and can be undone.

A change enters a preset only if Windows or the driver actually reads it and it plausibly changes latency or
jitter. After applying, "Did it help?" re-runs the same check so you can see whether it did.

## Competitive gaming

Trades CPU, some peak throughput and idle power for lower latency and jitter.

| Where | What | Why |
| --- | --- | --- |
| Adapter | Interrupt moderation off, receive coalescing (RSC) off, flow control off | Packets are handed up as they arrive instead of batched; no pause frames stalling the queue. |
| Adapter | Energy-efficient Ethernet, Green Ethernet, power-saving modes, selective suspend, power gating off | A sleeping link or controller delays the first packet after a pause. |
| Wi-Fi adapter | Power save off, U-APSD off, MIMO power save off | The radio no longer dozes between beacons. |
| Windows | Global receive coalescing off | Holds even when the driver exposes no keyword. |
| Windows | Multimedia scheduler: network throttling off, system responsiveness 10 | Documented MMCSS values. |
| Device | Power management off (no "turn off to save power", no wake) | Windows stops powering the adapter down. Ends wake-on-LAN for it. |

A vendor-specific keyword is set only when the driver advertises it and itself labels the target value "Disabled",
"Off", "Disabilitato" and so on. A driver that labels it differently is skipped, not inverted. A test checks the
list against every driver in `alpha-tester-output/`.

## Calls and remote work

Consistency first: flow control off and the same power-saving removals as above (Ethernet and Wi-Fi), device power
management off. Interrupt moderation stays adaptive.

## Streaming and upload

Throughput first: receive coalescing and large-send offload on.

## Left out on purpose

These do not lower game latency on current Windows, so no preset ships them: TCP auto-tuning off (caps download
speed), TCP timestamps, ECN, `Tcp1323Opts`, `GlobalMaxTcpWindowSize`, the "reservable bandwidth" limit, QoS
packet-scheduler tweaks, `TCPNoDelay` (unverified), `TcpAckFrequency`/delayed ACK (TCP only, experimental).

Considered from the reference tweaker script and not included: AFD buffer sizes and thresholds, RSS base processor
and queue counts, `NetworkDirect` switches. Their effect is unproven; they would be manual, advanced-only options
with a warning, never part of a preset. Not built yet.
