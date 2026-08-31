# Drop Shelf

> Local-first desktop utility for Windows and macOS that provides an always-on-top temporary drag-and-drop shelf for moving files, text, and URLs between apps without rearranging windows.

## Overview

Drop Shelf is a small desktop surface that appears at a screen edge or via a global shortcut. Drop files, selected text, or links onto it, switch applications, and drag the items back out where they are needed. It is designed for awkward cross-window transfers: attaching several files to a message, collecting references while researching, or moving mixed content between full-screen applications.

Drop Shelf is offline-first and does not require an account, synchronization service, browser extension, or AI service.

## Motivation

Dragging between overlapping or full-screen windows often becomes a choreography of minimizing, resizing, and reopening applications. Clipboard managers help with pasteable content, but they do not provide an explicit visual staging area for mixed files, text, and URLs. Drop Shelf keeps a short-lived, user-curated handoff surface visible without turning it into a permanent document manager.

## Target users

- People who frequently move attachments between Finder/Explorer and mail, chat, or web apps
- Researchers and writers collecting a small set of links, quotes, and source files
- Designers and developers transferring artifacts between full-screen tools
- Keyboard and assistive-technology users who need an alternative to long pointer drags

## Concrete use cases

1. Drop three files from Explorer/Finder onto the shelf, open a mail compose window, then drag all three into the message.
2. Collect a URL and two selected text excerpts while researching, review their source labels, then drag or copy them into notes.
3. Stage screenshots from one desktop space and retrieve them from another without changing the original files.
4. Use the keyboard picker to focus the shelf, select an item, and copy or open it without precise pointer movement.

## Intended workflow

1. Launch Drop Shelf; it stays in the tray/menu bar with no shelf visible.
2. Reveal the shelf using a configurable shortcut or screen-edge trigger.
3. Drag files, text, or URLs onto it. Each item shows its type, source application when available, and age.
4. Switch to the destination and drag items out, copy them, open them, or remove them.
5. Unpinned items expire after a configurable interval or when the session ends. Pinned items remain until explicitly removed.

File items are references to their original paths by default; Drop Shelf does not silently copy or move files. If a source disappears, the item is marked unavailable. A later, explicit “managed copy” mode may copy selected files into the app data directory, with visible storage usage and cleanup controls.

## MVP features

- Always-on-top shelf that docks to a screen edge and can collapse to a small handle
- Native inbound and outbound drag/drop for files, plain text, and URLs
- Multi-select, reorder, remove, copy, open, and reveal-in-folder actions
- Global reveal/hide shortcut and tray/menu-bar controls
- Clear item provenance, age, missing-file state, and expiration behavior
- Session restore plus explicit pinning for selected items
- Search-free keyboard navigation, high-contrast focus states, and screen-reader labels
- Local JSON/SQLite persistence with exportable settings and shelf metadata
- Windows 10/11 and current supported macOS builds

## Non-goals

- Passive clipboard monitoring or a searchable clipboard history
- Cloud sync, collaboration, accounts, or mobile sharing
- Replacing Finder, Explorer, a download manager, or a permanent knowledge base
- Editing file contents, rich documents, or images
- Automatically uploading, moving, deleting, or duplicating source files
- Capturing passwords or hidden clipboard formats

## Privacy, permissions, and data storage

- **Offline by default:** no telemetry, account, remote API, or network listener.
- **Explicit collection:** only items the user drops or explicitly pastes into the shelf are recorded; the app does not monitor clipboard history.
- **File behavior:** original paths and display metadata are stored locally. Files are not copied or moved unless a future managed-copy command is explicitly chosen.
- **Local data:** settings and shelf metadata live under `%LOCALAPPDATA%/DropShelf` on Windows and `~/Library/Application Support/DropShelf` on macOS. The architecture uses a versioned store, atomic writes, transactional creation of schema version 2, and a transactional migration from schema version 1.
- **Permissions:** the selected Windows `RegisterHotKey` and macOS Carbon `RegisterEventHotKey` implementations do not require administrator/root or macOS Accessibility access. Drop Shelf does not direct users to broad Accessibility settings for this shortcut backend.
- **Cleanup:** unpinned items expire by policy, users can clear all metadata immediately, and uninstall documentation identifies the app-data directory.
- **Export:** users can export settings and shelf metadata to a documented JSON format. Exported file references do not include file contents.

