# Private Research Material

This directory contains the original material supplied for SockTuner research. It is deliberately separated from project documentation and future application source code.

> Nothing in this directory is automatically considered correct, safe, licensed for redistribution, or suitable for production use.

## Organization

| Directory | Contents |
| --- | --- |
| `notes/` | Networking theory, NDIS command notes, and saved text references |
| `links/` | Windows shortcuts and web links |
| `scripts/diagnostics/` | Adapter discovery, network diagnostics, gaming tests, and endpoint discovery scripts |
| `scripts/tuning/` | TCP/IP, MTU, NIC, interrupt, power, offload, and registry tuning scripts |
| `projects/` | Complete research projects such as GameNetAnalyzer and bufferbloat material |
| `tools/` | Third-party or bundled utilities, binaries, manuals, and tool collections |

## Handling rules

- Treat scripts as candidate ideas, not authoritative recommendations.
- Do not execute bundled binaries as part of SockTuner development or normal operation.
- Validate settings against Microsoft/vendor documentation, installed-driver capabilities, and repeatable tests.
- Preserve provenance and review licenses before reusing any code or data.
- Do not publish nested `.git` directories, executables, archives, PCAP files, personal reports, or unknown-license material.
- Move only independently validated and original conclusions into `docs/` or the future application source tree.
