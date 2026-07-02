# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Building

Requires the .NET 9.0 SDK. There is no XAML/markup — the UI is built in code — so any editor works.

From the command line:
```
dotnet build DesktopKeyboard.csproj
dotnet run --project DesktopKeyboard.csproj
```

Note: the app sets `uiAccess="true"` in [app.manifest](app.manifest). A dev build run from the repo will *not* get uiAccess (Windows requires a signed exe in a trusted location), so auto-show above shell UI and typing into elevated apps only work from the installed build.

To generate the installer, run `.\build.ps1` (publishes framework-dependent ReadyToRun to `publish\`, creates/reuses a self-signed code-signing cert, signs the exe and MSI, and produces `DesktopKeyboard.Installer\bin\DesktopKeyboard_Setup.msi` via WiX). See [README.md](README.md) for one-time WiX setup. Installing once also places the self-signed cert in LocalMachine\Root so uiAccess is honored.

## Architecture

A single-window **Avalonia** app (`net9.0-windows`, Skia renderer) chosen for **GPU-composited transparency** — WPF's `AllowsTransparency` forces a CPU `UpdateLayeredWindow` blit, whereas Avalonia composites the transparent window on the GPU (keypress CPU ~1–2% vs WPF's 3–7%, ~17 MB working set). The UI is built entirely in C# (no AXAML). It references `Microsoft.WindowsDesktop.App` solely to reuse the managed UI Automation client for focus detection (no WPF render stack loads).

Files:
- **[Program.cs](Program.cs)** / **[App.cs](App.cs)** — Avalonia entry point. `ShutdownMode.OnExplicitShutdown` keeps the app alive while the keyboard is collapsed.
- **[MainWindow.cs](MainWindow.cs)** — all behavior + the code-built UI.
- **[Key.cs](Key.cs)** — one keyboard key (Border-based, no control theme): themed background + press-highlight that fades on release + a white glyph with a 1px black outline (four offset copies, no shader effect). Raises `Pressed`/`Released`.
- **[InputLayout.cs](InputLayout.cs)** — Windows keyboard-layout (HKL) service: per-HKL glyph tables (`MapVirtualKeyEx` + `ToUnicodeEx` with the 0x04 no-kernel-state-change flag), foreground-window HKL detection, layout enumeration/switching (`WM_INPUTLANGCHANGEREQUEST`), and locale short names via `GetLocaleInfoW` (`InvariantGlobalization` makes `CultureInfo(langid)` unusable). The `ToUnicodeEx` P/Invoke must keep `CharSet.Unicode` — without it the `char[]` marshals as ANSI and the API overruns the native buffer (heap corruption).
- **[TouchSlider.cs](TouchSlider.cs)** — minimal touch-sized slider for the theme popup (no theme dependency).

### Key behaviors

**Single window, sizes to content:** `SizeToContent.WidthAndHeight` with a `LayoutTransform` `ScaleTransform` for the size presets — keys keep their size and the window grows to fit the side clusters rather than scaling the keys down. A persistent top row holds Esc (top-left, shown only with the body), a centred **mode button** + `⋯` toggle, and the toggle-collapsible theme/layout/close. "Hidden" collapses the body and Esc/chrome, leaving just `[mode] [⋯]`. The window is anchored on the **mode-button centre** (`_modeAnchor`) so it stays put across collapse/expand/resize.

**Composable arrangement:** the body is `[numpad?][nav?][alpha block][nav?][numpad?]` — nav cluster and numpad are independently Off/Left/Right, plus a numpad-only mode, chosen from the layout button's popup panel and persisted (`Nav`/`Numpad`/`NumpadOnly`; the legacy `Layout` dword migrates on load). Clusters are built lazily exactly once and **re-parented** by `ApplyArrangement` (never rebuilt — keys keep their `_allKeys` registration and event wiring).

**Windows-layout-aware keys:** the alphanumeric zone is **positional** — each key is a scan code (`KeyDef.Sc`, tag `SC..`), sent via `KEYEVENTF_SCANCODE` so the receiving thread's own layout translates it, and labelled from `InputLayout.Table(hkl)` for the foreground app's HKL (so a UK/French/German layout shows and types its own arrangement). HKL changes are detected on the UIA thread (focus events + the 1 Hz poll — the poll catches Win+Space with unchanged focus) and marshalled to `OnHklMaybeChanged`. The **LANG key** cycles installed layouts by posting `WM_INPUTLANGCHANGEREQUEST` to the foreground window (async and refusable — relabel optimistically, reconcile 300 ms later). Fixed keys (Enter, nav, numpad, modifiers) stay VK-based; extended keys (RCtrl/RAlt/Apps/nav/Ins/Del/PrtSc/num-slash) always carry scan + `KEYEVENTF_EXTENDEDKEY` via `ExtScan`, otherwise injected right-variants degrade to left ones.

**Auto show/hide via UI Automation:** subscribed under a `CacheRequest` (so each focus event carries cached `ControlType`/`Name`/patterns — no per-focus cross-process calls); classification (`IsEditableTextField`) runs on the UIA thread, then marshals the show/hide decision to the UI thread. Shows on editable focus, hides after a 200 ms debounce. Use Full cache mode, not `None` (None faults the process).

**Non-activating, always-on-top window:** in `OnOpened`, `WS_EX_NOACTIVATE` is applied via `SetWindowLong` and `SetWindowPos`/`HWND_TOPMOST` keeps it on top. The keyboard is dragged manually (the OS move-loop can't drive a no-activate window) using an absolute grab offset from Avalonia pointer coords (`PointToScreen`), which is touch- and DPI-accurate.

**Key input via `SendInput`:** keys fire on **press** and auto-repeat while held (typematic — Backspace/Del clear quickly); modifiers toggle on **release**. `SendWrapped` batches a modifier-wrapped keystroke (VK or scan-code) into one atomic `SendInput`. Modifier behaviour: tap = one-shot (cleared after the next key, wrapped per-key), long-press (300 ms) = **locked** (physically held via a real key-down/up so it acts like a held key; released on close). Both Shifts share one `Mod` (its `Keys` list highlights every key carrying the tag). `_defByTag`/`_allKeys` caches avoid tree walks. Fn is local-only (remaps the number row to F1–F12 and Backspace→Del via `FnMap`, keyed by positional `SC..` tags).

**Theming:** HSV → `ImmutableSolidColorBrush`; `ApplyTheme` sets each key's background, the panel/border, and the mode button. Settings persist to `HKCU\Software\serifpersia\DesktopKeyboard` (debounced 400 ms; flushed on close).

**Diagnostics:** set registry `Diag`=1 (or env `DESKTOPKEYBOARD_DIAG=1`) to log whole-process CPU%/working set/GC heap to `%TEMP%\DesktopKeyboard_perf.log` every 2 s. Off and zero-overhead otherwise.

### Runtime trims
csproj sets framework-dependent ReadyToRun, workstation non-concurrent GC, `InvariantGlobalization`, and feature switches (`UseSystemResourceKeys`, `EventSourceSupport=false`, `HttpActivityPropagationSupport=false`). `EmptyWorkingSet` is called after startup and on explicit Hide to return idle pages to the OS.
