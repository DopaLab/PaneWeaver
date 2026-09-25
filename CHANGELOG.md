# Changelog

All notable changes to PaneWeaver are documented here.

## 1.1.0 — 2026-09-25

- Replace blocking queue and sleep loops with a pumped STA and asynchronous transactions.
- New-tab requests complete on native child creation without Shell COM enumeration.
- Match destination tabs using their native HWND and IShellBrowser, not mutable collection indexes.
- Cache ShellWindows; use typed COM and avoid scanning unchanged registrations.
- Confirm the destination and recheck the source before closing; preserve uncertain transfers.
- Add an independent watchdog, pause cancellation, bounded admission, and HWND destruction tracking.
- Fix double-decoding of percent escapes and the five-second Shift-release bypass.
- Restrict the local command pipe to the current user.
- Add live Explorer checks and timing logs. Cold/external launches can still exceed one second.

## 1.0.0 — 2026-08-09

### Added

- Native File Explorer tab routing for new folder windows
- Exact destination-tab selection through Shell COM entries
- Serialized concurrent-launch handling
- Direct `Win + E` tab creation
- One-time Shift bypass for separate windows
- Fail-open restoration when a route cannot be confirmed
- Per-user installer, Windows startup entry, tray controls, and activity log
- Self-contained Windows x64 release
