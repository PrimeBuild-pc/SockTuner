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

## CI

`.github/workflows/ci.yml` runs on pushes and pull requests to `main`:

1. restore;
2. formatting verification;
3. Release build;
4. unit tests;
5. test-result upload.

## Private pre-releases

Create an existing semantic-version tag such as `v0.2.0-alpha.1`, then manually run `.github/workflows/release.yml` from the Actions tab and provide that tag. The owner-only workflow validates the tag, requires its commit to belong to `main`, builds and tests, publishes a self-contained `win-x64` single-file app (native libraries self-extract at runtime), creates a ZIP and SHA-256 file, and creates a GitHub pre-release.

Release safety controls:

- the repository must still be private and named `PrimeBuild-pc/SockTuner`;
- only the repository owner can run the publishing jobs;
- release execution is manual, so an untrusted tag push cannot expose signing secrets;
- the `private-release` GitHub environment scopes signing secrets;
- add environment required reviewers and tag rules if the repository billing plan later supports them.

Unsigned builds receive `UNSIGNED-PREVIEW.txt` and must not be redistributed. To sign the executable, configure these protected-environment secrets:

- `WINDOWS_CERTIFICATE`: Base64-encoded PFX;
- `WINDOWS_CERTIFICATE_PASSWORD`: PFX password.

Stable/public release policy remains blocked until signing, installer, update verification, and the release checklist are complete.
