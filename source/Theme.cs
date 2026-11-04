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
            "Focus", "Selection", "InputBg", "InputBorder", "ScrollThumb", "ScrollThumbHover", "Track"
        };
        static readonly string[] Light = {
            "#F3F5F6", "#FFFFFF", "#F0F4F5", "#E6EDF0", "#DCE3E7", "#9EAEB8", "#15232C", "#4F606B", "#66767F",
            "#FFFFFF", "#0A7A70", "#08665E", "#06544D", "#DDF1ED", "#07645C",
            "#B42318", "#FDECEA", "#17694A", "#E3F4EC", "#8A5300", "#FFF4DB", "#1F5F99", "#E6F0FA",
            "#112833", "#E8F0F3", "#A3B7C1", "#21404D", "#1A3541",
            "#0FA193", "#B7E4DD", "#FFFFFF", "#A7B6BF", "#B8C4CB", "#8E9DA6", "#E3EAED"
        };
        static readonly string[] Dark = {
            "#0E151A", "#161F25", "#1C272E", "#233039", "#28363F", "#4B5D68", "#E6EDF1", "#A1B0B9", "#8797A1",
            "#04211D", "#33C2AC", "#52D0BC", "#27A592", "#133833", "#5FD6C3",
            "#FF9C94", "#3A1B19", "#76D6A8", "#15352A", "#F2C265", "#3A2D12", "#8CC2F5", "#16293B",
            "#0A1115", "#E6EDF1", "#94A8B3", "#1B2D36", "#15232A",
            "#52D0BC", "#1F5B52", "#11191E", "#3C4D57", "#3B4A53", "#56666F", "#26333B"
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
                    { "ScrollThumb", SystemColors.WindowTextColor }, { "ScrollThumbHover", SystemColors.HighlightColor }, { "Track", SystemColors.WindowColor }
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
