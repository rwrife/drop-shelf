# Drop Shelf 0.1.0 release candidate

This technical prerelease packages the local-first Drop Shelf MVP implemented through the following tracked work. Issue #7 supplies the packaging automation and records the physical checks that remain release blockers; its closure is not a claim that those manual checks passed:

- [#1 — Bootstrap the cross-platform solution and CI quality gates](https://github.com/rwrife/drop-shelf/issues/1)
- [#2 — Implement the canonical shelf domain and versioned local store](https://github.com/rwrife/drop-shelf/issues/2)
- [#3 — Deliver native inbound and outbound drag/drop for files, text, and URLs](https://github.com/rwrife/drop-shelf/issues/3)
- [#4 — Build the accessible docked shelf interaction](https://github.com/rwrife/drop-shelf/issues/4)
- [#5 — Add global reveal, tray/menu-bar, and native shell integration](https://github.com/rwrife/drop-shelf/issues/5)
- [#6 — Complete privacy controls, export, cleanup, and state recovery](https://github.com/rwrife/drop-shelf/issues/6)
- [#7 — Package and verify the Windows and macOS release candidate](https://github.com/rwrife/drop-shelf/issues/7) — automated packaging/evidence complete; target-host verification gaps listed below

## Artifacts

- Self-contained Windows x64 ZIP
- Unsigned Windows x64 MSIX packaging-evidence artifact
- Self-contained macOS Intel and Apple-silicon `.app` ZIPs and DMGs
- SHA-256 checksum lists
- Platform-specific SPDX 2.3 third-party NuGet dependency inventories
- Per-platform verification records from the tagged workflow

## Security and privacy posture

Drop Shelf remains offline/local-first: no accounts, telemetry, network listener, cloud service, updater, or passive clipboard history. Explicitly staged file items remain references to their original paths. The app does not implicitly copy, move, delete, upload, or modify source files.

## Important release-candidate limitations

- Windows artifacts have no Authenticode/MSIX signature. The MSIX cannot be normally installed until signed with a trusted certificate matching its publisher identity.
- macOS apps use ad-hoc signing only. They are not Developer ID signed, notarized, or stapled.
- Real Explorer/Finder/browser/editor/mail drag/drop workflows remain untested on physical Windows/macOS hosts.
- Narrator and VoiceOver keyboard flows remain untested and block a generally available release.
- Native shortcut, tray/menu-bar, login, open/reveal, mixed-DPI, monitor topology, install, and uninstall flows remain manually untested.

See the [installation guide](install-and-uninstall.md), [packaged smoke procedure](packaged-smoke-test.md), and [release checklist](release-checklist.md) before using or redistributing these development artifacts.