## Accessibility

The shelf must be fully usable without drag gestures: global reveal, logical tab order, keyboard multi-selection, copy/open/remove commands, visible focus, and contextual help. Controls require accessible names and state announcements. The UI must support Windows high contrast, macOS increased contrast, reduced motion, 200% scaling, and screen readers. Edge-trigger behavior is optional and must not be the only way to reveal the app.

## Current status

**Accessible shelf interaction and native-shell policy/composition are implemented; target-host verification and packaging remain in progress.** The repository contains a pinned .NET 8 solution, a UI-free validated shelf domain, versioned SQLite persistence with v1-to-v2 migration, schema-v1 JSON metadata export, an Avalonia docked shelf with inbound/outbound drag wiring, keyboard and pointer commands, deterministic focus policy, accessible card metadata and live status, native open/reveal actions, tray/menu composition, login policy, and architecture-boundary tests. No installer or signed/notarized package is claimed yet.

## Core domain and local store

The canonical `ShelfItem` supports three explicit payloads: a file/directory reference, normalized plain text, or an absolute HTTP, HTTPS, or file URL. Items have stable caller-supplied IDs, UTC creation/last-used timestamps, pinned state, and dense zero-based ordinals. `ShelfSession` deterministically adds, reorders, removes, pins, expires, and refreshes file availability through its read-only `IFileSystem` boundary. An unpinned item expires when its last-used time is exactly at or before the retention boundary; pinned items are exempt.

Inputs are treated as untrusted. Text is newline-normalized and limited to 65,536 characters; display/source labels, paths, URLs, item counts, and exports also have explicit limits. Validation failures carry a typed error code and field. URL credentials, malformed URLs, and schemes other than HTTP(S)/file are rejected. File items persist only their original qualified path and optional kind/size/modified/availability metadata. Core exposes no copy, move, delete, open, networking, telemetry, account, or clipboard-monitoring service.

`SqliteShelfStore` stores the complete session and settings in a local SQLite database using atomic transactions. SQLite schema version 2 is created on first use; valid schema-v1 databases migrate transactionally with the default shortcut and retain their shelf items and settings. A future schema fails explicitly; malformed databases or invalid/inconsistent rows fail the complete load and never return a partial shelf. The store does not access or mutate referenced source files.

Default settings are: right dock edge, 24-hour retention, start-at-login off, reduced motion off, high contrast off, and global shortcut Ctrl+Alt+Space. The in-app Settings section offers four bounded shortcut choices and an explicit launch-at-login toggle. Retention is configurable from one minute through 30 days. Settings may be persisted independently in one SQLite transaction without replacing shelf items.

## JSON metadata export schema

Metadata export is UTF-8 JSON with a maximum encoded size of 16 MiB and schema version `1`:

```json
{
  "schemaVersion": 1,
  "exportedAt": "2026-01-02T03:04:05+00:00",
  "settings": {
    "dockEdge": "right",
    "retentionSeconds": 86400,
    "startAtLogin": false,
    "reduceMotion": false,
    "highContrast": false
  },
  "items": []
}
```

Each item contains its common metadata and only the fields for its declared kind: `text` for text; `url` and optional `title` for URLs; or `path`, `fileKind`, optional `sizeBytes`/`modifiedAt`, and `availability` for file references. Other kind-specific fields are null. Import re-runs all domain limits and kind consistency checks, rejects unknown or duplicate members and unknown schema versions, requires `exportedAt`, and is capped at 1,000 items. File bytes are never read, embedded, or exported. Schema version 1 predates the global-shortcut setting, so exports omit it and imports retain the safe default shortcut. Exports can contain sensitive text and full local paths, so they should be protected like the source metadata.

### Milestones

1. Core item model, expiration policy, and durable local store
2. Cross-platform application skeleton and CI
3. Inbound/outbound mixed-content drag/drop
4. Shelf interaction, accessibility, and native integrations
5. Privacy controls, export, recovery, and tests
6. Windows/macOS packaging and first release candidate

