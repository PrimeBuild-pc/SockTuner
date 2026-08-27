#!/usr/bin/env python3
"""Rebuild alpha-tester-output/ as a per-model capability archive from --probe reports.

Development-time maintenance script; not part of the application or its build. Reports are read
from the archive itself plus any extra folders given on the command line, filed under reports/
for provenance, split one file per adapter model, and summarised in INDEX.md.

    python tools/build-probe-archive.py [extra-report-folder ...]
"""
import collections
import glob
import json
import os
import re
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "alpha-tester-output")
REPORTS = os.path.join(ARCHIVE, "reports")
VENDOR_BY_PCI = {"8086": "Intel", "10EC": "Realtek", "14C3": "MediaTek", "1969": "Qualcomm-Atheros"}
VIRTUAL_VENDORS = ("Hyper-V", "TAP-Windows", "Microsoft")


def vendor_of(desc, component_id):
    match = re.search(r"VEN_([0-9A-Fa-f]{4})", component_id or "")
    if match and match.group(1).upper() in VENDOR_BY_PCI:
        return VENDOR_BY_PCI[match.group(1).upper()]
    for name in ("Intel", "Realtek", "MediaTek", "Qualcomm", "Broadcom", "Marvell", "Killer",
                 "TAP-Windows", "Hyper-V", "Microsoft"):
        if name.lower() in desc.lower():
            return name
    return "Unknown"


def slug(desc, vendor):
    """vendor-model key: drop marketing noise, the duplicated vendor, and the instance suffix."""
    text = re.sub(r"\(R\)|\(TM\)", " ", desc)
    text = re.sub(r"\b(Family|Controller|Adapter|NIC|Card|Wireless LAN)\b", " ", text, flags=re.I)
    text = re.sub(r"#\s*\d+", " ", text)
    text = re.sub(re.escape(vendor), " ", text, flags=re.I)
    text = re.sub(r"\bPCI-?E\b", " ", text, flags=re.I)
    text = re.sub(r"[^A-Za-z0-9]+", "-", text).strip("-")
    return re.sub(r"-+", "-", text) or "unknown"


def load_reports(extra_sources):
    reports = []
    for source in [ARCHIVE, REPORTS, *extra_sources]:
        for path in sorted(glob.glob(os.path.join(source, "socktuner-probe-*.json"))):
            try:
                with open(path, encoding="utf-8") as handle:
                    reports.append((path, json.load(handle)))
            except (OSError, ValueError) as error:
                print("  skipped", os.path.basename(path), error)
    return reports


def build(extra_sources):
    os.makedirs(REPORTS, exist_ok=True)

    # Read everything before deleting anything: a raw report dropped into the archive root must
    # not be removed before it has been read.
    reports = load_reports(extra_sources)
    for path, _ in reports:
        target = os.path.join(REPORTS, os.path.basename(path))
        if os.path.abspath(path) != os.path.abspath(target):
            shutil.copy2(path, target)
        if os.path.dirname(os.path.abspath(path)) == os.path.abspath(ARCHIVE):
            os.remove(path)
    for stale in glob.glob(os.path.join(ARCHIVE, "*.json")):
        os.remove(stale)

    entries = {}
    for path, report in reports:
        snapshot = report["snapshot"]
        system = snapshot["system"]
        caps_by_desc = collections.defaultdict(list)
        for capability in (snapshot.get("adapterCapabilities") or []):
            caps_by_desc[capability["interfaceDescription"]].append(capability)

        for adapter in snapshot["adapters"]:
            ndis = adapter.get("ndisProperties") or []
            caps = caps_by_desc.get(adapter["description"], [])
            if not ndis and not caps:
                continue
            driver = adapter.get("driver") or {}
            desc = adapter["description"]
            vendor = vendor_of(desc, driver.get("componentId", ""))
            key = f"{vendor}-{slug(desc, vendor)}-{driver.get('version') or 'unknown'}"

            existing = entries.get(key)
            # Prefer whichever record carries the richer structured capability data.
            if existing and (len(existing["capabilities"]), len(existing["ndisProperties"])) >= (len(caps), len(ndis)):
                continue
            entries[key] = {
                "archiveKey": key,
                "vendor": vendor,
                "model": re.sub(r"\s*#\s*\d+$", "", desc),
                "isVirtual": vendor in VIRTUAL_VENDORS,
                "driver": driver,
                "capturedFrom": {
                    "schemaVersion": report.get("schemaVersion"),
                    "operatingSystem": system.get("operatingSystem"),
                    "osVersion": system.get("version"),
                    "sourceReport": os.path.basename(path),
                },
                "adapter": {k: v for k, v in adapter.items() if k != "ndisProperties"},
                "ndisProperties": ndis,
                "capabilities": caps,
            }

    for key, entry in sorted(entries.items()):
        with open(os.path.join(ARCHIVE, key + ".json"), "w", encoding="utf-8", newline="\n") as handle:
            json.dump(entry, handle, ensure_ascii=False, indent=2)
    return entries


