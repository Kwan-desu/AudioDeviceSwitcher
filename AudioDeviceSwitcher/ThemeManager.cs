using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;

namespace AudioDeviceSwitcher
{
    /// <summary>
    /// Builds and maintains the app-wide Windows 11 Fluent design tokens as
    /// <see cref="Application.Current"/> resources, so every window (Mixer, Settings, OSD)
    /// pulls from a single coherent source (Complete + Coherent principle).
    ///
    /// Tokens follow Microsoft's published specs:
    ///   • Geometry  – ControlCornerRadius = 4, OverlayCornerRadius = 8.
    ///   • Type ramp – Caption 12/16, Body 14/20, BodyStrong 14/20, Subtitle 20/28, Title 28/36.
    ///   • Accent    – follows the Windows system accent color.
    ///   • Theme     – Light / Dark aware, mirrors the OS or an explicit user choice.
    ///
    /// Resource keys (use with {DynamicResource ...} so runtime theme swaps repaint live):
    ///   Brushes:  AccentBrush, AccentBrushHover, AccentBrushPressed, OnAccentBrush,
    ///             LayerBrush, CardBrush, CardHoverBrush, StrokeBrush, StrokeStrongBrush,
    ///             TextPrimaryBrush, TextSecondaryBrush, TextTertiaryBrush, DangerBrush.
    ///   Radii:    ControlCornerRadius (4), OverlayCornerRadius (8), CardCornerRadius (8).
    ///   Doubles:  FontSizeCaption(12) FontSizeBody(14) FontSizeBodyLarge(18)
    ///             FontSizeSubtitle(20) FontSizeTitle(28).
    ///   Fonts:    FontFamilyText, FontFamilyDisplay, FontFamilyIcons.
    /// </summary>
    public static class ThemeManager
    {
        public static event Action? ThemeChanged;

        private static bool _isDark = true;
        public static bool IsDark => _isDark;

        private static string _preference = "System";

        private static Color _accent = Color.FromRgb(0x60, 0xCD, 0xFF); // fallback cyan

