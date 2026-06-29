# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Building

Requires Visual Studio 2022+ with .NET 9.0 SDK. Open `DesktopKeyboard.slnx` in Visual Studio and build.

From the command line:
```
dotnet build DesktopKeyboard.csproj
dotnet run --project DesktopKeyboard.csproj
```

To generate the installer, run `.\build.ps1` (builds the app, creates/reuses a self-signed code-signing cert, signs the exe and MSI, and produces `DesktopKeyboard.Installer\bin\DesktopKeyboard_Setup.msi` via WiX). See [README.md](README.md) for one-time WiX setup.

## Architecture

This is a single-window WPF app (`net9.0-windows`) with no external dependencies. All logic lives in two files:

- **[MainWindow.xaml](MainWindow.xaml)** — UI layout: a frameless, always-on-top, transparent window containing a `Viewbox`-scaled keyboard grid. Keys use `Button.Tag` to carry their virtual key identifier (e.g. `"A"`, `"SPACE"`, `"TOGGLE_SHIFT"`).
- **[MainWindow.xaml.cs](MainWindow.xaml.cs)** — All behavior in one class.

### Key behaviors

**Auto show/hide via UI Automation:** `Automation.AddAutomationFocusChangedEventHandler` listens system-wide for focus changes. `IsEditableTextField` classifies whether the focused element is a writable text field (`ControlType.Edit`, `ComboBox`, or elements with a non-readonly `ValuePattern`/`TextPattern`). The keyboard shows when an editable field is focused and hides after a 200ms debounce timer when focus leaves.

**Non-activating, always-on-top window:** On `OnSourceInitialized`, `WS_EX_NOACTIVATE` is applied via `SetWindowLong` so clicking keys never steals focus from the target app. `SetWindowPos` with `HWND_TOPMOST` keeps it above all other windows.

**Key input via `keybd_event`:** Clicking a key calls `keybd_event` (Win32 P/Invoke) to synthesize the keypress in the previously-focused application. Shift and Ctrl are toggle modifiers — their state is held in `isShiftActive`/`isCtrlActive` and wrapped around each key event.

**Shift visual update:** `UpdateKeys` walks the WPF logical tree recursively to update button `Content` labels when Shift is toggled.

**Three size presets:** The Size button cycles through 600×254, 850×360, and 1200×508. The `Viewbox` in XAML scales all content uniformly, so layout proportions are preserved at all sizes.

**Manual hide:** The minimize button sets `isManuallyHidden = true` and collapses the window. This flag prevents the auto-show logic from re-opening it until the next editable field focus event clears it.
