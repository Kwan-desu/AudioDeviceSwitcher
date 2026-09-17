# Audio Device Switcher — Improvement Plan

Target: cleaner, more authentic **Windows 11 Fluent** UI + a deeper, more useful feature set.
Stack unchanged: C# / WPF + WinForms / .NET 10 / `AudioSwitcher.AudioApi.CoreAudio`.

---

## Part A — UI: Cleaner & More Windows 11

### A1. Adopt real Win11 backdrops (Mica / Acrylic) — *highest visual impact*
**Problem:** Windows currently use the Windows 10-era, undocumented
`SetWindowCompositionAttribute` (`ACCENT_ENABLE_ACRYLICBLURBEHIND`). On Windows 11 this
produces a muddy tint, occasional black flashes, and lag on drag.

**Fix:**
- Switch the **Settings** window (persistent) to **Mica** via
  `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW)`.
- Keep **Acrylic** for transient surfaces (Mixer flyout, OSD) but use the *supported*
  `DWMSBT_TRANSIENTWINDOW` backdrop type instead of the legacy accent policy.
- Remove the manual `#EE181818` / `#05FFFFFF` fill hacks once the OS backdrop draws the material.
- Requires Windows 11 build 22621+; keep the legacy path as a runtime fallback for Win10.

**Files:** `MixerWindow.xaml.cs`, `SettingsWindow.xaml.cs`, `OsdWindow.xaml(.cs)`.
New shared helper: `WindowBackdrop.cs` (one place for all DWM interop, deduplicating the
two identical `#region Acrylic Blur Interop` blocks).

### A2. Centralize a theme + design-token resource dictionary
**Problem:** Colors, radii, font families, and the accent `#60CDFF` are duplicated as literals
across every XAML file and in C# (`Color.FromRgb(...)`). Impossible to theme or tweak globally.

**Fix:** Add `Themes/Tokens.xaml` (merged in `App.xaml`) exposing:
- Brushes: `AccentBrush`, `LayerCardBrush`, `LayerCardHoverBrush`, `StrokeBrush`,
  `TextPrimary`, `TextSecondary`, `TextTertiary`, `DangerBrush`.
- Corner radii `4` (controls) / `8` (surfaces) per DESIGN.md.
- Type ramp: Caption 12 / Body 13.5 / Subtitle 15 / Title 20 using **Segoe UI Variable**.
- Replace hardcoded C# brush creation in `MixerWindow` card factories with `FindResource`.

### A3. Follow the Windows **system accent color**
**Problem:** Fixed cyan `#60CDFF` ignores the user's chosen Windows accent.

**Fix:** Read `SystemParameters` / `UISettings.GetColorValue(UIColorType.Accent)` (WinRT) on
startup and on `WM_DWMCOLORIZATIONCOLORCHANGED`, and push it into `AccentBrush`. Falls back to
the current cyan if unavailable.

### A4. Light / Dark / High-Contrast support
**Problem:** DESIGN.md requires it; app is dark-only (`darkMode = 1` hardcoded).

**Fix:**
- Detect OS theme from registry `AppsUseLightTheme` + listen for
  `WM_SETTINGCHANGE("ImmersiveColorSet")`.
- Provide `Tokens.Light.xaml` / `Tokens.Dark.xaml`; swap the merged dictionary at runtime.
- Add a **Theme** dropdown in Settings: System / Light / Dark (persist in `AppSettings.Theme`).
- Honor `SystemParameters.HighContrast` by switching to system brushes.

### A5. Fluent tray context menu
**Problem:** Menu uses emoji (`🎚️ 🔄 ⚙️ ❌`) — visually off-brand vs. the polished windows.

**Fix:** Owner-draw the `ContextMenuStrip` (dark renderer + rounded corners) or move to a small
WPF flyout, using Segoe Fluent Icons glyphs instead of emoji. Match the OSD/Mixer styling.

### A6. Polish pass (small, high-ratio wins)
- Mixer "make default" click target: whole card already works — add a subtle **pressed** scale
  micro-interaction (DESIGN.md motion 150–300ms).
- Add empty-state text in Mixer when no devices/apps ("No active audio apps").
- Align all spacing to the 4/8px grid; a couple of margins (e.g. `16,14,16,14`) are off-grid.
- Give OSD a volume **progress bar** under the label for volume events (not just text).

---

## Part B — Feature Set

### B1. Customizable hotkeys (finish what's started) — *low effort, high value*
**Problem:** `AppSettings.QuickSwitchHotkey` / `OpenMixerHotkey` are stored but
`GlobalHotkeyManager` ignores them and registers hardcoded `Ctrl+Shift+S/M`.

**Fix:** Parse the stored strings into modifiers+vk and register those. Add a
**hotkey capture control** in Settings (click → press combo → stores). Handle registration
failure (combo already taken) with inline feedback.

### B2. Per-device volume/mute in the mixer for *all* selected devices
Currently the mixer shows selected devices but app sessions only for the **default** device.
Add: show app sessions grouped under whichever device is active, and allow expanding a
non-default device to preview its sessions.

### B3. Quick-switch OSD device picker (hold hotkey to choose)
Instead of blind cycling, holding the Quick Switch hotkey shows a small overlay listing
selected devices; release on the highlighted one to switch (Alt-Tab style). Cycling remains
the default tap behavior.

### B4. Device profiles / naming polish
- `DeviceLabels` already exists but there's **no UI to edit labels**. Add inline rename in
  Settings (the tray badge already uses these labels — "AUX 1", etc.).
- Optional per-device default volume applied on switch.

### B5. Auto-switch rules (opt-in)
- "When device X connects, make it default" (e.g., plug in headset → auto-select).
- Uses CoreAudio device-added/state-changed notifications.

### B6. Keyboard accessibility in the Mixer
Arrow keys to move between cards, Enter to set default, +/- to change volume, M to mute.
Currently only Esc is handled. Improves the "Effortless/Familiar" DESIGN principles.

### B7. Volume limiter / max-volume guard (per device)
Optional cap so scroll/hotkey can't blow past a safe level on a given device.

### B8. Housekeeping
- Fix version drift: `csproj` says `1.5.0` but the folder/README say `1.5.2`.
- Extract duplicated DWM interop (A1) — removes ~120 lines of copy-paste.
- Wrap silent `catch { }` blocks with at least debug logging.

---

## Suggested Sequencing

**Phase 1 — Foundation (unlocks everything else)**
1. `WindowBackdrop.cs` helper + Mica/Acrylic migration (A1)
2. `Themes/Tokens.xaml` design tokens (A2)
3. System accent color (A3)

**Phase 2 — Visible polish**
4. Light/Dark/High-Contrast + Settings theme dropdown (A4)
5. Fluent tray menu (A5)
6. Polish pass + OSD progress bar (A6)

**Phase 3 — Features**
7. Customizable hotkeys + capture UI (B1)
8. Device rename UI + per-device default volume (B4)
9. Keyboard nav in mixer (B6)
10. Quick-switch picker overlay (B3), auto-switch rules (B5), volume limiter (B7)

**Phase 4 — Cleanup**
11. Version alignment, interop dedupe, logging (B8)

---

## Risk / Verification Notes
- All UI changes require a Windows 11 machine with the .NET 10 SDK to build
  (`dotnet publish -c Release -r win-x64`) and visually verify — this environment is Linux, so
  I can write the code but cannot run the WPF app here.
- Mica/backdrop APIs need build 22621+; the legacy fallback path must stay for older systems.
- Accent/theme change listeners must marshal to the UI thread (pattern already used in
  `TrayScrollManager`).
