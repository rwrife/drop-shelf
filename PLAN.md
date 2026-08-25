# Drop Shelf implementation plan

## Scope

The MVP is a local cross-platform desktop utility that gives users one temporary, explicit staging surface for files, plain text, and URLs. It must accept and originate native drag operations, remain useful without elevated or accessibility permissions, expose keyboard equivalents for every drag-centric action, and never mutate source files implicitly.

## Architecture

```text
DropShelf.App (Avalonia MVVM shell)
├── DropShelf.Core
│   ├── ShelfItem domain model (FileReference, Text, Url)
│   ├── shelf/session commands and expiration policy
│   ├── canonical drag payload conversion
│   └── store, clock, filesystem, and platform interfaces
├── DropShelf.Infrastructure
│   ├── SQLite/versioned persistence
│   ├── atomic settings and JSON export
│   └── thumbnail/metadata cache with bounded storage
├── DropShelf.Platform.Windows
│   ├── global shortcut and tray integration
│   ├── native drag/drop format adapter
│   └── Explorer open/reveal integration
└── DropShelf.Platform.macOS
    ├── global shortcut and menu-bar integration
    ├── pasteboard/drag format adapter
    └── Finder open/reveal integration
```

`DropShelf.Core` is UI-free and treats OS handles and pasteboard formats as adapter inputs. The core owns item identity, ordering, pin/expiry behavior, missing-file transitions, and safe serialization. Native adapters translate OS drag payloads into canonical items and back into formats accepted by destination applications.

### Data model

- `ShelfItem`: stable ID, kind, display label, created/last-used timestamps, pinned state, ordinal, source hint, and kind-specific payload
- `FileReferencePayload`: original path, file/directory kind, optional size/modified snapshot, and availability state
- `TextPayload`: normalized plain text with bounded length and optional source application label
- `UrlPayload`: absolute HTTP(S) or file URL plus display title when user-provided
- `ShelfSession`: item ordering and expiration policy
- `AppSettings`: dock edge, shortcut, expiry duration, startup behavior, motion/contrast preferences

Secrets and hidden clipboard formats are outside the data model. The store schema is versioned and migration-tested. File contents are not part of the default store.

## Technology choices

- **.NET 8 / C#:** mature cross-platform runtime, native interop support, and deterministic core testing.
- **Avalonia UI:** one accessible Windows/macOS view layer while allowing native platform adapters. WPF is rejected because it would make macOS a separate product.
- **MVVM:** isolates shelf behavior from rendering and enables headless view-model tests.
- **SQLite:** transactional ordering/session persistence and explicit schema migrations without requiring a service.
- **xUnit:** fast unit and integration tests for core policies and storage.
- **Native Win32 and AppKit adapters:** used only where Avalonia abstractions do not preserve outbound drag formats, global shortcuts, tray/menu-bar behavior, or reveal-in-folder semantics.

No AI dependency is planned; it would not improve the core transfer workflow.

## Milestones and dependency order

### M1 — Skeleton and quality gates

Create the solution, projects, formatting/analyzer settings, test harness, and Windows/macOS CI matrix. Establish architecture boundaries before platform code.

### M2 — Core domain and persistence

Implement canonical item types, validation/size bounds, ordering, pinning, expiry, missing-file checks, SQLite migrations, atomic settings, and documented JSON metadata export.

### M3 — Drag/drop vertical slice

Accept files, plain text, and URLs; render canonical cards; originate standards-compatible outbound drags; prove behavior with adapter contract tests and manual matrices for representative destination apps.

### M4 — Shelf UX and native shell integration

Add edge docking/collapse, multi-selection, keyboard commands, global reveal shortcut, tray/menu bar, open/reveal actions, monitor/DPI handling, and permission onboarding.

### M5 — Privacy, resilience, and accessibility

Add clear-all, expiry controls, export/import validation, corruption recovery, bounded caches, diagnostics with redacted paths, screen-reader verification, reduced motion, high contrast, and keyboard-only task tests.

### M6 — Packaging and release candidate

Produce reproducible Windows ZIP/MSIX and macOS app/DMG workflows, checksums, uninstall/data-removal docs, smoke-test checklist, and release notes. Signing/notarization may remain a documented downstream step when credentials are unavailable.

## Testing strategy

### Automated

- Unit tests for payload validation, URL parsing, ordering, pinning, expiry boundaries, and missing-file transitions
- Property/fuzz tests for untrusted drag text and JSON import bounds
- SQLite migration and crash-recovery integration tests in isolated temporary directories
- Adapter contract tests using synthetic canonical/native payload fixtures
- View-model tests for selection, commands, keyboard flow, and focus restoration
- Static analysis, formatting, build, and test gates on Windows and macOS CI runners

### Platform/manual

A checked-in matrix will record actual results—never assumptions—for inbound and outbound transfer with:

- Explorer/Finder
- a browser
- a plain-text editor
- a mail or equivalent attachment target
- multiple monitors and common DPI/scaling combinations
- VoiceOver and Narrator keyboard-only flows

Manual evidence must distinguish tested OS/API behavior from behavior inferred from documentation.

## Packaging and distribution

- **Windows:** self-contained `win-x64` ZIP first; MSIX packaging after app identity, shortcut, update, and uninstall behavior are validated.
- **macOS:** universal or per-architecture `.app` bundles and DMG; document ad-hoc local builds separately from Developer ID signing/notarization.
- **Release artifacts:** checksums, SPDX-compatible license inventory, privacy statement, versioned JSON schema, and known platform limitations.
- No auto-updater in the MVP; users choose when to install releases.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Outbound drag payloads differ between apps and OS versions | Canonical adapter boundary, fixture tests, and a destination compatibility matrix using real runs |
| A file moves or disappears while staged | Store references only, re-check before action, mark unavailable, never silently recreate or delete |
| Always-on-top shelf obstructs work | Collapsible handle, configurable edge, keyboard hide, per-monitor placement, persisted geometry validation |
| Edge triggers conflict with OS gestures | Disabled by default until configured; global shortcut and tray/menu bar remain complete alternatives |
| Sensitive text is retained unexpectedly | Explicit-drop-only design, visible expiry, clear-all, bounded retention, no passive clipboard listener |
| Global shortcut permission differs by platform | Native adapter and clear just-in-time explanation; app remains useful without the optional shortcut |
| Cross-platform UI hides native accessibility defects | Keyboard-first acceptance criteria plus actual Narrator/VoiceOver smoke checks |

## Explicit non-goals

- Passive clipboard history or clipboard synchronization
- Cloud accounts, LAN transfer, collaborative shelves, or subscription services
- Rich-text fidelity across arbitrary applications
- File editing, file conversion, content indexing, or permanent archival
- Automatic source-file moves, cleanup, deletion, or managed copying in the initial vertical slice
- Mobile, Linux, browser-extension, and remote-control clients in the MVP
- Claims of universal drag/drop compatibility without measured evidence
