# PaneWeaver 1.1 verification

Tested locally on Windows 11 x64, build 26100, on September 25, 2026. These are observations from a real desktop session, not a universal latency guarantee.

## Final routing run

All checks passed: five new-tab requests each added one native tab, twelve explicit folder requests arrived at the correct paths (including a literal `%20` folder name), three simultaneous opens arrived once each, the independently navigated original tab kept its location, and three external Explorer launches were transferred into the same window.

| Operation | Observed time |
| --- | --- |
| New-tab broker completion, five requests | 125–578 ms |
| New-tab test observer completion | 141–1,050 ms |
| Explicit folder requests, twelve requests | median 640 ms; maximum 1,236 ms |
| Three simultaneous folder requests | broker completions 1,375–1,563 ms; all verified by 1,876 ms |
| External window capture to confirmed transfer | 1,281–1,984 ms |
| External launch through verification, including Windows startup | 2,462–3,132 ms |

The broker and observer measure different boundaries. Observer scheduling and COM verification add overhead; neither metric measures every pixel finishing rendering. Earlier exploratory runs found warm new-tab completions as low as 29–60 ms, but those are not advertised as guaranteed timings. The all-opens-under-one-second target is **not met** for external launches, cold starts, or bursts on this machine.

## Recovery checks

The fault harness deliberately blocks the broker for 5.5 seconds. The independent watchdog restores a captured source around its three-second deadline, and the late continuation leaves that restored window open. Pausing during capture also restores the source. These checks passed.

Noninteractive regression checks cover path case/separators, literal percent names, virtual-folder aliases, the difference between `C:` and `C:\`, and diagnostic observers that throw while recovery is logging.

## Reproduce

```powershell
dotnet run --project tests/PaneWeaver.Tests.csproj -c Release

# Interactive desktop needed. Exit the resident PaneWeaver instance first.
dotnet run --project tests/PaneWeaver.Tests.csproj -c Release -- --live
dotnet run --project tests/PaneWeaver.Tests.csproj -c Release -- --faults
```

Live tests create uniquely named folders below `%TEMP%`, preserve unrelated Explorer windows, and close only windows containing their own fixture paths and blank/virtual tabs. Empty fixture directories are retained for inspection. `--cleanup-test-windows` cleans up abandoned test windows after an interrupted run, subject to the same ownership check.

CI runs the noninteractive checks. Live Explorer timing and recovery checks require a signed-in desktop and are not claimed as CI coverage. Physical Win+E input was not injected: its shared router path was exercised directly. Network drives, third-party shell extensions, other Windows feature builds, process crashes, and every possible manual tab-drag interaction remain outside this test run.
