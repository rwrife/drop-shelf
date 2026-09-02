# Packaged smoke-test procedure

This ledger separates automated package checks from physical desktop checks. A passing CLI smoke test does not establish drag/drop, assistive-technology, shell, or installer compatibility.

## Automated checks

`DropShelf.App --package-smoke-test` is an explicit, non-UI mode. It creates a unique temporary app-owned test directory, writes one bounded text item to SQLite, disposes and reopens the store, verifies restore, clears all metadata, verifies the default empty state, and removes the temporary directory. It does not read, copy, move, delete, or modify a source file and does not print payload text or paths.

Successful output is:

```text
PACKAGED_SMOKE_TEST_PASS add=1 restore=1 clear=1 cleanup=1
```

The Windows package script runs this mode from both an extracted deterministic ZIP and a MakeAppx-unpacked MSIX. The macOS script runs it from the completed, ad-hoc-signed `.app` matching the runner's native architecture. The non-native app is structurally packaged but not executed on that runner.

## Required physical package checks

Run these from a clean standard-user account on each target before promoting a prerelease. Record the exact OS version, architecture, package checksum, destination application/version, result, and evidence link. Do not replace an unavailable result with API reasoning.

| Scenario | Windows ZIP | Signed Windows MSIX | macOS Intel app/DMG | macOS Apple-silicon app/DMG |
|---|---|---|---|---|
| Clean install/extract and first launch | Untested | Blocked: current MSIX is unsigned | Untested | Untested |
| Explicitly add text and URL, quit, relaunch, restore, and clear | Untested | Blocked | Untested | Untested |
| Inbound file drag from Explorer/Finder | Untested | Blocked | Untested | Untested |
| Inbound URL/text from a browser and plain-text editor | Untested | Blocked | Untested | Untested |
| Outbound file drag to mail/attachment target | Untested | Blocked | Untested | Untested |
| Outbound text/URL to browser or editor | Untested | Blocked | Untested | Untested |
| Configure an occupied shortcut; verify visible fallback and tray/menu access | Untested | Blocked | Untested | Untested |
| Enable/disable launch at login without elevation | Untested | Blocked | Untested | Untested |
| Uninstall app only; verify referenced source files and metadata remain | Untested | Blocked | Untested | Untested |
| Remove documented app-data directory; verify source files remain | Untested | Blocked | Untested | Untested |

The stable-release gate and assistive-technology requirements are tracked in [release-checklist.md](release-checklist.md).
