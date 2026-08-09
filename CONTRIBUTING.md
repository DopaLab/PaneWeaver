# Contributing to PaneWeaver

Thanks for helping make File Explorer calmer.

## Before opening an issue

- Check existing issues for the same Windows build and symptom.
- Confirm that native File Explorer tabs work without PaneWeaver.
- Include the exact Windows version from `winver`.
- Mention Explorer customization tools that are installed or running.

## Development setup

PaneWeaver requires Windows 11 and the .NET 8 SDK or newer.

```powershell
dotnet restore
dotnet build -c Release
```

## Pull requests

1. Create a focused branch.
2. Keep unrelated formatting out of the change.
3. Explain the Windows build used for live Explorer testing.
4. Preserve the fail-open rule: never discard a folder request unless the target tab has been confirmed.
5. Run `dotnet build -c Release` before submitting.

Changes involving Explorer window capture, COM entry selection, native commands, or hook lifetimes should include a reproducible manual test plan.

## Design principles

- Native Explorer UI only
- No simulated typing or clipboard navigation
- No administrator requirement
- No telemetry or network dependency
- Restore normal Windows behavior on uncertainty
