# Windows 11 Fluent Design — Research Notes

Source: Microsoft Learn, *Windows 11 design principles* and its signature-experience sub-pages.
Fetched 2026-09-12. These are the authoritative specs the IMPROVEMENT_PLAN should conform to.

- Principles: https://learn.microsoft.com/en-us/windows/apps/design/design-principles
- Elevation & layering: .../signature-experiences/layering
- Materials: .../signature-experiences/materials
- Typography: .../signature-experiences/typography
- Geometry: .../signature-experiences/geometry

---

## 1. The five design principles (and what each means for this app)

| Principle | Microsoft definition | Application to Audio Device Switcher |
|-----------|----------------------|--------------------------------------|
| **Effortless** | Fast, intuitive, focus + precision | One-click switch stays instant; add keyboard nav + hold-to-pick so power users never leave the keyboard. |
| **Calm** | Softer, decluttered, fades into background | Reduce visual noise: fewer hard borders, rely on Mica/Acrylic + subtle stroke instead of heavy fills. |
| **Personal** | Adapts to the user (theme, input, layout) | Follow **system accent** + **light/dark/high-contrast**; customizable hotkeys; device renaming. |
| **Familiar** | Refreshed look, zero learning curve | Use standard Fluent glyphs, sentence casing, native tray menu conventions. |
| **Complete + Coherent** | Consistent across surfaces | One shared token dictionary so Mixer, Settings, OSD, tray all match exactly. |

---

## 2. Materials — corrected guidance (this changes the plan)

Microsoft splits materials into **occluding** (Mica, Acrylic) and **transparent** (Smoke).

- **Mica** — opaque, tinted with the desktop wallpaper color, mode-aware, and **indicates window
  focus (active/inactive) automatically**. Use for **long-lived base layers** (the app's main
  window). → **Settings window** should use Mica.
- **Acrylic** — semi-transparent frosted glass, mode-aware. Windows 11 acrylic is **brighter and
  more translucent**. Explicitly: *"Acrylic is used only for transient, light-dismiss surfaces
  such as flyouts and context menus."* → **Mixer flyout** and **OSD** correctly use Acrylic;
  **tray context menu** should also be Acrylic.
- **Smoke** — dims surfaces beneath a modal; always translucent black (not mode-aware). → Only
  needed if a true modal dialog is added later.

**Implication for the plan:** The app's current use of the legacy `ACCENT_ENABLE_ACRYLICBLURBEHIND`
on the *Settings* window is doubly wrong — Settings is a base layer and should be **Mica**, not
Acrylic. Confirms plan item A1.

---

## 3. Elevation & layering — concrete values

Windows 11 expresses elevation with **shadow + contour (1px stroke)**. Elevation values:

| Surface | Elevation | Stroke |
|---------|-----------|--------|
| Window / Dialog | 128 | 1 |
| Flyout | 32 | 1 |
| Tooltip | 16 | 1 |
| Card | 8 | 1 |
| Control | 2 | 1 |
| Layer | 1 | 1 |

Control state via elevation: **Rest = 2, Hover = 2, Pressed = 1** (pressed drops elevation).

**Two-layer system:** every app has a **base layer** (menus, commands, navigation) and a
**content layer** (the central experience, optionally split into **cards**).

**Implication:** The Mixer's device/app cards are correctly "cards" (elevation 8, 1px stroke).
The pressed-state micro-interaction in the plan (A6) should *lower* elevation on press, matching
the Rest 2 → Pressed 1 pattern. All surfaces keep a 1px contour — the app already does this.

---

## 4. Geometry — exact corner radii

Controlled globally by `ControlCornerRadius` (4px) and `OverlayCornerRadius` (8px).

| Radius | Applies to |
|--------|-----------|
| **8px** | Top-level windows, flyouts, dialogs |
| **4px** | In-page controls: Button, CheckBox, ComboBox, TextBox, ListView |
| **4px** | Bar elements: **ProgressBar, ScrollBar, Slider** |
| **4px** | ToolTip (exception — small) |
| **0px** | Straight edges meeting straight edges; snapped/maximized windows |