        /// <summary>Initialize tokens once at startup. Applies the given user preference.</summary>
        public static void Initialize(string preference)
        {
            _preference = preference ?? "System";
            _accent = ReadSystemAccent();
            _isDark = ResolveDark(_preference);

            EnsureStaticTokens();
            ApplyThemeTokens();

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        /// <summary>Change the active theme preference at runtime (System/Light/Dark).</summary>
        public static void SetPreference(string preference)
        {
            _preference = preference ?? "System";
            _accent = ReadSystemAccent();
            _isDark = ResolveDark(_preference);
            ApplyThemeTokens();
            ThemeChanged?.Invoke();
        }

        private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            // Fires on OS theme/accent changes. Only react when following the system.
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
            {
                var newAccent = ReadSystemAccent();
                bool newDark = ResolveDark(_preference);
                if (newAccent != _accent || newDark != _isDark)
                {
                    _accent = newAccent;
                    _isDark = newDark;
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        ApplyThemeTokens();
                        ThemeChanged?.Invoke();
                    });
                }
            }
        }

        private static bool ResolveDark(string preference)
        {
            return preference switch
            {
                "Light" => false,
                "Dark" => true,
                _ => ReadSystemUsesLightTheme() == false
            };
        }

        private static bool? ReadSystemUsesLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var val = key?.GetValue("AppsUseLightTheme");
                if (val is int i) return i != 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] read light theme failed: {ex.Message}");
            }
            return null;
        }

        private static Color ReadSystemAccent()
        {
            // DWM colorization gives the system accent; high byte is alpha we ignore.
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                var val = key?.GetValue("AccentColor");
                if (val is int argb)
                {
                    // Stored as 0xAABBGGRR
                    byte r = (byte)(argb & 0xFF);
                    byte g = (byte)((argb >> 8) & 0xFF);
                    byte b = (byte)((argb >> 16) & 0xFF);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] read accent failed: {ex.Message}");
            }
            return Color.FromRgb(0x60, 0xCD, 0xFF);
        }

        // ── Token application ────────────────────────────────────────────────

        private static void EnsureStaticTokens()
        {
            var r = Application.Current?.Resources;
            if (r == null) return;

            // Geometry (Microsoft: ControlCornerRadius 4, OverlayCornerRadius 8)
            r["ControlCornerRadius"] = new CornerRadius(4);
            r["OverlayCornerRadius"] = new CornerRadius(8);
            r["CardCornerRadius"] = new CornerRadius(8);

            // Type ramp (effective px, matching the Windows type ramp)
            r["FontSizeCaption"] = 12.0;
            r["FontSizeBody"] = 14.0;
            r["FontSizeBodyLarge"] = 18.0;
            r["FontSizeSubtitle"] = 20.0;
            r["FontSizeTitle"] = 28.0;

            // Fonts
            r["FontFamilyText"] = new FontFamily("Segoe UI Variable Text, Segoe UI");
            r["FontFamilyDisplay"] = new FontFamily("Segoe UI Variable Display, Segoe UI");
            r["FontFamilyIcons"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");
        }

        private static void ApplyThemeTokens()
        {
            var r = Application.Current?.Resources;
            if (r == null) return;

            // Accent + its states + text drawn on top of accent.
            SetBrush(r, "AccentBrush", _accent);
            SetBrush(r, "AccentBrushHover", Lighten(_accent, 0.12));
            SetBrush(r, "AccentBrushPressed", Darken(_accent, 0.12));
            SetBrush(r, "OnAccentBrush", PerceivedLuminance(_accent) > 0.6 ? Colors.Black : Colors.White);

            if (_isDark)
            {
                SetBrush(r, "LayerBrush", Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));
                SetBrush(r, "CardBrush", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
                SetBrush(r, "CardHoverBrush", Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                SetBrush(r, "StrokeBrush", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                SetBrush(r, "StrokeStrongBrush", Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                SetBrush(r, "TextPrimaryBrush", Color.FromRgb(0xF5, 0xF5, 0xF5));
                SetBrush(r, "TextSecondaryBrush", Color.FromRgb(0xC8, 0xC8, 0xC8));
                SetBrush(r, "TextTertiaryBrush", Color.FromRgb(0x8A, 0x8A, 0x8A));
                SetBrush(r, "DangerBrush", Color.FromRgb(0xFF, 0x5A, 0x5A));
                // Accent-tinted selected card fills
                SetBrush(r, "AccentCardBrush", WithAlpha(_accent, 0x1C));
                SetBrush(r, "AccentCardHoverBrush", WithAlpha(_accent, 0x2A));
                SetBrush(r, "AccentStrokeBrush", WithAlpha(_accent, 0x46));
                // Solid flyout backings (mixer/OSD frame + popups) — dark glass
                SetBrush(r, "FlyoutFillBrush", Color.FromArgb(0xB0, 0x20, 0x20, 0x20));
                SetBrush(r, "FlyoutPopupBrush", Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B));
                SetBrush(r, "OsdFillBrush", Color.FromArgb(0xE6, 0x16, 0x16, 0x16));
                SetBrush(r, "IconBrush", Color.FromRgb(0xDC, 0xDC, 0xDC));
            }
            else
            {
                SetBrush(r, "LayerBrush", Color.FromArgb(0x08, 0x00, 0x00, 0x00));
                SetBrush(r, "CardBrush", Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
                SetBrush(r, "CardHoverBrush", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
                SetBrush(r, "StrokeBrush", Color.FromArgb(0x14, 0x00, 0x00, 0x00));
                SetBrush(r, "StrokeStrongBrush", Color.FromArgb(0x24, 0x00, 0x00, 0x00));
                SetBrush(r, "TextPrimaryBrush", Color.FromRgb(0x10, 0x10, 0x10));
                SetBrush(r, "TextSecondaryBrush", Color.FromRgb(0x44, 0x44, 0x44));
                SetBrush(r, "TextTertiaryBrush", Color.FromRgb(0x70, 0x70, 0x70));
                SetBrush(r, "DangerBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
                SetBrush(r, "AccentCardBrush", WithAlpha(_accent, 0x22));
                SetBrush(r, "AccentCardHoverBrush", WithAlpha(_accent, 0x33));
                SetBrush(r, "AccentStrokeBrush", WithAlpha(_accent, 0x66));
                // Solid flyout backings (mixer/OSD frame + popups) — light glass
                SetBrush(r, "FlyoutFillBrush", Color.FromArgb(0xD8, 0xF3, 0xF3, 0xF3));
                SetBrush(r, "FlyoutPopupBrush", Color.FromArgb(0xF6, 0xFB, 0xFB, 0xFB));
                SetBrush(r, "OsdFillBrush", Color.FromArgb(0xF0, 0xF6, 0xF6, 0xF6));
                SetBrush(r, "IconBrush", Color.FromRgb(0x40, 0x40, 0x40));
            }
        }

        /// <summary>Accent color for use in C#-created controls (Mixer card factories).</summary>
        public static Color Accent => _accent;

        // ── helpers ──────────────────────────────────────────────────────────

        private static void SetBrush(ResourceDictionary r, string key, Color c)
        {
            if (r[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = c;
            }
            else
            {
                var b = new SolidColorBrush(c);
                r[key] = b;
            }
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        private static Color Lighten(Color c, double amt) => Color.FromRgb(
            (byte)Math.Clamp(c.R + 255 * amt, 0, 255),
            (byte)Math.Clamp(c.G + 255 * amt, 0, 255),
            (byte)Math.Clamp(c.B + 255 * amt, 0, 255));

        private static Color Darken(Color c, double amt) => Color.FromRgb(
            (byte)Math.Clamp(c.R - 255 * amt, 0, 255),
            (byte)Math.Clamp(c.G - 255 * amt, 0, 255),
            (byte)Math.Clamp(c.B - 255 * amt, 0, 255));

        private static double PerceivedLuminance(Color c) =>
            (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    }
}
