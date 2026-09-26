#!/usr/bin/env python3
"""Rebuild alpha-tester-output/ as a per-model archive from compatibility reports.

Development-time maintenance script; not part of the application or its build. Reports are read
from the archive itself plus any extra folders given on the command line, filed under reports/
for provenance, split one file per adapter model, and summarised in INDEX.md.

    python tools/build-probe-archive.py [extra-report-folder ...]
    python tools/build-probe-archive.py --check
"""
import collections
import glob
import hashlib
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
CLASSIFICATION_FIELDS = ("areas", "risk", "tradeOff", "rejected", "areasDisplay", "riskBadge", "evidence")


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


def report_archive_name(path, report):
    name = os.path.basename(path)
    if not name.lower().startswith("socktuner-compatibility"):
        return name
    stem, extension = os.path.splitext(name)
    stem = re.sub(r"-[0-9a-f]{12}$", "", stem, flags=re.I)
    content = json.dumps(report, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    return f"{stem}-{hashlib.sha256(content).hexdigest()[:12]}{extension}"


def load_reports(extra_sources):
    reports = []
    for source in [ARCHIVE, REPORTS, *extra_sources]:
        patterns = ("socktuner-probe-*.json", "SockTuner-compatibility*.json")
        for path in sorted({path for pattern in patterns for path in glob.glob(os.path.join(source, pattern))}):
            try:
                with open(path, encoding="utf-8") as handle:
                    report = json.load(handle)
                reports.append((path, report_archive_name(path, report), report))
            except (OSError, ValueError) as error:
                print("  skipped", os.path.basename(path), error)
    return reports


def load_archive_entries():
    entries = {}
    for path in glob.glob(os.path.join(ARCHIVE, "*.json")):
        try:
            with open(path, encoding="utf-8") as handle:
                entry = json.load(handle)
            entries[entry["archiveKey"]] = entry
        except (KeyError, OSError, ValueError):
            pass
    return entries


def preserve_characterisations(entries, previous_entries):
    """Keep reviewed catalog metadata when an older raw report still says 'uncharacterised'."""
    for key, entry in entries.items():
        previous = previous_entries.get(key, {})
        old_caps = {item["keyword"].casefold(): item for item in previous.get("capabilities", [])}
        for capability in entry["capabilities"]:
            if "not yet characterised" not in capability.get("tradeOff", ""):
                continue
            old = old_caps.get(capability["keyword"].casefold())
            if not old or "not yet characterised" in old.get("tradeOff", ""):
                continue
            for field in CLASSIFICATION_FIELDS:
                if field in old:
                    capability[field] = old[field]


def build(extra_sources, write=True):
    if write:
        os.makedirs(REPORTS, exist_ok=True)

    # Read everything before deleting anything: a raw report dropped into the archive root must
    # not be removed before it has been read. Existing derived entries carry reviewed catalog
    # metadata that older raw reports could not know yet.
    reports = load_reports(extra_sources)
    previous_entries = load_archive_entries()
    entries = {}
    for path, archive_name, report in reports:
        snapshot = report.get("snapshot", report)
        system = snapshot["system"]
        caps_by_desc = collections.defaultdict(list)
        for capability in (snapshot.get("adapterCapabilities") or []):
            caps_by_desc[capability["interfaceDescription"]].append(capability)

        for adapter in snapshot["adapters"]:
            ndis = adapter.get("ndisProperties") or []
            caps = []
            seen_keywords = set()
            reported_capabilities = adapter.get("capabilities") or caps_by_desc.get(adapter["description"], [])
            for capability in reported_capabilities:
                if capability["keyword"] not in seen_keywords:
                    caps.append(capability)
                    seen_keywords.add(capability["keyword"])
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
                    "sourceReport": archive_name,
                },
                "adapter": {k: v for k, v in adapter.items() if k != "ndisProperties"},
                "ndisProperties": ndis,
                "capabilities": caps,
            }

    preserve_characterisations(entries, previous_entries)
    if not write:
        return entries

    for path, archive_name, _ in reports:
        target = os.path.join(REPORTS, archive_name)
        if os.path.abspath(path) != os.path.abspath(target):
            shutil.copy2(path, target)
        if os.path.dirname(os.path.abspath(path)) == os.path.abspath(ARCHIVE):
            os.remove(path)
    for stale in glob.glob(os.path.join(ARCHIVE, "*.json")):
        os.remove(stale)
    for key, entry in sorted(entries.items()):
        with open(os.path.join(ARCHIVE, key + ".json"), "w", encoding="utf-8", newline="\n") as handle:
            json.dump(entry, handle, ensure_ascii=False, indent=2)
    return entries