**Implication / corrections to current code:**
- Window frames use `8` ✓ (correct).
- Cards currently use `CornerRadius=7` → should be **8** (or a defined card radius), and buttons
  use `5` → should be **4**. The OSD pill uses `12` — acceptable as a custom pill, but the icon
  circle and cards should align to the 4/8 system.
- Slider track uses `2` radius — fine as a bar sub-element, but the design system's bar radius is
  4px; keep visual judgment for a 4px-tall track.

---

## 5. Typography — the official type ramp

Font: **Segoe UI Variable** (axes: weight `wght` 100–700, optical size `opsz` automatic 8–36pt).
Use **Segoe Fluent Icons** for glyphs (not emoji).

| Style | Weight | Size / line-height (epx) |
|-------|--------|--------------------------|
| Caption | Small | 12 / 16 |
| Body | Text | 14 / 20 |
| Body Strong | Text Semibold | 14 / 20 |
| Body Large | Text | 18 / 24 |
| Body Large Strong | Text Semibold | 18 / 24 |
| Subtitle | Display Semibold | 20 / 28 |
| Title | Display Semibold | 28 / 36 |
| Title Large | Display Semibold | 40 / 52 |
| Display | Display Semibold | 68 / 92 |

Best practices:
- **Weights:** Regular for body, **Semibold for titles**. *No Bold, no Italic* — use Semibold for
  emphasis (italics hurt dyslexic readers).
- **Minimum legible:** 14px Semibold / 12px Regular. Nothing smaller.
- **Casing:** **Sentence case for ALL UI text, including titles.**
- **Alignment:** Left by default; center only for text under icons.
- **Truncation:** Prefer clipping + wrap; ellipses when container isn't well-defined.

**Implication / corrections to current code:**
- Good: headers use Segoe UI Variable Display Semibold; body uses Text.
- **Fix casing:** OSD titles are **ALL CAPS** (`"AUDIO PLAYBACK SWITCHED"`, `"VOLUME ADJUSTED"`)
  — violates the sentence-case rule. Change to `"Audio playback switched"` / `"Volume adjusted"`.
- **Fix sub-minimum sizes:** several elements use 12.5 / 13 / 13.5 Regular and 10.5 Semibold
  (OSD label) — the 10.5px is below the 12px floor and should move to 12px. Normalize body text
  to 14/20 where space allows, or 12 Caption for secondary lines.
- Replace emoji in the tray menu with Segoe Fluent Icons glyphs (Familiar principle).

---

## 6. Motion (from principles page)
Reactive, direct, context-appropriate; provides feedback and reinforces spatial paradigms.
Current entrance animations (fade + 8–12px translate, 150–220ms cubic-ease) are on-spec. Keep
durations short (≤300ms) and add directional continuity for drill-in (e.g., device → its apps).

---

## 7. Net changes to IMPROVEMENT_PLAN driven by this research

1. **A1 upgraded:** Settings window must be **Mica** (base layer), not just "supported acrylic".
   Mixer + OSD + tray menu = **Acrylic** (transient). This is now backed by explicit MS guidance.
2. **New item — Geometry normalization:** card radius 7→8, button radius 5→4, align to
   `ControlCornerRadius`/`OverlayCornerRadius` semantics.
3. **New item — Typography compliance:** sentence-case all titles (OSD especially), raise all
   text to the 12px/14px minimums, adopt the named type-ramp sizes as tokens.
4. **A2 tokens** should be named after the official ramp (Caption/Body/BodyStrong/Subtitle/Title)
   and the elevation/stroke system, so the codebase speaks the same language as the docs.
5. **Elevation tokens:** define card (8), flyout (32), control (2/1 pressed) shadow+stroke values
   rather than ad-hoc `#20FFFFFF` borders.
