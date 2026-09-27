# ADR 0001: Native passive Wi‑Fi diagnostics

- Status: Accepted
- Date: 2025-09-27

## Context

SockTuner needs Wi‑Fi evidence relevant to gaming latency, jitter and stability. The application already
uses supported Windows inventory and diagnosis APIs and must remain usable without extra hardware,
drivers, elevation or offensive wireless capabilities.

wifit3 was evaluated at v0.3.4 (`236481b07249db92a64cd94ba6f13332be550dca`). Its current CLI has no
headless passive scan, stable JSON/CSV protocol or device-list API. Its TUI may enable WPS PushButton
auto-invade by default. Windows setup can install an unsigned WinUSB helper with UAC, and redistribution
would add GPL-2.0-only, firmware, signing and supply-chain obligations.

## Decision

The primary Wi‑Fi path uses only native Windows WLAN API data:

- enumerate all WLAN interfaces;
- read current association, PHY, rates, authentication/cipher and radio/capability state;
- inspect the cached BSS list without calling `WlanScan`;
- bounds-check HT/VHT/HE, RSN/PMF, BSS Load and WPS information elements;
- correlate passive radio facts with gateway probes and adapter counter deltas in a pure engine;
- optionally observe the current association for 60 seconds, at a bounded interval, with cancellation;
- expose permission denial, partial data and unknowns instead of guessing;
- redact SSID, BSSID and interface identity from support exports.

No plugin/provider framework is introduced. WPF remains the presentation layer and no UI dependency is
added.

## Consequences

### Positive

- no extra hardware, runtime, driver, elevation or license obligation;
- deterministic and unit-testable diagnosis;
- no active scan interruption;
- evidence can join existing gaming reports, history, recommendations and redacted exports;
- Windows location privacy failures have a clear recovery path.

### Limits

- the BSS list is a Windows cache and its age is not reliably exposed;
- RSSI and negotiated rate are not throughput;
- nearby BSS count is potential channel pressure, not airtime usage;
- monitor mode, frame retries, universal noise/SNR and per-client airtime are unavailable;
- HE width parsing is implemented only where the documented 6 GHz operation field is present;
- EHT presence is retained, but EHT width is not guessed without a stable driver-independent contract.

## External-tool boundary

wifit3 is not bundled, imported or used as a data provider. A future independent launcher may be reviewed
only if all of the following exist:

1. a guaranteed RX-only mode;
2. a documented non-interactive command;
3. versioned JSON output and defined exit codes;
4. no implicit WPS/deauthentication/injection behavior;
5. separate legal, firmware, driver-signing and supply-chain approval.

Until then, Tools & references may link to documentation, but SockTuner does not execute or parse wifit3.
