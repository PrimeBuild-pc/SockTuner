# Changelog

## 0.9.0-beta.2 — 2026-11-07

- Add native authenticated UDP quality measurements with separate upload/download loss, RTT median/P95, jitter, late echoes, duplicates, reordering and local scheduling delays.
- Add baseline/repeat comparison and JSON export without the responder key. Disable tuning writes while UDP measurements are running.
- Include a self-contained Windows x64 UDP responder in the portable package under `UdpResponder/`, with setup instructions and a command-line client.
- Add a balanced gaming preset and mark the aggressive gaming preset as experimental, including its provenance through preview, confirmation and audit.
- Offer selected unarchived Intel/Realtek templates only with live driver constraints and experimental warnings; hardware verification is not inferred from a template.
- Replace automatic legacy `PnPCapabilities=24` proposals with driver-advertised power controls, preserving wake-on-LAN. Retain the legacy backend for existing audit recovery.
- Keep EnablePME as a separate experimental manual wake control. Offload changes remain explicit trials; comparisons do not certify performance gains.

Validation: 989 .NET tests passed with 14 pre-existing skips, native Windows UI smoke tests, protocol loss/authentication/cancellation tests, and two real PC-to-Windows-VM LAN runs of 600 packets each with no observed loss. These results do not establish Internet-path quality or tuning benefits on physical NICs.

This release remains an unsigned community beta. Existing settings are preserved on upgrade; changing the selected preset does not reset previous settings. Use recorded audit rollback when reverting applied changes. The responder requires a second endpoint selected by the user and does not automatically open firewall ports.

## 0.9.0-beta.1 — 2026-11-06

- Add automatic diagnosis, Windows-selected interface context and individual guided changes.
- Require characterised hardware and matching live constraints for ordinary NIC writes.
- Strengthen before/after comparisons and dedicated worker backend validation.

See [the beta.1 release](https://github.com/PrimeBuild-pc/SockTuner/releases/tag/v0.9.0-beta.1) for its full notes.
