# PaneWeaver 1.1 architecture

PaneWeaver is an out-of-process Windows Forms tray utility. It does not inject code into Explorer, modify default folder associations, add a toolbar, or simulate address-bar input.

## Scheduling

Window and keyboard hooks stay on the UI thread. Work is admitted to a bounded queue and dispatched to a background STA with a Windows message pump. Async continuations resume on this apartment. A semaphore covers native tab creation only; source discovery, registration waits, and navigation overlap. COM calls themselves remain synchronous and can block when Explorer is unresponsive.

## Exact destination selection

Before issuing Explorer's internal `WM_COMMAND 0xA21B`, snapshot its `ShellTabWindowClass` child HWNDs. Deliver the command with a bounded `SendMessageTimeout`, then identify the new child. If multiple children appear, fail open rather than guess. Resolve that HWND through each candidate browser's `IServiceProvider -> IShellBrowser -> IOleWindow.GetWindow`. Never select a browser by collection index or by whichever tab is active.

`IShellWindows` is cached on the broker apartment and reconnected after errors. Enumeration visits newest entries first, but their HWND—not position—decides identity. Reading the registration count avoids repeatedly scanning unchanged collections; periodic probes also cover a close and an open that leave the count unchanged. Filesystem navigation uses typed dispatch. Filesystem-path checks avoid loading virtual-folder automation during destination verification.

Win+E, the tray action, and `NEWTAB` skip COM after the new native child exists. Their timing measures tab creation, not completion of the Home page's contents.

## Captured windows

Existing Explorer windows are seeded into a handled set. CREATE and SHOW are deduplicated; DESTROY removes state immediately so HWND reuse is not permanently ignored. Captured windows are excluded from target selection even if they report `IsWindowVisible`.

DWM cloaking is attempted first. If Windows refuses cross-process cloaking, a reversible layered-alpha fallback is used. Existing layered windows are left untouched. A separate timer watches the three-second capture budget independently of the COM apartment. Pause, exit, failures, and expiry restore captures. Original styles are preserved, and timeout and close decisions share a per-capture lock.

After navigation, the broker reads back the destination, rechecks that the source still contains exactly one tab at the original location, and only then requests source closure. It retains watchdog ownership until destruction, so an ignored close cannot leave the source hidden forever.

## Boundaries

- Native tab creation is an undocumented Explorer command and may change with Windows updates.
- Out-of-process capture cannot guarantee pre-paint interception or sub-second creation of a window owned by Windows.
- The recovery deadline does not interrupt an in-flight COM call. The independent watchdog can restore the source while the broker is blocked; elapsed deadlines prevent later closure.
- Overlapping user-created tabs or source changes cause conservative fallback. Rare timing ambiguity remains possible with undocumented tab internals; this is not proof of bug-free operation.
- Folder location is preserved; selection, navigation history, custom view state, and arbitrary multi-tab window transfers are not implemented.
- Hard process termination can prevent in-process recovery. Normal exit restores captures.
- Explicit opens fall back to Explorer on uncertainty; a late response can leave an extra tab. No arbitrary tab is closed to hide uncertainty.

## References

- [IShellWindows](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nn-exdisp-ishellwindows)
- [COM identity rules](https://learn.microsoft.com/en-us/windows/win32/com/rules-for-implementing-queryinterface)
- [Navigate behavior](https://learn.microsoft.com/en-us/previous-versions/aa752133(v=vs.85))
