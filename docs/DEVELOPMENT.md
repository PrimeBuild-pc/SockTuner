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

The current UI exposes read-only inventory and diagnostics plus an allowlisted dry-run change cart. The UI has no apply path and uses a read-only registry store for preview.

## DPI validation

The disposable Italian Windows 11 VM validates the live application at its Hyper-V display driver's native 100% scale. The virtual driver advertises no alternate scale offsets, so the same real WPF `App`/`MainWindow` visual tree is additionally rendered at 125%, 150%, and 200% pixel density for every tab. Evidence remains outside the repository under `C:\VmLab\Runs`; the VM is restored to `Clean-Base` afterward.

## Mutation safety

`WindowsRegistrySettingStore.CreateForIsolatedVm()` is not reachable from the UI. It requires administrator rights and this explicit environment confirmation:

```text
SOCKTUNER_ISOLATED_VM_MUTATIONS=DISPOSABLE-VM-ONLY
```

The variable is an operator confirmation, not proof of virtualization. Use it only inside a disposable Windows VM with a recovery path. CI never enables it.

The writable store has a second, code-level allowlist. Its current Step 6 scope is limited to `SystemResponsiveness` and `NetworkThrottlingIndex`; every other catalog entry remains write-blocked. Both values passed three `read → apply → read → rollback → read` cycles on disposable Windows 10 22H2 and Windows 11 VMs, with exact DWORD restoration and persisted apply/rollback audit entries. Evidence stays outside the repository under `C:\VmLab\Runs`. The typed worker can execute only this VM-gated scope; the normal UI remains read-only.

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
