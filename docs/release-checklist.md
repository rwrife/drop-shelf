# Release checklist

A release-candidate tag may publish unsigned/ad-hoc artifacts as a clearly labeled technical prerelease. Promotion to a generally available release is blocked until every target-platform item below has an actual result. “Untested,” synthetic adapter coverage, source inspection, and documented API behavior are not passes.

## Reproducible source and automated gates

- [ ] Tag points to the reviewed commit on `main`.
- [ ] Release workflow records successful restore, formatting, Release build, and full test suite on Windows and macOS.
- [ ] Windows ZIP and MSIX are present; MakeAppx pack/unpack and smoke mode pass.
- [ ] Intel and Apple-silicon `.app` ZIPs and DMGs are present; native-runner app smoke mode passes.
- [ ] `DropShelf-windows-SHA256SUMS.txt` and `DropShelf-macos-SHA256SUMS.txt` match uploaded assets.
- [ ] Both platform-specific third-party inventory files are valid JSON using SPDX 2.3 and list restored NuGet packages.
- [ ] Release notes link the completed GitHub issues and exact workflow run.
- [ ] Signing status is explicit in release notes, package status files, and install docs.

## Windows physical release gates

- [ ] Clean ZIP extract and launch as a standard user.
- [ ] Signed MSIX install, launch, upgrade, and uninstall as a standard user. **Current blocker:** no trusted production signing certificate; the generated MSIX is unsigned.
- [ ] Inbound and outbound Explorer, browser, plain-text editor, and mail/attachment drag tests recorded.
- [ ] Shortcut registration, conflict fallback, tray access, login toggle, open/reveal, mixed scaling, and monitor disconnect recorded.
- [ ] Clear/uninstall/data-removal procedure verified without changing referenced source files.
- [ ] Windows high contrast, 200% scaling, enlarged text, and reduced-motion checks recorded.
- [ ] Full keyboard flow with Narrator recorded, including card type/label/state/position and live announcements. **Current release gap/blocker:** no Windows Narrator host result is available.

## macOS physical release gates

- [ ] Developer ID signed, notarized, and stapled Intel and Apple-silicon app/DMG artifacts verified. **Current blocker:** CI uses ad-hoc signing only and has no Developer ID/notarization credentials.
- [ ] Clean DMG install, first launch, upgrade, and uninstall as a standard user on both architectures.
- [ ] Inbound and outbound Finder, browser, plain-text editor, and mail/attachment drag tests recorded.
- [ ] Shortcut registration, conflict fallback, menu-bar access, login toggle, open/reveal, Retina/mixed scaling, and monitor disconnect recorded.
- [ ] Clear/uninstall/data-removal procedure verified without changing referenced source files.
- [ ] Increased contrast, enlarged text, and reduced-motion checks recorded.
- [ ] Full keyboard flow with VoiceOver recorded, including card type/label/state/position and live announcements. **Current release gap/blocker:** no macOS VoiceOver host result is available.

## Privacy and safety review

- [ ] No account, telemetry, remote service, updater, network listener, or passive clipboard collection was introduced.
- [ ] Dragged paths/text and imported JSON remain treated as untrusted and bounded.
- [ ] User-facing/package logs omit payload text and full local paths by default.
- [ ] No implicit source-file copy, move, delete, upload, or mutation occurs.
- [ ] App-data cleanup is scoped to the documented Drop Shelf directory.

## Release classification

- **Technical prerelease:** permitted when automated gates pass and all signing/manual gaps are stated prominently.
- **General availability:** prohibited while any physical, Narrator, VoiceOver, signing, or notarization gate above is unchecked.
