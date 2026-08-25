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
- **Local data:** settings and shelf metadata live under `%LOCALAPPDATA%/DropShelf` on Windows and `~/Library/Application Support/DropShelf` on macOS. The architecture uses a versioned store and atomic writes/migrations.
- **Permissions:** macOS may require Accessibility permission only for an optional global shortcut implementation if the selected native API requires it; basic drag/drop remains useful without that permission. Windows needs no administrator access. Drop Shelf will explain permission purpose before opening OS settings.
- **Cleanup:** unpinned items expire by policy, users can clear all metadata immediately, and uninstall documentation identifies the app-data directory.
- **Export:** users can export settings and shelf metadata to a documented JSON format. Exported file references do not include file contents.

## Accessibility

The shelf must be fully usable without drag gestures: global reveal, logical tab order, keyboard multi-selection, copy/open/remove commands, visible focus, and contextual help. Controls require accessible names and state announcements. The UI must support Windows high contrast, macOS increased contrast, reduced motion, 200% scaling, and screen readers. Edge-trigger behavior is optional and must not be the only way to reveal the app.

## Current status

**Planning scaffold.** This repository currently contains product and implementation documentation only. No application binary, successful build, automated test result, installer, or signed package is claimed yet.

### Milestones

1. Core item model, expiration policy, and durable local store
2. Cross-platform application skeleton and CI
3. Inbound/outbound mixed-content drag/drop
4. Shelf interaction, accessibility, and native integrations
5. Privacy controls, export, recovery, and tests
6. Windows/macOS packaging and first release candidate

## Development quickstart (planned)

The planned stack is .NET 8, C#, Avalonia UI, SQLite, and xUnit. After the project skeleton lands:

```bash
git clone https://github.com/rwrife/drop-shelf.git
cd drop-shelf
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

Platform packaging will use a Windows self-contained ZIP/MSIX path and a signed/notarizable macOS `.app`/DMG path. Signing and notarization credentials are not expected for ordinary local development.

## Contributing

Work is tracked in GitHub issues and should land through focused pull requests with reproducible verification. Avoid claims about platform behavior that have not been exercised on that platform.

## License

MIT — see [LICENSE](LICENSE).
