# GitHub Repository Metadata

## Repository name

`SockTuner`

## Short description

> Advanced all-in-one network tuning and diagnostics for Windows 10 and 11, with transparent TCP/IP, Winsock, NIC driver, MTU, QoS, offload, RSS, interrupt, testing, and rollback controls.

## Topics

```text
windows-network-suite
packet-optimization
jitter-reduction
winsock-tuner
nic-driver-tweaker
tcp-ack-optimizer
windows-11-networking
low-latency-network
```

Optional broader discovery topics for the public launch:

```text
windows-10
windows-11
network-diagnostics
network-optimization
dotnet
wpf
```

## Suggested About settings

- **Website:** leave empty until an official project page exists.
- **Releases:** private pre-releases are automated from semantic-version tags; stable releases remain disabled until public-release gates pass.
- **Packages:** disabled unless a packaging workflow needs them.
- **Discussions:** enable only when the repository becomes public and moderation is available.
- **Issues:** keep private-team use during development; add templates before public beta.
- **Wiki:** unnecessary while versioned documentation lives in `docs/`.

## Repository status text

Use this near the top of the README until a safe public build exists:

> **Status: pre-alpha development. Read-only inventory and diagnostics are under construction; live network mutations remain disabled.**

## Public-launch checklist

- Replace planning status with accurate installation and support information.
- Publish only signed artifacts.
- Choose and add a project license.
- Add security, privacy, contribution, and code-of-conduct policies as appropriate.
- Add screenshots from the real application; do not use mockups as product evidence.
- Publish the supported Windows/hardware matrix and known limitations.
- Remove external binaries, PCAP files, personal reports, nested repositories, and unlicensed reference material.
- Verify every performance claim with a reproducible method and representative results.
- Keep CI build/test and Dependabot green; add SBOM generation before public release.
- Review exported diagnostics for IP addresses, MAC addresses, hostnames, and other sensitive data.

## Positioning line

> Inspect first. Change deliberately. Measure the result. Roll back exactly.