def write_index(entries):
    physical = {k: v for k, v in entries.items() if not v["isVirtual"]}
    virtual = {k: v for k, v in entries.items() if v["isVirtual"]}
    keywords = set()
    for entry in physical.values():
        keywords.update(item["keyword"] for item in entry["ndisProperties"])
        keywords.update(item["keyword"] for item in entry["capabilities"])

    lines = [
        "# Capability archive",
        "",
        "Redacted `--probe` reports, split one file per adapter model. This is the reference for",
        "which hardware SockTuner has real capability data for, and therefore which keywords its",
        "catalog is characterised against. Regenerate with `python tools/build-probe-archive.py`.",
        "",
        "Naming: `<vendor>-<model>-<driver version>.json`. A model seen in more than one slot",
        "collapses to a single entry; the richer record wins when a model appears in several reports.",
        "Raw reports are kept under `reports/` for provenance.",
        "",
        "`capabilities` carries the structured driver constraints (valid values, min/max/step,",
        "default) that the tuning surface uses. Reports captured before schema 12 have only",
        "`ndisProperties`; re-running the probe on that hardware upgrades the entry.",
        "",
        "## Physical adapters",
        "",
        "| Vendor | Model | Driver | NDIS keywords | Structured capabilities | OS |",
        "| --- | --- | --- | ---: | ---: | --- |",
    ]
    for entry in sorted(physical.values(), key=lambda item: (item["vendor"], item["model"])):
        caps = len(entry["capabilities"])
        lines.append(
            f"| {entry['vendor']} | {entry['model']} | {entry['driver'].get('version', '—')} | "
            f"{len(entry['ndisProperties'])} | {caps if caps else '— (pre-schema-12)'} | "
            f"{entry['capturedFrom'].get('osVersion', '—')} |")

    lines += [
        "",
        "## Virtual and filter adapters",
        "",
        "Kept for completeness; these are not tuning targets.",
        "",
        "| Vendor | Model | Driver | NDIS keywords | Structured capabilities |",
        "| --- | --- | --- | ---: | ---: |",
    ]
    for entry in sorted(virtual.values(), key=lambda item: (item["vendor"], item["model"])):
        lines.append(
            f"| {entry['vendor']} | {entry['model']} | {entry['driver'].get('version', '—')} | "
            f"{len(entry['ndisProperties'])} | {len(entry['capabilities'])} |")

    vendors = sorted({entry["vendor"] for entry in physical.values()})
    lines += [
        "",
        "## Coverage",
        "",
        f"- {len(physical)} physical adapter model(s) across {len(vendors)} vendor(s): {', '.join(vendors)}.",
        f"- {len(keywords)} distinct NDIS keywords observed.",
        "",
        "### Gaps worth filling",
        "",
        "Hardware with no report yet, roughly in order of how common it is among the target users:",
        "",
        "- Intel I219 and I225 (I226 is covered; I225 has known errata worth capturing)",
        "- Realtek RTL8111 (RTL8125 is covered)",
        "- Killer / Qualcomm Atheros E2500 and E3100, common on gaming boards",
        "- Broadcom, and Aquantia/Marvell 2.5–10GbE",
        "- Intel AX200 / AX210 / BE200 Wi-Fi (AC 3168 is covered)",
        "",
        "### Adding a report",
        "",
        "1. Run `SockTuner.exe --probe` on the machine; it writes a redacted report to the Desktop.",
        "2. Drop the file into this folder, or pass its folder to the build script.",
        "3. Run `python tools/build-probe-archive.py` to file it, split it per model and refresh this index.",
        "",
        "Reports contain no machine name, IP addresses, routes or full MAC addresses; the vendor OUI",
        "prefix and driver identity are kept deliberately, since they are the point of the archive.",
    ]
    with open(os.path.join(ARCHIVE, "INDEX.md"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    built = build(sys.argv[1:])
    write_index(built)
    for key, entry in sorted(built.items()):
        print(f"  {key}.json  ndis={len(entry['ndisProperties'])} caps={len(entry['capabilities'])}")
    print(f"{len(built)} archive entries")
