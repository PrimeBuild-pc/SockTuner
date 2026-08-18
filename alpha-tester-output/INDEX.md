# Capability archive

Redacted `--probe` reports, split one file per adapter model. This is the reference for
which hardware SockTuner has real capability data for, and therefore which keywords its
catalog is characterised against. Regenerate with `python tools/build-probe-archive.py`.

Naming: `<vendor>-<model>-<driver version>.json`. A model seen in more than one slot
collapses to a single entry; the richer record wins when a model appears in several reports.
Raw reports are kept under `reports/` for provenance.

`capabilities` carries the structured driver constraints (valid values, min/max/step,
default) that the tuning surface uses. Reports captured before schema 12 have only
`ndisProperties`; re-running the probe on that hardware upgrades the entry.

## Physical adapters

| Vendor | Model | Driver | NDIS keywords | Structured capabilities | OS |
| --- | --- | --- | ---: | ---: | --- |
| Intel | Intel(R) Dual Band Wireless-AC 3168 | 19.51.38.2 | 19 | 19 | 10.0.26200.0 |
| Intel | Intel(R) Ethernet Controller I226-V | 2.1.5.7 | 21 | 21 | 10.0.26200.0 |
| MediaTek | MediaTek Wi-Fi 7 MT7925 Wireless LAN Card | 5.7.0.4669 | 20 | — (pre-schema-12) | 10.0.26200.0 |
| Realtek | Realtek 8852CE WiFi 6E PCI-E NIC | 6001.16.172.0 | 13 | — (pre-schema-12) | 10.0.26200.0 |
| Realtek | Realtek Gaming 2.5GbE Family Controller | 1125.28.20.1224 | 28 | — (pre-schema-12) | 10.0.26200.0 |
| Realtek | Realtek PCIe 2.5GbE Family Controller | 1125.21.903.2024 | 30 | — (pre-schema-12) | 10.0.26200.0 |

## Virtual and filter adapters

Kept for completeness; these are not tuning targets.

| Vendor | Model | Driver | NDIS keywords | Structured capabilities |
| --- | --- | --- | ---: | ---: |
| Hyper-V | Hyper-V Virtual Ethernet Adapter | — | 0 | 18 |
| TAP-Windows | TAP-Windows Adapter V9 | 9.27.0.0 | 4 | 4 |

## Coverage

- 6 physical adapter model(s) across 3 vendor(s): Intel, MediaTek, Realtek.
- 77 distinct NDIS keywords observed.

### Gaps worth filling

Hardware with no report yet, roughly in order of how common it is among the target users:

- Intel I219 and I225 (I226 is covered; I225 has known errata worth capturing)
- Realtek RTL8111 (RTL8125 is covered)
- Killer / Qualcomm Atheros E2500 and E3100, common on gaming boards
- Broadcom, and Aquantia/Marvell 2.5–10GbE
- Intel AX200 / AX210 / BE200 Wi-Fi (AC 3168 is covered)

### Adding a report

1. Run `SockTuner.exe --probe` on the machine; it writes a redacted report to the Desktop.
2. Drop the file into this folder, or pass its folder to the build script.
3. Run `python tools/build-probe-archive.py` to file it, split it per model and refresh this index.

Reports contain no machine name, IP addresses, routes or full MAC addresses; the vendor OUI
prefix and driver identity are kept deliberately, since they are the point of the archive.
