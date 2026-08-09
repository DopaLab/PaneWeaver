# PaneWeaver verification

Tested on Windows 11 Pro 24H2, build 26100, x64.

## Live Explorer checks

- Single folder launch: created one native tab in the existing Explorer HWND; no extra Explorer window remained.
- Concurrent launch stress: `Beta`, `Gamma`, and `Delta` launched together and arrived once each in the same Explorer window.
- Browse-race check: the original tab was independently navigated to `C:\Windows` while those three requests were processing; it stayed at `C:\Windows` and none of the requested paths crossed.
- `Win+E`: native tab count increased by one while Explorer top-level window count stayed unchanged.
- Shift bypass: the next folder launch remained in a genuinely separate Explorer HWND.
- Published executable smoke test: the self-contained release started successfully without using the development runtime.

The application also uses a fail-open path: if the native tab host or destination tab cannot be confirmed, the captured Explorer window is restored instead of discarded.