## Development quickstart

Install the .NET 8 SDK selected by `global.json` (currently 8.0.423), then run:

```bash
git clone https://github.com/rwrife/drop-shelf.git
cd drop-shelf
dotnet restore DropShelf.sln
dotnet format DropShelf.sln --verify-no-changes --no-restore
dotnet build DropShelf.sln --configuration Release --no-restore
dotnet test DropShelf.sln --configuration Release --no-build --no-restore
```

The solution separates the UI-free core, infrastructure, Windows and macOS adapters, and Avalonia app under `src/`. Tests under `tests/` exercise domain policies, hostile/oversized inputs, SQLite reopen/corruption/version behavior, architecture and privacy boundaries, and the empty shell with Avalonia's synthetic headless backend. The exact local verification commands are:

```bash
dotnet restore DropShelf.sln
dotnet format DropShelf.sln --no-restore
dotnet format DropShelf.sln --verify-no-changes --no-restore
dotnet build DropShelf.sln --configuration Release --no-restore
dotnet test DropShelf.sln --configuration Release --no-build --no-restore
```

GitHub Actions runs the formatting, Release build, and test gates on its configured Windows and macOS runners.

Headless tests verify the shell composition without opening a native window. They are not a substitute for manual Windows/macOS launch, accessibility, drag/drop, or destination-application testing.

Platform packaging will use a Windows self-contained ZIP/MSIX path and a signed/notarizable macOS `.app`/DMG path. Signing and notarization credentials are not expected for ordinary local development.

## Drag/drop architecture (issue #3)

Inbound drag data is captured as the UI-free `InboundDropPayload` contract in Core and converted atomically into canonical `ShelfItem` values. Multi-format precedence is explicit and deterministic:

1. A non-empty file list wins and creates one file-reference item per path in source order.
2. Otherwise an explicit native URL format creates one URL item.
3. Otherwise plain text creates one text item, except that a complete HTTP, HTTPS, or file URL in text is canonicalized as a URL item.
4. With none of those formats, the drop is rejected.

The selected format is converted completely before `ShelfSession.AddRange` mutates the session. Empty, unsupported, malformed, oversized, over-capacity, or partly invalid payloads therefore produce no partial shelf items. The Avalonia window renders accepted items with type and safe display name; it never shows a full file path by default. Rejections render a bounded user-facing status that does not echo untrusted content.

Core owns `INativeDragDropAdapter`, `InboundDropPayload`, and `NativeOutboundPayload`; it has no Avalonia or operating-system dependency. `DropShelf.Platform.Windows` maps the Windows data-object identifiers `FileDrop`, `UnicodeText`, and `UniformResourceLocatorW`. `DropShelf.Platform.macOS` maps the pasteboard identifiers `public.file-url`, `NSFilenamesPboardType`, `public.utf8-plain-text`, and `public.url`. File arrays retain caller selection order. The platform assemblies retain dictionary-shaped synthetic boundaries for hostile contract tests. In the running app, Avalonia 11.3's `DataTransfer`, `DataTransferItem`, universal file/text formats, and platform URL formats form the native host bridge to its Windows and macOS drag backends; no separate COM or AppKit implementation is claimed.

The current Avalonia inbound handler accepts storage-item paths, explicit native URLs, and text supplied by an explicit drop gesture. URL-shaped text is recognized by Core. Rendered shelf cards expose an explicit pointer drag gesture that builds a live Avalonia transfer and invokes `DragDrop.DoDragDropAsync`; when the shelf contains at least two file or directory items, an aggregate “Drag all N files” handle transfers those items in current session order without adding general selection or reorder UX. File references are resolved through Avalonia storage APIs before the drag starts; if any selected reference cannot be resolved, no partial drag starts and a path-free error is shown. The app does not monitor the clipboard, contact a network, emit telemetry, use accounts, or read/copy/move/delete referenced file contents.

Synthetic and headless tests verify inbound and outbound format mapping, live-transfer construction, precedence, order, atomic failures, safe UI status/rendering, and unchanged source paths and bytes. Real Windows/macOS host compatibility has not been manually tested: no real Explorer, Finder, browser, text-editor, COM, or AppKit drag was executed. See the [compatibility matrix](docs/drag-drop-compatibility.md); every manual result remains explicitly **Untested**.

