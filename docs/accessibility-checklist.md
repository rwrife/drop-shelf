# Shelf accessibility checklist

Date: 2026-08-28
Build under test: source build for issue #4
Host available: Linux (automated/headless checks only)

This document records results without treating automation as a substitute for a physical assistive-technology check.

## Keyboard-only task checklist

| Task | Keyboard path | Automated result | Physical result |
|---|---|---|---|
| Reach the shelf | Tab into the shelf; the empty drop surface or first card is the deterministic target | Pass | Linux desktop manual run not performed |
| Select one item | Tab to a card, Space | Pass | Not performed |
| Select many items | Space on additional cards, or Ctrl+A for all | Pass | Not performed |
| Reorder selected items | Alt+Arrow or activate Move up / Move down | Pass; multi-selection retains shelf order while moving as a group | Not performed |
| Copy | Ctrl+C or Tab to Copy and press Enter/Space | Pass; injectable action boundary and private recoverable failure are covered | Native clipboard integration deferred to issue #5 |
| Open | Enter or activate Open | Pass; injectable action boundary covered | Native open integration deferred to issue #5 |
| Reveal | Activate Reveal | Pass; injectable action boundary covered | Native reveal integration deferred to issue #5 |
| Pin/unpin | P or activate Pin | Pass | Not performed |
| Remove | Delete or activate Remove | Pass; next surviving card is the deterministic focus target | Not performed |
| Clear | Activate Clear | Pass; focus target becomes the empty drop surface | Not performed |
| Collapse/expand | Escape or activate Collapse; then activate Expand | Pass; focus moves to the reachable 44×44 expand button and restores to the prior card | Not performed |
| Recover from an action failure | Retry the action after the visible path-free message | Pass | Not performed |

## Screen reader smoke checks

| Platform / assistive technology | Result | Notes |
|---|---|---|
| Windows 10/11 + Narrator | **Unavailable — not tested** | Current host is Linux. No Windows VM or physical Windows machine with audio/input access was available. |
| macOS + VoiceOver | **Unavailable — not tested** | Current host is Linux. No Mac with audio/input access was available. |

The headless suite verifies card names omit full paths and payload text, list-item roles and set position, selected/pinned/missing states, toolbar names, a polite live status region, deterministic focus targets, and useful loading/expired/unavailable/recoverable-error messages. It does not verify spoken output, announcement timing, platform accessibility-tree bridges, keyboard layout differences, or real native open/reveal/copy behavior.

## Visual and layout smoke checks

| Requirement | Repository evidence | Physical result |
|---|---|---|
| 200% scaling and text expansion | Wrapped card/status text, scrolling item region, wrapping command panel, no fixed card height, and 44 logical-pixel minimum controls | Not physically tested on Windows/macOS |
| Windows high contrast | System theme brushes and native controls; no color-only state or custom fixed-color focus treatment | **Unavailable — not tested** on Windows |
| macOS increased contrast | System theme/native controls and text state labels | **Unavailable — not tested** on macOS |
| Reduced motion | Shelf state changes use no animation | Pass by inspection; OS setting not physically tested |
| Monitor topology/resolution changes | Pure geometry tests cover every dock edge and clamp expanded/collapsed bounds into a replacement work area | Physical hot-plug/topology test not performed |

## Follow-up physical smoke procedure

On each target platform, launch a Release build, enable the platform contrast and 200% scaling settings, enlarge system text, enable reduced motion, and repeat every keyboard task above. Then repeat with Narrator or VoiceOver, confirming the card type, safe label, source (when present), age, selected state, pinned state, missing state, position, live add/remove/collapse announcements, and recovery message. Disconnect or resize a secondary monitor while expanded and collapsed and verify the shelf/handle remains visible and keyboard reachable.
