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

The current UI exposes read-only inventory, read-only diagnostics, and a read-only setting catalog.

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

Pushing a semantic version tag such as `v0.2.0-alpha.1` runs `.github/workflows/release.yml`. It builds and tests, publishes a self-contained `win-x64` single-file app (native libraries self-extract at runtime), creates a ZIP and SHA-256 file, uploads the artifact, and creates a GitHub pre-release.

Release safety requirements:

- the repository must still be private and named `PrimeBuild-pc/SockTuner`;
- only a tag pushed by the repository owner can publish;
- protect the `v*` tag namespace so only maintainers can create release tags;
- configure the `private-release` GitHub environment with required reviewers;
- store signing secrets in that protected environment, not as unprotected repository secrets.

Unsigned builds receive `UNSIGNED-PREVIEW.txt` and must not be redistributed. To sign the executable, configure these protected-environment secrets:

- `WINDOWS_CERTIFICATE`: Base64-encoded PFX;
- `WINDOWS_CERTIFICATE_PASSWORD`: PFX password.

Stable/public release policy remains blocked until signing, installer, update verification, and the release checklist are complete.