## Accessible shelf interaction (issue #4)

Shelf cards expose type, a bounded safe display label, optional source hint, age, pinned/selected state, and file availability without displaying payload text, URL details, or full paths by default. Cards are focusable toggle controls with list-item automation metadata. Pointer or keyboard users can select one or many items, reorder the selected items as an ordered group, copy, open, reveal, pin, remove, clear, collapse, and expand. Ctrl+A selects all, Ctrl+C copies, Enter opens, Delete removes, P pins/unpins, Alt+Arrow reorders, and Escape collapses; all commands also have visible buttons, including Move up and Move down.

The view-model owns deterministic focus targets after add, remove, clear, collapse, and expand, plus polite, path-free announcements. Copy/open/reveal cross explicit app-action boundaries; open and reveal use validated native adapters, while copy remains an explicit recoverable unavailable action. Failed actions retain selection and do not expose payloads or full paths. The item region scrolls, text wraps, the toolbar wraps, controls have a 44-logical-pixel minimum target, state is never color-only, and no interaction uses animation. A geometry policy clamps expanded and collapsed bounds into the current monitor work area for every dock edge after resolution or topology changes.

Loading, expired, unavailable, empty, and recoverable-error states have concise guidance, with a visible retry action for loading failures. Automated coverage and the keyboard-only/manual assistive-technology record are in the [accessibility checklist](docs/accessibility-checklist.md). Narrator and VoiceOver checks are explicitly marked unavailable because development occurred on Linux; no physical target-platform result is claimed.

## Native shell integration (issue #5)

Application composition installs a persistent Avalonia `TrayIcon`/native menu with show-or-hide and quit commands before attempting the persisted optional global shortcut. The visible in-app Settings section exposes a bounded shortcut picker/apply command and an explicit launch-at-login toggle. Shortcut conflict or an unavailable backend does not close the window, disable drag/drop, or remove tray/menu access. Reconfiguration registers a replacement before releasing the prior shortcut. The selected native shortcut APIs require neither administrator/root elevation nor macOS Accessibility access, so the application does not prompt users to grant that broad permission.

Windows uses `RegisterHotKey`/`UnregisterHotKey` on a contained message thread; `WM_HOTKEY` dispatches reveal/hide back to Avalonia's UI thread. Open uses shell execution and reveal uses `explorer.exe /select,` with separate argument-list values. Launch at login is opt-in and reversible through the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value; it installs no service. macOS uses Carbon `RegisterEventHotKey` and an application event handler, `/usr/bin/open`/`open -R` for open/reveal, and `SMAppService.mainAppService` for reversible login registration without a helper service. Tray/menu behavior uses Avalonia's documented `TrayIcon` abstraction over the Windows notification area and macOS status item.

Open and reveal accept only qualified local paths or absolute HTTP(S)/file URLs without credentials. File targets are checked immediately before dispatch. Missing or malformed targets produce a visible generic message containing neither the full path, URL, nor private payload text. Source files are never copied, moved, deleted, or modified. Startup restores the recorded toggle state but does not mutate OS login registration; only deliberate toggle interaction calls the native adapter, and only successful native changes are persisted. Failure restores the prior control/recorded state with a generic message. Native errors remain recoverable optional-capability failures.

Monitor recovery reacts to screen-topology and render-scale changes, uses the selected target screen's scale instead of stale window scale, and guards against recursive repositioning. Topology changes re-dock and clamp expanded or collapsed bounds into a reachable work area after disconnected displays, mixed DPI/scaling, and invalid restored coordinates. Linux tests cover geometry, adapter mapping, target validation, transactional shortcut replacement, permission gating, and host orchestration. No Windows/macOS manual execution is claimed; the [native shell compatibility matrix](docs/native-shell-compatibility.md) marks target-host results **Untested**.

## Contributing

Work is tracked in GitHub issues and should land through focused pull requests with reproducible verification. Avoid claims about platform behavior that have not been exercised on that platform.

## License

MIT — see [LICENSE](LICENSE).