def render_index(entries):
    physical = {k: v for k, v in entries.items() if not v["isVirtual"]}
    virtual = {k: v for k, v in entries.items() if v["isVirtual"]}
    keywords = set()
    for entry in physical.values():
        keywords.update(item["keyword"] for item in entry["ndisProperties"])
        keywords.update(item["keyword"] for item in entry["capabilities"])

    lines = [
        "# Capability archive",
        "",
        "Privacy-safe compatibility reports, split one file per adapter model. This is the reference for",
        "which hardware SockTuner has real capability data for, and therefore which keywords its",
        "catalog is characterised against. Regenerate with `python tools/build-probe-archive.py`.",
        "",
        "Naming: `<vendor>-<model>-<driver version>.json`. A model seen in more than one slot",
        "collapses to a single entry; the richer record wins when a model appears in several reports.",
        "Raw reports are kept under `reports/` for provenance.",
        "",
        "`capabilities` carries the structured driver constraints (valid values, min/max/step,",
        "default) that the tuning surface uses. Older probe reports may have only",
        "`ndisProperties`; creating a new compatibility report on that hardware upgrades the entry.",
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
        "1. In SockTuner, open Preferences → Help improve SockTuner → Create compatibility report.",
        "2. Review the JSON, then drop it into this folder or pass its folder to the build script.",
        "3. Run `python tools/build-probe-archive.py` to file it, split it per model and refresh this index.",
        "4. Run `python tools/build-probe-archive.py --check` to verify regeneration produces no diff.",
        "",
        "Current reports contain no persistent device identifiers, network addresses, user paths,",
        "machine name or exact timestamps. Hardware model and driver identity are kept deliberately.",
    ]
    return "\n".join(lines) + "\n"


def write_index(entries):
    with open(os.path.join(ARCHIVE, "INDEX.md"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(render_index(entries))


def check_archive(entries):
    expected_names = {key + ".json" for key in entries}
    actual_names = {os.path.basename(path) for path in glob.glob(os.path.join(ARCHIVE, "*.json"))}
    failures = sorted(expected_names ^ actual_names)
    for key, entry in sorted(entries.items()):
        path = os.path.join(ARCHIVE, key + ".json")
        expected = json.dumps(entry, ensure_ascii=False, indent=2)
        try:
            with open(path, encoding="utf-8") as handle:
                actual = handle.read()
        except OSError:
            continue
        if actual != expected:
            failures.append(key + ".json")
    with open(os.path.join(ARCHIVE, "INDEX.md"), encoding="utf-8") as handle:
        if handle.read() != render_index(entries):
            failures.append("INDEX.md")
    if failures:
        print("archive differs from regenerated output:", ", ".join(sorted(set(failures))))
        return False
    print("archive matches regenerated output")
    return True


if __name__ == "__main__":
    check = "--check" in sys.argv[1:]
    sources = [arg for arg in sys.argv[1:] if arg != "--check"]
    if check and sources:
        raise SystemExit("--check does not accept extra report folders")
    built = build(sources, write=not check)
    if check:
        raise SystemExit(0 if check_archive(built) else 1)
    write_index(built)
    for key, entry in sorted(built.items()):
        print(f"  {key}.json  ndis={len(entry['ndisProperties'])} caps={len(entry['capabilities'])}")
    print(f"{len(built)} archive entries")
