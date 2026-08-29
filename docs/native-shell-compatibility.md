# Native shell compatibility matrix

Recorded on 2026-08-29 from the Linux development host. No Windows or macOS shell was available, so all manual platform results are honestly **Untested**. Automated contract tests exercise policy and error mapping with injected native seams; they are not evidence that an OS integration works.

| Platform | Scenario | API basis | Manual result |
|---|---|---|---|
| Windows 10 | Register, invoke, conflict, and reconfigure global shortcut | Win32 `RegisterHotKey`/`UnregisterHotKey` and `WM_HOTKEY` message loop | **Untested** |
| Windows 11 | Show/hide and quit from notification area | Avalonia `TrayIcon` over the Windows notification-area backend (`Shell_NotifyIcon`) | **Untested** |
| Windows 10/11 | Open file/URL and reveal file in Explorer | shell execution and `explorer.exe /select,` with argument-list escaping | **Untested** |
| Windows 10/11 | Enable, disable, and relaunch login item | per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; no service or elevation | **Untested** |
| Windows multi-monitor | Disconnect/reconnect, mixed scaling, invalid restored coordinates | Avalonia `Screens`, working areas, render scaling, and bounded geometry recovery | **Untested** |
| macOS | Register, invoke, conflict, and reconfigure global shortcut | Carbon `RegisterEventHotKey` application event handler; this backend does not require Accessibility access | **Untested** |
| macOS | Show/hide and quit from menu-bar status item | Avalonia `TrayIcon` over the macOS status-item backend (`NSStatusItem`) | **Untested** |
| macOS | Open file/URL and reveal in Finder | `/usr/bin/open`, including `-R` | **Untested** |
| macOS | Enable, disable, and relaunch login item | `SMAppService.mainAppService`; no helper service | **Untested** |
| macOS multi-display | Disconnect/reconnect, Retina scaling, invalid restored coordinates | Avalonia `Screens`, visible working areas, render scaling, and bounded geometry recovery | **Untested** |

Current evidence limitation: the production API bindings and application callbacks are wired, while injected seams map conflicts/errors, preserve the old registration during conflict-safe reconfiguration, exercise the real headless settings controls, contain callback exceptions, and verify success-only login persistence/rollback. They compile and their policy is exercised on Linux, but none of the native calls or callbacks has been executed on Windows or macOS. A runtime API failure maps to an optional-capability error; tray/menu access and the shelf remain usable. The chosen Carbon shortcut backend does not need macOS Accessibility access, so no broad-permission prompt or settings link is exposed.

Automated Linux-host evidence also covers SQLite schema-v1 to schema-v2 shortcut migration, bounded shortcut persistence, complete-batch open/reveal prevalidation and aggregate dispatch, deterministic supported/unsupported host selection, target-screen DPI policy, and the absence of an unnecessary Accessibility prompt for the selected Carbon backend. These tests do not change any manual result above from **Untested**.
