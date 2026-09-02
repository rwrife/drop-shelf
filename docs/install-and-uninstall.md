# Install, update, and uninstall

Drop Shelf release-candidate artifacts are development builds. The automated release workflow produces self-contained binaries, but there are currently no production signing credentials and no artifact is described as signed or notarized.

## Target matrix and evidence limits

| Target | Artifact | Build target | Measured desktop compatibility |
|---|---|---|---|
| Windows x64 | `DropShelf-win-x64.zip` | Windows 10/11, x64, self-contained .NET 8 | Automated packaged persistence smoke only; Explorer, destination-app drag/drop, tray, shortcut, Narrator, install, and uninstall remain manually untested |
| Windows x64 | `DropShelf-win-x64.msix` | Windows 10/11, x64, full-trust desktop MSIX | MakeAppx pack/unpack and unpacked-binary smoke only; package is unsigned and cannot be normally installed until signed by a certificate matching its manifest publisher |
| macOS x64 | `DropShelf-macos-x64.app.zip` and `.dmg` | macOS 12 or newer, Intel, self-contained .NET 8 | Bundle structure, ad-hoc signing, DMG creation, and runner-native smoke where architecture matches; Finder, destination-app drag/drop, menu bar, shortcut, VoiceOver, install, and uninstall remain manually untested |
| macOS arm64 | `DropShelf-macos-arm64.app.zip` and `.dmg` | macOS 12 or newer, Apple silicon, self-contained .NET 8 | Same limitation as the x64 package |

“Build target” is not a claim that the complete user workflow has passed on that platform. The measured compatibility ledgers remain in [drag-drop-compatibility.md](drag-drop-compatibility.md), [native-shell-compatibility.md](native-shell-compatibility.md), and [release-checklist.md](release-checklist.md).

## Verify downloads

Each workflow artifact contains a platform-specific `SHA256SUMS` file and the release contains both checksum files. Compare the checksum from a trusted release page before opening a package.

Windows PowerShell:

```powershell
Get-FileHash .\DropShelf-win-x64.zip -Algorithm SHA256
Get-Content .\DropShelf-windows-SHA256SUMS.txt
```

macOS:

```bash
shasum -a 256 DropShelf-macos-arm64.dmg
cat DropShelf-macos-SHA256SUMS.txt
```

The release also includes `DropShelf-windows-third-party-licenses.spdx.json` and `DropShelf-macos-third-party-licenses.spdx.json`, deterministic SPDX 2.3 inventories generated from each platform's restored NuGet dependency metadata.

## Windows ZIP

1. Verify the checksum.
2. Extract the complete `DropShelf` folder to a user-owned location.
3. Run `DropShelf.App.exe` from that folder. Do not move only the executable; its adjacent runtime files are required.
4. Windows may warn because the binary has no Authenticode signature. Treat this as a development build and do not bypass warnings for an artifact from an untrusted source.

Updating is explicit: quit Drop Shelf, verify and extract the newer ZIP to a new folder, and then remove the old application folder. User metadata is retained separately.

## Windows MSIX

The workflow creates and then unpacks `DropShelf-win-x64.msix` with the Windows SDK `MakeAppx` tool and runs the packaged executable's smoke mode. The current MSIX is deliberately **unsigned**. Windows requires an MSIX signature whose certificate subject matches `CN=DropShelf Development`, so this artifact is packaging evidence rather than a generally installable distribution. No production certificate is committed to the repository.

A downstream signer must sign the exact MSIX, publish its new checksum, install the trusted signing certificate through an appropriate organization/user trust process, and re-run the manual release checklist. Do not describe that downstream package as signed without preserving signing verification output.

## macOS app and DMG

The workflow builds separate Intel and Apple-silicon `.app` bundles, applies only an ad-hoc local signature, verifies the bundle with `codesign --verify --deep --strict`, and places each app in a DMG. It does **not** use Developer ID credentials and does **not** submit either artifact for notarization.

1. Choose the package matching the Mac architecture and verify its checksum.
2. Open the DMG and copy `DropShelf-macos-<architecture>.app` to `/Applications` or `~/Applications`, or extract the app ZIP.
3. Expect Gatekeeper to identify the development build as unnotarized. Do not weaken system-wide Gatekeeper policy. A production release requires Developer ID signing, notarization, stapling, and recorded verification.

Updating is explicit: quit Drop Shelf, verify the newer artifact, and replace the prior app bundle. There is no auto-updater.

## Permissions and local data

The chosen Windows `RegisterHotKey` and macOS Carbon `RegisterEventHotKey` shortcut backends do not require administrator/root or macOS Accessibility access. Launch-at-login is opt-in and reversible. Drop Shelf has no account, cloud service, telemetry, network listener, or passive clipboard monitoring.

Metadata stays in:

- Windows: `%LOCALAPPDATA%\DropShelf`
- macOS: `~/Library/Application Support/DropShelf`

File items are references. Clearing or uninstalling Drop Shelf does not delete, move, copy, upload, or modify the referenced source files.

## Uninstall and remove data

1. Quit Drop Shelf from its tray/menu-bar command.
2. Disable **Start at login** before removal when possible.
3. Delete the extracted Windows application folder or macOS app bundle. For a future signed MSIX, uninstall it through Windows Installed apps.
4. To remove local metadata too, delete only the platform app-data directory listed above.

Application removal and metadata removal are intentionally separate. Never delete a referenced source path as part of uninstall or cleanup.
