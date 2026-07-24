# SockTuner Development Rules

## Safety

- Never execute network mutations on the development host.
- Never run scripts or binaries under `research/`.
- Mutation tests must use an in-memory/fake platform. Real Windows integration tests must be explicitly opt-in and reserved for a disposable VM.
- Do not restart adapters, change registry/network settings, DNS, MTU, QoS, Winsock, routes, bindings, or power state during development.
- Read-only Windows inventory and diagnostics may run locally.

## Architecture

- C#/.NET 10 LTS, WPF, x64-first Windows 10/11 desktop application.
- Native/API-first; no PowerShell runtime dependency and no arbitrary shell execution.
- One production application project and one test project until a split is justified.
- Normally unelevated UI; typed allowlisted elevated worker for future mutation operations.
- Every writable setting requires snapshot, validation, read-back verification, and exact rollback.

## Workflow

1. Work in a small roadmap phase.
2. Build and run safe tests.
3. Ask the project `supervisor` agent for a read-only phase gate.
4. Fix blocking findings.
5. Commit only after a PASS verdict.
6. Push after the completed set of phases.

## Commands

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```
