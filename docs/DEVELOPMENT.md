# Development

## Requirements

- Windows 10/11 x64
- .NET 10 SDK
- No external networking tools are required

## Safe local checks

```powershell
dotnet restore SockTuner.sln
dotnet format SockTuner.sln --verify-no-changes
dotnet build SockTuner.sln -c Release --no-restore
dotnet test SockTuner.sln -c Release --no-build
```

The default tests are deterministic/in-memory and do not modify Windows network settings. Do not run material under `research/`.

## Run the application

```powershell
dotnet run --project src/SockTuner/SockTuner.csproj
```

The UI exposes read-only inventory and diagnostics, plus the **Tuning plan** tab: driver-advertised adapter properties filtered by intent preset (latency, bandwidth, power and wake, Wi-Fi radio, VLAN and identity), with preview, apply and exact rollback. Preview always reads through a read-only store; only an explicit apply elevates.

## DPI validation

The disposable Italian Windows 11 VM validates the live application at its Hyper-V display driver's native 100% scale. The virtual driver advertises no alternate scale offsets, so the same real WPF `App`/`MainWindow` visual tree is additionally rendered at 125%, 150%, and 200% pixel density for every tab. Evidence remains outside the repository under `C:\VmLab\Runs`; the VM is restored to `Clean-Base` afterward.

## Mutation safety

Live writes are enabled in the alpha. The `SOCKTUNER_ISOLATED_VM_MUTATIONS` environment gate has been retired; four independent controls replace it:

1. **Alpha consent.** The first apply shows a blocking risk notice covering adapter restarts, connectivity loss, and recovery. Acceptance is stored in preferences as a versioned record (`WriteConsent.CurrentVersion`), so changing the risk text re-prompts.
2. **Elevation.** The UI stays unelevated. Applying launches the same executable as a short-lived elevated worker over a private named pipe and exchanges one typed JSON request. A dismissed UAC prompt is reported as a clean no-op.
3. **Per-setting gating.** Registry-backed catalog settings must appear in `WindowsRegistrySettingStore.WritableSettingIds`. NIC properties have no static allowlist: **the driver is the allowlist**. A NIC change is legal only if the CIM provider still advertises that keyword for that adapter and the value satisfies the driver's own `ValidRegistryValues` or min/max/step — re-read inside the elevated process immediately before writing, never trusted from the caller.
4. **Typed confirmation.** Any change that is `ChangeRisk.High` or `EvidenceLevel.Experimental` additionally requires typing `APPLY`.

Every apply still runs `read → plan → snapshot → apply → read-back verify → audit`, refuses a stale plan, and rolls back in reverse order on failure. NIC writes go through `MSFT_NetAdapterAdvancedPropertySettingData`, so no registry path is ever composed from plan data; affected adapters are then restarted via `MSFT_NetAdapter.Restart()` and checked back to the link state they had beforehand.

Default and CI tests never touch the host. Read-only checks against real drivers are opt-in:

```powershell
$env:SOCKTUNER_LIVE_INVENTORY = '1'; dotnet test SockTuner.sln -c Release
```

## CI

`.github/workflows/ci.yml` runs on pushes and pull requests to `main`:

1. restore;
2. formatting verification;
3. Release build;
4. unit tests;
5. test-result upload.

## Pre-releases

**Pushing a semantic-version tag such as `v0.7.0-alpha.2` ships a release automatically** — treat tag pushes as the release action. `.github/workflows/release.yml` validates the tag, requires its commit to belong to `main`, builds and tests, publishes a self-contained `win-x64` single-file app (native libraries self-extract at runtime), creates a ZIP and SHA-256 file, and creates a GitHub pre-release. Re-running for an existing tag from the Actions tab (workflow dispatch) is also supported.

Release safety controls:

- the workflow only runs for the `PrimeBuild-pc/SockTuner` repository and only the repository owner can trigger the publishing jobs;
- the tag must match strict semver, belong to `main`, and is revalidated for immutability before publishing;
- signing secrets are repository secrets, never exposed to pull-request workflows (CI for PRs runs without secrets).

Unsigned builds include `UNSIGNED-PREVIEW.txt` (SmartScreen warning + SHA-256 verification instructions). To sign the executable, configure these repository secrets:

- `WINDOWS_CERTIFICATE`: Base64-encoded PFX;
- `WINDOWS_CERTIFICATE_PASSWORD`: PFX password.

Dependabot pull requests are squash-merged automatically by `.github/workflows/dependabot-auto-merge.yml` once the required `build-test` check passes (the workflow uses `pull_request_target` because Dependabot-triggered `pull_request` runs get a read-only token; it never checks out PR code).

Stable/public release policy remains blocked until signing, installer, update verification, and the release checklist are complete.
