using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Flow {
    // Colors are brush resources that XAML reads with DynamicResource, so switching never rebuilds the UI
    // and never interrupts running downloads.
    public static class Theme {
        public static readonly string[] Keys = {
            "Bg", "Surface", "Surface2", "SurfaceHover", "Border", "BorderStrong", "Text", "TextMuted", "TextFaint",
            "OnAccent", "Accent", "AccentHover", "AccentPressed", "AccentSoft", "AccentText",
            "Danger", "DangerSoft", "Success", "SuccessSoft", "Warning", "WarningSoft", "Info", "InfoSoft",
            "Sidebar", "SidebarText", "SidebarMuted", "SidebarActive", "SidebarHover",
            "Focus", "Selection", "InputBg", "InputBorder", "ScrollThumb", "ScrollThumbHover", "Track",
            "SourceYouTube", "SourceInstagram", "SourceTikTok", "SourceOther",
            // Ink on the two plates that high contrast paints with the highlight colour.
            "OnSidebarActive", "OnSurfaceHover"
        };
        // Afiş ızgarası: a solid cobalt rail on a pure white ground, near-black ink, hairline rules.
        // Red is kept for errors only; the Source* colours mark a job's source as a small square key.
        static readonly string[] Light = {
            "#FFFFFF", "#FFFFFF", "#F1F2F4", "#E6E8EC", "#D5D8DE", "#3A3F48", "#111318", "#4A4F5A", "#5F6570",
            "#FFFFFF", "#1F3FD1", "#1A35B3", "#152B91", "#E5EAFC", "#1A36B8",
            "#C4161C", "#FDE8E8", "#1C7A3E", "#E3F3E8", "#8F5200", "#FFF1D6", "#20539E", "#E6EEF9",
            "#1F3FD1", "#FFFFFF", "#C9D3FA", "#142C9E", "#2A49DA",
            "#1F3FD1", "#C9D3FA", "#FFFFFF", "#8C929E", "#B9BEC8", "#8C929E", "#ECEEF1",
            "#C8402A", "#A8336F", "#0F7C80", "#8A6300",
            "#FFFFFF", "#111318"
        };
        // Dark: a lifted charcoal ground, softened ink and quiet rules, so text and lines do not glare.
        static readonly string[] Dark = {
            "#16181C", "#1B1D22", "#23262C", "#2B2F36", "#2A2E35", "#4A505A", "#D3D6DC", "#9CA2AC", "#8B919B",
            "#FFFFFF", "#3D5CF5", "#3654EC", "#2A45CF", "#1F2754", "#8C9CF0",
            "#F28B82", "#3A1D1D", "#79C99A", "#183226", "#E9B45A", "#352A14", "#8DB3E8", "#182638",
            "#1B2E9A", "#E6E9F6", "#AEB9EE", "#0F1D6E", "#24399F",
            "#8090E6", "#2A3A8F", "#1D2025", "#50565F", "#3A3F48", "#555C67", "#23262C",
            "#E0705C", "#D46AA2", "#3FB2B6", "#C9A13B",
            "#E6E9F6", "#D3D6DC"
        };

        public static string Preference { get; private set; }
        public static string Current { get; private set; }   // light | dark | contrast
        public static event Action Changed;
        static readonly List<WeakReference> targets = new List<WeakReference>();
        static bool watching;

        public static bool SystemUsesLight() {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return !(value is int) || (int)value != 0;
                }
            } catch (Exception) { return true; }
        }

        public static string Resolve(string preference) {
            if (SystemParameters.HighContrast) return "contrast";
            if (preference == "light" || preference == "dark") return preference;
            return SystemUsesLight() ? "light" : "dark";
        }

        public static Dictionary<string, Color> Palette(string name) {
            var result = new Dictionary<string, Color>();
            if (name == "contrast") {
                // Follow the user's Windows contrast theme instead of Flow's colors.
                var map = new Dictionary<string, Color> {
                    { "Bg", SystemColors.WindowColor }, { "Surface", SystemColors.WindowColor }, { "Surface2", SystemColors.WindowColor }, { "SurfaceHover", SystemColors.HighlightColor },
                    { "Border", SystemColors.WindowTextColor }, { "BorderStrong", SystemColors.WindowTextColor }, { "Text", SystemColors.WindowTextColor }, { "TextMuted", SystemColors.WindowTextColor }, { "TextFaint", SystemColors.GrayTextColor },
                    { "OnAccent", SystemColors.HighlightTextColor }, { "Accent", SystemColors.HighlightColor }, { "AccentHover", SystemColors.HighlightColor }, { "AccentPressed", SystemColors.HighlightColor }, { "AccentSoft", SystemColors.WindowColor }, { "AccentText", SystemColors.HotTrackColor },
                    { "Sidebar", SystemColors.WindowColor }, { "SidebarText", SystemColors.WindowTextColor }, { "SidebarMuted", SystemColors.WindowTextColor }, { "SidebarActive", SystemColors.HighlightColor }, { "SidebarHover", SystemColors.WindowColor },
                    { "Focus", SystemColors.HighlightColor }, { "Selection", SystemColors.HighlightColor }, { "InputBg", SystemColors.WindowColor }, { "InputBorder", SystemColors.WindowTextColor },
                    { "ScrollThumb", SystemColors.WindowTextColor }, { "ScrollThumbHover", SystemColors.HighlightColor }, { "Track", SystemColors.WindowColor },
                    { "OnSidebarActive", SystemColors.HighlightTextColor }, { "OnSurfaceHover", SystemColors.HighlightTextColor }
                };
                foreach (var key in Keys) result[key] = map.ContainsKey(key) ? map[key] : key.EndsWith("Soft") ? SystemColors.WindowColor : SystemColors.WindowTextColor;
                return result;
            }
            var values = name == "dark" ? Dark : Light;
            for (int i = 0; i < Keys.Length; i++) result[Keys[i]] = (Color)ColorConverter.ConvertFromString(values[i]);
            return result;
        }

        public static void Set(string preference) {
            Preference = preference;
            Current = Resolve(preference);
            var palette = Palette(Current);
            foreach (var reference in targets.ToArray()) {
                var window = reference.Target as Window;
                if (window == null) { targets.Remove(reference); continue; }
                ApplyTo(window, palette);
            }
            var handler = Changed; if (handler != null) handler();
        }

        public static void Attach(Window window) {
            targets.Add(new WeakReference(window));
            ApplyTo(window, Palette(Current ?? Resolve(Preference ?? "system")));
            window.SourceInitialized += (s, e) => SetTitleBar(window);
            if (!watching) {
                watching = true;
                SystemEvents.UserPreferenceChanged += (s, e) => {
                    if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle && e.Category != UserPreferenceCategory.Accessibility) return;
                    var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
                    if (dispatcher != null) dispatcher.BeginInvoke(new Action(() => { if (Resolve(Preference ?? "system") != Current) Set(Preference ?? "system"); }));
                };
            }
        }

        static void ApplyTo(Window window, Dictionary<string, Color> palette) {
            foreach (var pair in palette) {
                var brush = new SolidColorBrush(pair.Value); brush.Freeze();
                window.Resources[pair.Key] = brush;
                window.Resources[pair.Key + "Color"] = pair.Value;
            }
            SetTitleBar(window);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        static void SetTitleBar(Window window) {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int dark = Current == "dark" ? 1 : 0;
            try { if (DwmSetWindowAttribute(handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(handle, 19, ref dark, 4); } catch (Exception) {}
        }

        // WCAG relative-luminance contrast; used by tests to keep text readable in both themes.
        public static double Contrast(Color a, Color b) {
            Func<Color, double> lum = c => {
                Func<byte, double> ch = v => { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
                return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
            };
            double l1 = lum(a), l2 = lum(b);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }
    }
}
