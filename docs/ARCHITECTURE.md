# Architecture

PaneWeaver is a Windows Forms notification-area process with two event hooks and one serialized STA transaction broker.

## Components

### Window event hook

`SetWinEventHook` watches Explorer `CabinetWClass` creation and show events. Each HWND is claimed once. If another visible Explorer window exists, the new window receives a layered alpha value of zero and a DWM cloak while its Shell destination becomes available.

This is an out-of-process accessibility hook; PaneWeaver does not inject a DLL into Explorer.

### Keyboard hook

A low-level keyboard hook recognizes `Win + E`. When Explorer already exists, the keystroke is consumed and PaneWeaver requests a native tab directly. Shift arms one bypass transaction so users can still request a separate window.

### Shell COM enumeration

`Shell.Application.Windows()` exposes `IWebBrowser2` entries for File Explorer tabs. PaneWeaver reads the captured window's location from `LocationURL`, with a Shell folder fallback for virtual locations.

Before asking Explorer for a new tab, PaneWeaver records the global Shell window count. Explorer appends the new tab as another Shell COM entry, allowing the broker to address that exact entry even if the user changes the active tab.

### Native tab command

The Explorer descendant window class `ShellTabWindowClass` accepts the internal new-tab `WM_COMMAND` value `0xA21B` on currently supported Windows 11 builds. This command is not a documented public API and may require maintenance after Windows feature updates.

### Transaction broker

All capture-to-tab operations execute on one background STA thread:

1. Resolve the captured destination.
2. Validate or replace the primary Explorer HWND.
3. Snapshot the Shell COM count.
4. create one native tab.
5. Identify and navigate the appended COM entry.
6. Close the hidden source window only after success.

If any required step fails, the original extended style is restored, the DWM cloak is removed, and the normal Explorer window is shown.

## Safety invariants

- Never close a captured Explorer window before its destination exists in a confirmed tab.
- Never navigate an arbitrary active tab.
- Never use clipboard contents or simulated address-bar input.
- Never require elevation.
- Never replace the default Windows folder association.
- Always restore a captured window on uncertainty.
